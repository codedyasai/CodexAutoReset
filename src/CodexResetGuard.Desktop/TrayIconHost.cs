using System.Drawing;
using System.IO;
using CodexResetGuard.Runtime;
using Forms = System.Windows.Forms;

namespace CodexResetGuard.Desktop;

public sealed class TrayIconHost : IDisposable
{
    private readonly MainWindowViewModel viewModel;
    private readonly MainWindow window;
    private readonly Func<int, Task> exitAsync;
    private readonly Icon trayIcon;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ToolStripMenuItem statusItem;
    private readonly Forms.ToolStripMenuItem automationItem;
    private readonly Forms.ToolStripMenuItem weeklyItem;
    private readonly Forms.ToolStripMenuItem creditsItem;
    private readonly Forms.ToolStripMenuItem startupItem;
    private string? lastNotificationCode;
    private bool disposed;

    public TrayIconHost(
        MainWindowViewModel viewModel,
        MainWindow window,
        Func<int, Task> exitAsync)
    {
        this.viewModel = viewModel;
        this.window = window;
        this.exitAsync = exitAsync;

        statusItem = new Forms.ToolStripMenuItem("상태: 확인 전") { Enabled = false };
        automationItem = new Forms.ToolStripMenuItem("자동 초기화: 꺼짐") { Enabled = false };
        weeklyItem = new Forms.ToolStripMenuItem("주간: 확인 전") { Enabled = false };
        creditsItem = new Forms.ToolStripMenuItem("초기화권: 확인 전") { Enabled = false };
        startupItem = new Forms.ToolStripMenuItem("Windows 자동 시작");
        startupItem.Click += OnStartupClick;

        var refreshItem = new Forms.ToolStripMenuItem("지금 새로고침");
        refreshItem.Click += (_, _) => viewModel.RequestRefresh();
        var openItem = new Forms.ToolStripMenuItem("설정 열기");
        openItem.Click += (_, _) => Dispatch(window.ShowAndActivate);
        var exitItem = new Forms.ToolStripMenuItem("종료");
        exitItem.Click += OnExitClick;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(
        [
            statusItem,
            automationItem,
            weeklyItem,
            creditsItem,
            new Forms.ToolStripSeparator(),
            refreshItem,
            openItem,
            startupItem,
            new Forms.ToolStripSeparator(),
            exitItem,
        ]);

        trayIcon = LoadTrayIcon();
        notifyIcon = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "CodexResetGuard",
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += (_, _) => Dispatch(window.ShowAndActivate);

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
        statusItem.Text = snapshot.ActionKind == CycleActionKind.ResetPending
            ? snapshot.IsFailure
                ? "상태: 초기화 결과 대기 · 조회 실패 (자동 재시도)"
                : "상태: 초기화 결과 확인 대기 (자동 재시도)"
            : snapshot.IsFailure
                ? "상태: 안전 차단"
                : snapshot.ActionKind == CycleActionKind.ResetSucceeded
                    ? "상태: 초기화권 처리 완료"
                    : snapshot.ActionKind == CycleActionKind.ResetNoEffect
                        ? "상태: 처리 완료 · 초기화 항목 없음"
                        : $"상태: {FormatStatus(snapshot.StatusCode)}";
        automationItem.Text = snapshot.Settings.AutomationEnabled
            ? "자동 초기화: 켜짐"
            : "자동 초기화: 꺼짐";
        weeklyItem.Text = $"주간: {FormatRemaining(snapshot.Weekly)}";
        creditsItem.Text = $"초기화권: {snapshot.AvailableCreditCount?.ToString() ?? "알 수 없음"}";
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
        MaybeNotify(snapshot);
    }

    private void MaybeNotify(MonitorSnapshot snapshot)
    {
        var shouldNotify = snapshot.IsFailure
            || snapshot.ActionKind == CycleActionKind.ResetPending
            || snapshot.ActionKind is CycleActionKind.ResetSucceeded
                or CycleActionKind.ResetNoEffect;
        if (!shouldNotify
            || string.Equals(lastNotificationCode, snapshot.StatusCode, StringComparison.Ordinal))
        {
            return;
        }

        lastNotificationCode = snapshot.StatusCode;
        notifyIcon.ShowBalloonTip(
            4000,
            "CodexResetGuard",
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

    private static string FormatStatus(string statusCode) => statusCode switch
    {
        "waiting" => "확인 전",
        "automation_disabled" => "사용량 확인 중 · 자동 초기화 꺼짐",
        "no_action" => "정상 · 초기화 조건 미충족",
        "duplicate_suppressed" => "정상 · 이번 주간 구간은 이미 처리됨",
        _ => "상태 확인됨",
    };

    private static Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri(
                "/CodexResetGuard;component/Assets/CodexResetGuard.ico",
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

    private static string FormatRemaining(Core.WindowReading? reading) =>
        reading is null ? "알 수 없음" : $"잔여 {reading.RemainingPercent:F1}%";

    private static string BuildTooltip(MonitorSnapshot snapshot)
    {
        var remaining = snapshot.Weekly is null
            ? "?"
            : $"{snapshot.Weekly.RemainingPercent:F0}%";
        var state = snapshot.Settings.AutomationEnabled ? "자동 켜짐" : "자동 꺼짐";
        var text = $"CodexResetGuard · 주간 {remaining} · 권 {snapshot.AvailableCreditCount?.ToString() ?? "?"} · {state}";
        return text.Length <= 63 ? text : text[..63];
    }

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
