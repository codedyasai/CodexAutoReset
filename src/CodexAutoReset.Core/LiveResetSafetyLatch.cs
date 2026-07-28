using System.Text;
using System.Text.Json;

namespace CodexAutoReset.Core;

/// <summary>
/// Keeps protocol and unknown live failures fail-closed in memory and, when a
/// path is supplied, across process restarts.
/// </summary>
public sealed class LiveResetSafetyLatch
{
    private const int CurrentMarkerSchemaVersion = 3;
    private const int MaximumCompatibilityRevisionLength = 128;
    private const int MaximumMarkerBytes = 4 * 1024;
    private const string MarkerFileName = "live-safety-block.json";
    private const string MutationAmbiguousOrigin = "mutationAmbiguous";

    private readonly string? durablePath;
    private readonly string? temporaryPath;
    private readonly string? currentCompatibilityRevision;
    private readonly Action? beforePersist;
    private readonly object durabilityGate = new();
    private string? blockedCompatibilityRevision;
    private int blockReason;
    private int durabilityState;

    public LiveResetSafetyLatch()
    {
    }

    public LiveResetSafetyLatch(string durablePath)
        : this(
            durablePath,
            currentCompatibilityRevision: null,
            beforePersist: null)
    {
    }

    public LiveResetSafetyLatch(
        string durablePath,
        string currentCompatibilityRevision)
        : this(
            durablePath,
            ValidateCompatibilityRevision(
                currentCompatibilityRevision,
                nameof(currentCompatibilityRevision)),
            beforePersist: null)
    {
    }

    internal LiveResetSafetyLatch(string durablePath, Action? beforePersist)
        : this(
            durablePath,
            currentCompatibilityRevision: null,
            beforePersist)
    {
    }

    private LiveResetSafetyLatch(
        string durablePath,
        string? currentCompatibilityRevision,
        Action? beforePersist)
    {
        if (string.IsNullOrWhiteSpace(durablePath))
        {
            throw new ArgumentException("live_safety_block_path_invalid", nameof(durablePath));
        }

        this.durablePath = Path.GetFullPath(durablePath);
        if (!string.Equals(
            Path.GetFileName(this.durablePath),
            MarkerFileName,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("live_safety_block_path_invalid", nameof(durablePath));
        }

        temporaryPath = this.durablePath + ".tmp";
        this.currentCompatibilityRevision = currentCompatibilityRevision;
        this.beforePersist = beforePersist;
        var loadedMarker = LoadMarker(this.durablePath, temporaryPath);
        if (loadedMarker is not null)
        {
            blockReason = Encode(loadedMarker.Value.Reason);
            durabilityState = 1;
        }
    }

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public LiveAttemptBlockReason? BlockReason => Decode(
        Volatile.Read(ref blockReason));

    public void BlockProtocolMismatch() => Block(
        LiveAttemptBlockReason.ProtocolMismatch,
        currentCompatibilityRevision);

    public void BlockProtocolMismatch(string compatibilityRevision) => Block(
        LiveAttemptBlockReason.ProtocolMismatch,
        ValidateCompatibilityRevision(
            compatibilityRevision,
            nameof(compatibilityRevision)));

    public void BlockUnknownFailure() => Block(
        LiveAttemptBlockReason.UnknownFailure,
        compatibilityRevision: null);

    internal void Block(LiveAttemptBlockReason reason) => Block(
        reason,
        reason == LiveAttemptBlockReason.ProtocolMismatch
            ? currentCompatibilityRevision
            : null);

    public bool TryClearProtocolMismatch(
        bool compatibilityValidationSucceeded,
        bool hasUnresolvedAttempt) => false;

    public bool TryClearProtocolMismatch(
        string currentCompatibilityRevision,
        bool compatibilityValidationSucceeded,
        bool hasUnresolvedAttempt)
    {
        _ = ValidateCompatibilityRevision(
            currentCompatibilityRevision,
            nameof(currentCompatibilityRevision));
        return false;
    }

    private void Block(
        LiveAttemptBlockReason reason,
        string? compatibilityRevision)
    {
        if (reason is not (LiveAttemptBlockReason.ProtocolMismatch
            or LiveAttemptBlockReason.UnknownFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        lock (durabilityGate)
        {
            if (blockReason == 0)
            {
                blockedCompatibilityRevision =
                    reason == LiveAttemptBlockReason.ProtocolMismatch
                        ? compatibilityRevision
                        : null;
                Volatile.Write(ref blockReason, Encode(reason));
            }

            PersistIfRequired();
        }
    }

    internal void ThrowIfBlocked()
    {
        var reason = BlockReason;
        if (reason is not null)
        {
            throw CreateException(reason.Value);
        }
    }

    internal LiveStateException CreateBlockedException() => BlockReason is { } reason
        ? CreateException(reason)
        : new LiveStateException("live_needs_review");

    private void PersistIfRequired()
    {
        if (durablePath is null || temporaryPath is null)
        {
            return;
        }

        if (durabilityState != 0)
        {
            return;
        }

        try
        {
            beforePersist?.Invoke();
            var directory = Path.GetDirectoryName(durablePath)
                ?? throw new IOException("live_safety_block_path_invalid");
            Directory.CreateDirectory(directory);
            var reason = BlockReason == LiveAttemptBlockReason.ProtocolMismatch
                ? "protocolMismatch"
                : "unknownFailure";
            byte[] bytes;
            if (reason == "protocolMismatch"
                && blockedCompatibilityRevision is not null)
            {
                using var memory = new MemoryStream();
                using (var writer = new Utf8JsonWriter(memory))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber(
                        "schemaVersion",
                        CurrentMarkerSchemaVersion);
                    writer.WriteString("reason", reason);
                    writer.WriteString(
                        "compatibilityRevision",
                        blockedCompatibilityRevision);
                    writer.WriteString(
                        "origin",
                        MutationAmbiguousOrigin);
                    writer.WriteEndObject();
                }

                bytes = memory.ToArray();
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(
                    $"{{\"schemaVersion\":1,\"reason\":\"{reason}\"}}");
            }

            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, durablePath, overwrite: false);
            durabilityState = 1;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            durabilityState = 2;
            throw new LiveStateException("live_safety_block_persist_failed");
        }
    }

    private static LoadedMarker? LoadMarker(
        string path,
        string temporaryPath)
    {
        try
        {
            if (HasFileSystemEvidence(temporaryPath))
            {
                return LoadedMarker.UnknownFailure;
            }

            if (!HasFileSystemEvidence(path))
            {
                return null;
            }

            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return LoadedMarker.UnknownFailure;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumMarkerBytes)
            {
                return LoadedMarker.UnknownFailure;
            }

            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return LoadedMarker.UnknownFailure;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    return LoadedMarker.UnknownFailure;
                }
            }

