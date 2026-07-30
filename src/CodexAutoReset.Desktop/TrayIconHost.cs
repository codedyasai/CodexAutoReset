using System.Drawing;
using System.IO;
using System.Text.Json;
using CodexAutoReset.Runtime;
using Forms = System.Windows.Forms;

namespace CodexAutoReset.Desktop;

public sealed class TrayIconHost : IDisposable
{
    private readonly MainWindowViewModel viewModel;
    private readonly MainWindow window;
    private readonly Func<int, Task> exitAsync;
    private readonly Icon trayIcon;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ToolStripMenuItem statusItem;
    private readonly Forms.ToolStripMenuItem weeklyItem;
    private readonly Forms.ToolStripMenuItem fiveHourItem;
    private readonly Forms.ToolStripMenuItem creditsItem;
    private readonly Forms.ToolStripMenuItem startupItem;
    private readonly CompatibilityNotificationGate compatibilityNotificationGate;
    private readonly UsageResetNotificationCoordinator?
        usageResetNotificationCoordinator;
    private string? lastNotificationCode;
    private bool disposed;

    public TrayIconHost(
        MainWindowViewModel viewModel,
        MainWindow window,
        Func<int, Task> exitAsync,
        string? compatibilityNotificationStatePath = null,
        UsageResetNotificationCoordinator?
            usageResetNotificationCoordinator = null)
    {
        this.viewModel = viewModel;
        this.window = window;
        this.exitAsync = exitAsync;
        this.usageResetNotificationCoordinator =
            usageResetNotificationCoordinator;
        compatibilityNotificationGate = compatibilityNotificationStatePath is null
            ? new CompatibilityNotificationGate()
            : new CompatibilityNotificationGate(
                compatibilityNotificationStatePath);

        statusItem = new Forms.ToolStripMenuItem("상태: 확인 전") { Enabled = false };
        weeklyItem = new Forms.ToolStripMenuItem("주간: 확인 전") { Enabled = false };
        fiveHourItem = new Forms.ToolStripMenuItem("5시간: 확인 전") { Enabled = false };
        creditsItem = new Forms.ToolStripMenuItem("초기화권: 확인 전") { Enabled = false };
        startupItem = new Forms.ToolStripMenuItem("Windows 자동 시작");
        startupItem.Click += OnStartupClick;

        var refreshItem = new Forms.ToolStripMenuItem("지금 새로고침");
        refreshItem.Click += (_, _) => viewModel.RequestRefresh();
        var notificationItem =
            new Forms.ToolStripMenuItem("초기화 알림 보기")
            {
                Enabled = usageResetNotificationCoordinator is not null,
            };
        notificationItem.Click += (_, _) =>
            usageResetNotificationCoordinator?.BringPendingToFront();
        var openItem = new Forms.ToolStripMenuItem("설정 열기");
        openItem.Click += (_, _) => Dispatch(window.ShowAndActivate);
        var exitItem = new Forms.ToolStripMenuItem("종료");
        exitItem.Click += OnExitClick;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            statusItem,
            weeklyItem,
            fiveHourItem,
            creditsItem,
            new Forms.ToolStripSeparator(),
            refreshItem,
            notificationItem,
            openItem,
            startupItem,
            new Forms.ToolStripSeparator(),
            exitItem,
        ]);

        trayIcon = LoadTrayIcon();
        notifyIcon = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "CodexAutoReset",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += (_, _) => Dispatch(window.ShowAndActivate);
        notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateMenu(viewModel.CurrentSnapshot);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        trayIcon.Dispose();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.CurrentSnapshot)
            || eventArgs.PropertyName == nameof(MainWindowViewModel.ActualStartupStatus))
        {
            UpdateMenu(viewModel.CurrentSnapshot);
        }
    }

    private void UpdateMenu(MonitorSnapshot snapshot)
    {
        statusItem.Text = snapshot.CompatibilityState switch
        {
            CodexCompatibilityState.VerificationPending =>
                "상태: Codex 응답 재확인 중",
            CodexCompatibilityState.ReadUnsupported
                or CodexCompatibilityState.MutationUnverified =>
                "상태: Codex 호환성 오류",
            _ => snapshot.ActionKind == CycleActionKind.ResetPending
                ? snapshot.IsFailure
                    ? "상태: 초기화 결과 대기 · 조회 실패 (자동 재시도)"
                    : "상태: 초기화 결과 확인 대기 (자동 재시도)"
                : snapshot.IsFailure
                    ? "상태: 안전 차단"
                    : snapshot.ActionKind == CycleActionKind.ResetSucceeded
                        ? "상태: 초기화권 처리 완료"
                        : snapshot.ActionKind == CycleActionKind.ResetNoEffect
                            ? "상태: 처리 완료 · 초기화 항목 없음"
                            : $"상태: {FormatStatus(snapshot.StatusCode)}",
        };
        weeklyItem.Text = $"주간: {FormatRemaining(viewModel.WeeklyRemainingText)}";
        fiveHourItem.Text =
            $"5시간: {FormatRemaining(viewModel.FiveHourRemainingText)}";
        creditsItem.Text = $"초기화권: {snapshot.AvailableCreditCount?.ToString() ?? "-"}";
        startupItem.Checked = viewModel.IsStartupActuallyEnabled;
        startupItem.Text = viewModel.ActualStartupStatus switch
        {
            StartupStatus.Enabled => "Windows 자동 시작 (켜짐)",
            StartupStatus.Disabled => "Windows 자동 시작 (꺼짐)",
            StartupStatus.ForeignValue => "Windows 자동 시작 (다른 항목 점유)",
            StartupStatus.InvalidOwnedValue => "Windows 자동 시작 (경로 오류)",
            _ => "Windows 자동 시작 (상태 알 수 없음)",
        };

        notifyIcon.Text = BuildTooltip(snapshot);
        usageResetNotificationCoordinator?.RequestRefresh();
        MaybeNotify(snapshot);
    }

    private void MaybeNotify(MonitorSnapshot snapshot)
    {
        if (MaybeNotifyCompatibility(snapshot))
        {
            return;
        }

        var shouldNotify = snapshot.IsFailure
            || snapshot.ActionKind == CycleActionKind.ResetPending
            || snapshot.ActionKind == CycleActionKind.ResetNoEffect;
        if (!shouldNotify
            || string.Equals(lastNotificationCode, snapshot.StatusCode, StringComparison.Ordinal))
        {
            return;
        }

        lastNotificationCode = snapshot.StatusCode;
        notifyIcon.ShowBalloonTip(
            4000,
            "CodexAutoReset",
            snapshot.ActionKind == CycleActionKind.ResetPending
                ? snapshot.IsFailure
                    ? "사용량 조회에 실패했습니다. 같은 처리 요청으로 자동 재시도합니다."
                    : "결과 확인 대기 중입니다. 같은 처리 요청으로 자동 재시도합니다."
                : snapshot.IsFailure
                    ? "조회 또는 판단이 불명확해 안전하게 차단했습니다."
                    : "초기화권 처리 결과를 확인했습니다.",
            snapshot.IsFailure || snapshot.ActionKind == CycleActionKind.ResetPending
                ? Forms.ToolTipIcon.Warning
                : Forms.ToolTipIcon.Info);
    }

    private bool MaybeNotifyCompatibility(MonitorSnapshot snapshot)
    {
        var state = snapshot.CompatibilityState;
        var isCompatibilityConcern = state is
            CodexCompatibilityState.VerificationPending
            or CodexCompatibilityState.ReadUnsupported
            or CodexCompatibilityState.MutationUnverified;
        if (!isCompatibilityConcern)
        {
            compatibilityNotificationGate.Consume(state);
            return false;
        }

        if (compatibilityNotificationGate.Consume(state))
        {
            notifyIcon.ShowBalloonTip(
                5000,
                "Codex 호환성 확인 필요",
                "현재 Codex 응답을 안전하게 지원하지 않아 일부 기능을 중단했습니다. 앱을 열어 자세한 내용을 확인하세요.",
                Forms.ToolTipIcon.Warning);
        }

        // Pending verification and confirmed compatibility failures own the
        // notification channel so the generic failure balloon cannot create an
        // early or duplicate warning for the same incident.
        return true;
    }

    private static string FormatStatus(string statusCode) => statusCode switch
    {
        "waiting" => "확인 전",
        "automation_disabled" => "사용량 확인 완료",
        "no_action" => "정상 · 초기화 조건 미충족",
        "duplicate_suppressed" => "정상 · 이 한도 구간은 이미 처리됨",
        "live_recovery_pending" => "안전 대기 · 초기화 후 잔여량 회복 확인 중",
        "usage_reset_settling" => "안전 대기 · 사용량 초기화 반영 확인 중",
        "usage_reset_state_unavailable" => "안전 차단 · 초기화 확인 기록 오류",
        "scheduled_reset_imminent" => "안전 대기 · 정기 초기화 임박",
        _ => "상태 확인됨",
    };

    private static Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri(
                "/CodexAutoReset;component/Assets/CodexAutoReset.ico",
                UriKind.Relative))
            ?? throw new InvalidDataException("tray_icon_missing");
        using var stream = resource.Stream;
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    private async void OnStartupClick(object? sender, EventArgs eventArgs)
    {
        await DispatchAsync(() =>
            viewModel.SetStartWithWindowsAsync(!viewModel.IsStartupActuallyEnabled));
    }

    private async void OnExitClick(object? sender, EventArgs eventArgs)
    {
        await DispatchAsync(async () =>
        {
            window.CloseForExit();
            await exitAsync(0);
        });
    }

    private void OnBalloonTipClicked(object? sender, EventArgs eventArgs) =>
        Dispatch(window.ShowAndActivate);

    private static string FormatRemaining(string remainingText)
    {
        if (string.IsNullOrWhiteSpace(remainingText)
            || string.Equals(remainingText, "-", StringComparison.Ordinal))
        {
            return "-";
        }

        return remainingText.EndsWith("%", StringComparison.Ordinal)
            ? $"잔여 {remainingText}"
            : remainingText;
    }

    private string BuildTooltip(MonitorSnapshot snapshot)
    {
        if (snapshot.CompatibilityState is
            CodexCompatibilityState.ReadUnsupported
            or CodexCompatibilityState.MutationUnverified)
        {
            return "CodexAutoReset · Codex 호환성 오류";
        }

        if (snapshot.CompatibilityState ==
            CodexCompatibilityState.VerificationPending)
        {
            return "CodexAutoReset · Codex 응답 재확인 중";
        }

        var weeklyRemaining = ToTooltipRemaining(viewModel.WeeklyRemainingText);
        var fiveHourRemaining =
            ToTooltipRemaining(viewModel.FiveHourRemainingText);
        var text =
            $"CodexAutoReset · 주간 {weeklyRemaining} · 5시간 {fiveHourRemaining} · 권 {snapshot.AvailableCreditCount?.ToString() ?? "-"}";
        return text.Length <= 63 ? text : text[..63];
    }

    private static string ToTooltipRemaining(string remainingText) =>
        string.IsNullOrWhiteSpace(remainingText)
            || string.Equals(remainingText, "-", StringComparison.Ordinal)
            ? "-"
            : remainingText;

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }

    private static async Task DispatchAsync(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            await action();
            return;
        }

        await dispatcher.InvokeAsync(action).Task.Unwrap();
    }
}

