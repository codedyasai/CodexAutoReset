using System.Text;
using System.Text.Json;

namespace CodexAutoReset.Core;

/// <summary>
/// Keeps protocol and unknown live failures fail-closed in memory and, when a
/// path is supplied, across process restarts.
/// </summary>
public sealed class LiveResetSafetyLatch
{
    private const int MaximumMarkerBytes = 4 * 1024;
    private const string MarkerFileName = "live-safety-block.json";

    private readonly string? durablePath;
    private readonly string? temporaryPath;
    private readonly Action? beforePersist;
    private readonly object durabilityGate = new();
    private int blockReason;
    private int durabilityState;

    public LiveResetSafetyLatch()
    {
    }

    public LiveResetSafetyLatch(string durablePath)
        : this(durablePath, beforePersist: null)
    {
    }

    internal LiveResetSafetyLatch(string durablePath, Action? beforePersist)
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
        this.beforePersist = beforePersist;
        var loadedReason = LoadReason(this.durablePath, temporaryPath);
        if (loadedReason is not null)
        {
            blockReason = Encode(loadedReason.Value);
            durabilityState = 1;
        }
    }

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public LiveAttemptBlockReason? BlockReason => Decode(
        Volatile.Read(ref blockReason));

    public void BlockProtocolMismatch() => Block(
        LiveAttemptBlockReason.ProtocolMismatch);

    public void BlockUnknownFailure() => Block(
        LiveAttemptBlockReason.UnknownFailure);

    internal void Block(LiveAttemptBlockReason reason)
    {
        if (reason is not (LiveAttemptBlockReason.ProtocolMismatch
            or LiveAttemptBlockReason.UnknownFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Interlocked.CompareExchange(
            ref blockReason,
            Encode(reason),
            comparand: 0);
        PersistIfRequired();
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

        lock (durabilityGate)
        {
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
                var bytes = Encoding.UTF8.GetBytes(
                    $"{{\"schemaVersion\":1,\"reason\":\"{reason}\"}}");
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
    }

    private static LiveAttemptBlockReason? LoadReason(
        string path,
        string temporaryPath)
    {
        try
        {
            if (HasFileSystemEvidence(temporaryPath))
            {
                return LiveAttemptBlockReason.UnknownFailure;
            }

            if (!HasFileSystemEvidence(path))
            {
                return null;
            }

            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return LiveAttemptBlockReason.UnknownFailure;
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
                return LiveAttemptBlockReason.UnknownFailure;
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
                return LiveAttemptBlockReason.UnknownFailure;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)
                    || property.Name is not ("schemaVersion" or "reason"))
                {
                    return LiveAttemptBlockReason.UnknownFailure;
                }
            }

            if (names.Count != 2
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out var version)
                || version != 1
                || !root.TryGetProperty("reason", out var reason)
                || reason.ValueKind != JsonValueKind.String)
            {
                return LiveAttemptBlockReason.UnknownFailure;
            }

            return reason.GetString() switch
            {
                "protocolMismatch" => LiveAttemptBlockReason.ProtocolMismatch,
                "unknownFailure" => LiveAttemptBlockReason.UnknownFailure,
                _ => LiveAttemptBlockReason.UnknownFailure,
            };
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return LiveAttemptBlockReason.UnknownFailure;
        }
    }

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
}