            if (!root.TryGetProperty(
                    "schemaVersion",
                    out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out var version)
                || !root.TryGetProperty("reason", out var reason)
                || reason.ValueKind != JsonValueKind.String)
            {
                return LoadedMarker.UnknownFailure;
            }

            if (version == 1)
            {
                if (names.Count != 2
                    || names.Any(name => name is not (
                        "schemaVersion" or "reason")))
                {
                    return LoadedMarker.UnknownFailure;
                }

                return reason.GetString() switch
                {
                    "protocolMismatch" => new LoadedMarker(
                        LiveAttemptBlockReason.ProtocolMismatch),
                    "unknownFailure" => LoadedMarker.UnknownFailure,
                    _ => LoadedMarker.UnknownFailure,
                };
            }

            if (version == 2)
            {
                if (names.Count != 3
                    || names.Any(name => name is not (
                        "schemaVersion"
                        or "reason"
                        or "compatibilityRevision"))
                    || !string.Equals(
                        reason.GetString(),
                        "protocolMismatch",
                        StringComparison.Ordinal)
                    || !root.TryGetProperty(
                        "compatibilityRevision",
                        out var legacyCompatibilityRevision)
                    || legacyCompatibilityRevision.ValueKind
                        != JsonValueKind.String
                    || !IsValidCompatibilityRevision(
                        legacyCompatibilityRevision.GetString()))
                {
                    return LoadedMarker.UnknownFailure;
                }

                return new LoadedMarker(
                    LiveAttemptBlockReason.ProtocolMismatch);
            }

            if (version != CurrentMarkerSchemaVersion
                || names.Count != 4
                || names.Any(name => name is not (
                    "schemaVersion"
                    or "reason"
                    or "compatibilityRevision"
                    or "origin"))
                || !string.Equals(
                    reason.GetString(),
                    "protocolMismatch",
                    StringComparison.Ordinal)
                || !root.TryGetProperty(
                    "compatibilityRevision",
                    out var compatibilityRevision)
                || compatibilityRevision.ValueKind != JsonValueKind.String
                || !IsValidCompatibilityRevision(
                    compatibilityRevision.GetString())
                || !root.TryGetProperty("origin", out var origin)
                || origin.ValueKind != JsonValueKind.String
                || !string.Equals(
                    origin.GetString(),
                    MutationAmbiguousOrigin,
                    StringComparison.Ordinal))
            {
                return LoadedMarker.UnknownFailure;
            }

            return new LoadedMarker(
                LiveAttemptBlockReason.ProtocolMismatch);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return LoadedMarker.UnknownFailure;
        }
    }

    private static string ValidateCompatibilityRevision(
        string revision,
        string parameterName)
    {
        if (!IsValidCompatibilityRevision(revision))
        {
            throw new ArgumentException(
                "live_compatibility_revision_invalid",
                parameterName);
        }

        return revision;
    }

    private static bool IsValidCompatibilityRevision(string? revision) =>
        revision is { Length: > 0 and <= MaximumCompatibilityRevisionLength }
        && revision.All(character => character is >= '!' and <= '~');

    private static bool HasFileSystemEvidence(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static int Encode(LiveAttemptBlockReason reason) => reason switch
    {
        LiveAttemptBlockReason.ProtocolMismatch => 1,
        LiveAttemptBlockReason.UnknownFailure => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static LiveAttemptBlockReason? Decode(int value) => value switch
    {
        0 => null,
        1 => LiveAttemptBlockReason.ProtocolMismatch,
        _ => LiveAttemptBlockReason.UnknownFailure,
    };

    private static LiveStateException CreateException(
        LiveAttemptBlockReason reason) => new(reason switch
        {
            LiveAttemptBlockReason.ProtocolMismatch => "live_protocol_blocked",
            _ => "live_needs_review",
        });

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private readonly record struct LoadedMarker(
        LiveAttemptBlockReason Reason)
    {
        public static LoadedMarker UnknownFailure { get; } = new(
            LiveAttemptBlockReason.UnknownFailure);
    }
}