public sealed class CompatibilityNotificationGate
{
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(24);
    private const int MaximumStateBytes = 1_024;
    private readonly Func<DateTimeOffset> utcNowProvider;
    private readonly string? durablePath;
    private bool incompatibleIncidentActive;
    private DateTimeOffset? lastNotificationAt;

    public CompatibilityNotificationGate(
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        this.utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public CompatibilityNotificationGate(
        string durablePath,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        this.durablePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(durablePath)
                ? throw new ArgumentException(
                    "compatibility_notification_path_invalid",
                    nameof(durablePath))
                : durablePath);
        this.utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        LoadDurableState();
    }

    public bool Consume(CodexCompatibilityState state)
    {
        if (state == CodexCompatibilityState.Compatible)
        {
            var hadActiveIncident = incompatibleIncidentActive;
            incompatibleIncidentActive = false;
            lastNotificationAt = null;
            if (hadActiveIncident)
            {
                TryDeleteDurableState();
            }

            return false;
        }

        if (state is not (
            CodexCompatibilityState.ReadUnsupported
            or CodexCompatibilityState.MutationUnverified))
        {
            return false;
        }

        var now = utcNowProvider();
        if (incompatibleIncidentActive
            && lastNotificationAt is { } notifiedAt
            && now - notifiedAt < ReminderInterval)
        {
            return false;
        }

        incompatibleIncidentActive = true;
        lastNotificationAt = now;
        TryPersistDurableState(now);
        return true;
    }

    private void LoadDurableState()
    {
        if (durablePath is null)
        {
            return;
        }

        try
        {
            var info = new FileInfo(durablePath);
            if (!info.Exists
                || info.Length is <= 0 or > MaximumStateBytes
                || info.Attributes.HasFlag(FileAttributes.Directory)
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            using var stream = new FileStream(
                durablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetRawText().Length > MaximumStateBytes)
            {
                return;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    return;
                }
            }

            if (names.Count != 2
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || !schemaVersion.TryGetInt32(out var version)
                || version != 1
                || !root.TryGetProperty(
                    "lastNotificationAt",
                    out var notificationAt)
                || !notificationAt.TryGetInt64(out var unixSeconds))
            {
                return;
            }

            var loadedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            var now = utcNowProvider();
            if (loadedAt > now.AddMinutes(5))
            {
                return;
            }

            incompatibleIncidentActive = true;
            lastNotificationAt = loadedAt;
        }
        catch (Exception exception) when (exception is
            IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or JsonException
                or InvalidOperationException
                or ArgumentOutOfRangeException)
        {
        }
    }

    private void TryPersistDurableState(DateTimeOffset notificationAt)
    {
        if (durablePath is null)
        {
            return;
        }

        var temporaryPath = durablePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(durablePath)
                ?? throw new IOException(
                    "compatibility_notification_path_invalid");
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024,
                FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteNumber(
                    "lastNotificationAt",
                    notificationAt.ToUnixTimeSeconds());
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, durablePath, overwrite: true);
        }
        catch (Exception exception) when (exception is
            IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            TryDeletePath(temporaryPath);
        }
    }

    private void TryDeleteDurableState()
    {
        if (durablePath is null)
        {
            return;
        }

        TryDeletePath(durablePath);
        TryDeletePath(durablePath + ".tmp");
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is
            IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
        }
    }
}
