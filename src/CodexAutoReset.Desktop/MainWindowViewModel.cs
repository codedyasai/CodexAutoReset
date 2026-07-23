using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;

namespace CodexAutoReset.Desktop;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SettingsUpdateService settingsUpdateService;
    private readonly StartupService startupService;
    private readonly GuardMonitorService monitor;
    private readonly Func<string?> currentExecutablePathProvider;
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private int stopping;
    private GuardSettings persistedSettings;
    private string thresholdText;
    private string pollIntervalText;
    private bool automationEnabled;
    private bool startWithWindows;
    private string weeklyRemainingText = "—";
    private double weeklyRemainingPercent;
    private string weeklyResetStatus = "아직 확인하지 않았습니다.";
    private string creditStatus = "확인 전";
    private string lastCheckedStatus = "확인 전";
    private string overallStatus = "사용량을 확인할 예정입니다.";
    private string saveStatus = string.Empty;
    private StartupStatus? actualStartupStatus;

    public MainWindowViewModel(
        JsonSettingsStore settingsStore,
        StartupService startupService,
        GuardMonitorService monitor,
        GuardSettings initialSettings,
        Func<string?>? currentExecutablePathProvider = null)
    {
        settingsUpdateService = new SettingsUpdateService(settingsStore, startupService);
        this.startupService = startupService;
        this.monitor = monitor;
        this.currentExecutablePathProvider = currentExecutablePathProvider
            ?? (() => Environment.ProcessPath);
        persistedSettings = initialSettings;
        thresholdText = initialSettings.RemainingThresholdPercent.ToString(
            CultureInfo.InvariantCulture);
        pollIntervalText = initialSettings.PollIntervalMinutes.ToString(
            CultureInfo.InvariantCulture);
        automationEnabled = initialSettings.AutomationEnabled;
        startWithWindows = initialSettings.StartWithWindows;

        TryRefreshStartupState();
        monitor.SnapshotChanged += OnSnapshotChanged;
        ApplySnapshot(monitor.CurrentSnapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ThresholdText
    {
        get => thresholdText;
        set => SetField(ref thresholdText, value);
    }

    public string PollIntervalText
    {
        get => pollIntervalText;
        set => SetField(ref pollIntervalText, value);
    }

    public bool AutomationEnabled
    {
        get => automationEnabled;
        set
        {
            if (SetField(ref automationEnabled, value))
            {
                OnPropertyChanged(nameof(AutomationStateText));
                OnPropertyChanged(nameof(RequiresAutomationEnableConfirmation));
            }
        }
    }

    public string AutomationStateText => AutomationEnabled
        ? "자동 초기화 켜짐"
        : "자동 초기화 꺼짐";

    public bool RequiresAutomationEnableConfirmation =>
        AutomationEnabled && !persistedSettings.AutomationEnabled;

    public bool StartWithWindows
    {
        get => startWithWindows;
        set => SetField(ref startWithWindows, value);
    }

    public StartupStatus? ActualStartupStatus => actualStartupStatus;

    public bool IsStartupActuallyEnabled =>
        actualStartupStatus == StartupStatus.Enabled;

    public string StartupStatusText => actualStartupStatus switch
    {
        StartupStatus.Enabled => "자동 시작 등록됨",
        StartupStatus.Disabled => "자동 시작 꺼짐",
        StartupStatus.ForeignValue => "다른 항목이 같은 이름을 사용 중",
        StartupStatus.InvalidOwnedValue => "등록된 실행 파일 경로가 유효하지 않음",
        _ => "자동 시작 상태를 확인할 수 없음",
    };

    public string WeeklyRemainingText
    {
        get => weeklyRemainingText;
        private set => SetField(ref weeklyRemainingText, value);
    }

    public double WeeklyRemainingPercent
    {
        get => weeklyRemainingPercent;
        private set => SetField(ref weeklyRemainingPercent, value);
    }

    public string WeeklyResetStatus
    {
        get => weeklyResetStatus;
        private set => SetField(ref weeklyResetStatus, value);
    }

    public string CreditStatus
    {
        get => creditStatus;
        private set => SetField(ref creditStatus, value);
    }

    public string LastCheckedStatus
    {
        get => lastCheckedStatus;
        private set => SetField(ref lastCheckedStatus, value);
    }

    public string OverallStatus
    {
        get => overallStatus;
        private set => SetField(ref overallStatus, value);
    }

    public string SaveStatus
    {
        get => saveStatus;
        private set => SetField(ref saveStatus, value);
    }

    public MonitorSnapshot CurrentSnapshot => monitor.CurrentSnapshot;

    public void RequestRefresh() => monitor.RequestRefresh();

    public async Task SaveAsync(bool automationEnableConfirmed = false)
    {
        if (Volatile.Read(ref stopping) != 0)
        {
            return;
        }

        if (!await saveGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                return;
            }

            SaveStatus = string.Empty;
            if (RequiresAutomationEnableConfirmation && !automationEnableConfirmed)
            {
                SaveStatus = "자동 초기화를 켜려면 초기화권 사용 가능성을 확인해야 합니다.";
                return;
            }

            if (!int.TryParse(
                    ThresholdText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var threshold)
                || !int.TryParse(
                    PollIntervalText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pollInterval))
            {
                SaveStatus = "임계값과 조회 주기는 정수로 입력하세요.";
                return;
            }

            var settings = persistedSettings with
            {
                RemainingThresholdPercent = threshold,
                PollIntervalMinutes = pollInterval,
                StartWithWindows = StartWithWindows,
                AutomationEnabled = AutomationEnabled,
            };

            try
            {
                JsonSettingsStore.Validate(settings);
            }
            catch (SettingsException exception)
            {
                SaveStatus = ToFriendlySettingsFailure(exception.ReasonCode);
                return;
            }

            try
            {
                await monitor.SaveSettingsAsync(
                    settingsUpdateService,
                    persistedSettings,
                    settings,
                    currentExecutablePathProvider(),
                    CancellationToken.None);
                persistedSettings = settings;
                TryRefreshStartupState();
                OnPropertyChanged(nameof(RequiresAutomationEnableConfirmation));
                SaveStatus = "설정을 저장했습니다.";
            }
            catch (SettingsConflictException exception)
            {
                ApplyPersistedSettings(exception.CurrentSettings);
                SaveStatus = "설정 파일이 외부에서 변경되어 저장하지 않았습니다. 최신 값을 확인한 뒤 다시 저장하세요.";
            }
            catch (SettingsPartiallyAppliedException exception)
            {
                persistedSettings = exception.PersistedSettings;
                TryRefreshStartupState();
                OnPropertyChanged(nameof(RequiresAutomationEnableConfirmation));
                SaveStatus = "앱 설정은 저장했지만 Windows 자동 시작 변경은 완료하지 못했습니다. 실제 자동 시작 상태를 확인하세요.";
            }
            catch (StartupException exception)
            {
                TryRefreshStartupState();
                SaveStatus = exception.ReasonCode == "startup_foreign_value"
                    ? "같은 이름의 다른 자동 시작 항목이 있어 변경하지 않았습니다."
                    : $"자동 시작 설정 실패: {exception.ReasonCode}";
            }
            catch (SettingsException exception)
            {
                TryRefreshStartupState();
                SaveStatus = $"설정 저장 실패: {exception.ReasonCode}";
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                TryRefreshStartupState();
                SaveStatus = "설정을 안전하게 저장하지 못했습니다.";
            }
            catch (Exception)
            {
                TryRefreshStartupState();
                SaveStatus = "예상하지 못한 로컬 오류로 설정을 저장하지 않았습니다.";
            }
        }
        finally
        {
            saveGate.Release();
        }
    }

    public async Task SetStartWithWindowsAsync(bool enabled)
    {
        if (Volatile.Read(ref stopping) != 0
            || !await saveGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                return;
            }

            SaveStatus = string.Empty;
            var baselineSettings = persistedSettings;
            try
            {
                var updatedSettings = await monitor.SetStartWithWindowsAsync(
                    settingsUpdateService,
                    enabled,
                    currentExecutablePathProvider(),
                    CancellationToken.None);
                MergeFreshPersistedSettings(baselineSettings, updatedSettings);
                TryRefreshStartupState();
                SaveStatus = "자동 시작 설정을 저장했습니다.";
            }
            catch (SettingsConflictException exception)
            {
                MergeFreshPersistedSettings(
                    baselineSettings,
                    exception.CurrentSettings);
                TryRefreshStartupState();
                monitor.RequestRefresh();
                SaveStatus = "설정 파일이 다시 변경되어 자동 시작 설정을 저장하지 않았습니다.";
            }
            catch (SettingsPartiallyAppliedException exception)
            {
                MergeFreshPersistedSettings(
                    baselineSettings,
                    exception.PersistedSettings);
                TryRefreshStartupState();
                SaveStatus = "앱 설정은 유지했지만 Windows 자동 시작 변경은 완료하지 못했습니다.";
            }
            catch (StartupException exception)
            {
                TryRefreshStartupState();
                SaveStatus = exception.ReasonCode == "startup_foreign_value"
                    ? "같은 이름의 다른 자동 시작 항목이 있어 변경하지 않았습니다."
                    : $"자동 시작 설정 실패: {exception.ReasonCode}";
            }
            catch (SettingsException exception)
            {
                TryRefreshStartupState();
                SaveStatus = $"설정 저장 실패: {exception.ReasonCode}";
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                TryRefreshStartupState();
                SaveStatus = "자동 시작 설정을 안전하게 저장하지 못했습니다.";
            }
            catch (Exception)
            {
                TryRefreshStartupState();
                SaveStatus = "예상하지 못한 로컬 오류로 자동 시작 설정을 저장하지 않았습니다.";
            }
        }
        finally
        {
            saveGate.Release();
        }
    }

    public async Task StopAndDrainSettingsAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        await saveGate.WaitAsync();
        saveGate.Release();
    }

    public void CancelAutomationEnable()
    {
        AutomationEnabled = persistedSettings.AutomationEnabled;
        SaveStatus = "자동 초기화를 켜지 않았습니다.";
    }

    private void OnSnapshotChanged(object? sender, MonitorSnapshot snapshot)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplySnapshot(snapshot);
            return;
        }

        _ = dispatcher.BeginInvoke(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        WeeklyRemainingText = snapshot.Weekly is null
            ? "—"
            : $"{snapshot.Weekly.RemainingPercent:F1}%";
        WeeklyRemainingPercent = snapshot.Weekly is null
            ? 0
            : Math.Clamp(snapshot.Weekly.RemainingPercent, 0, 100);
        WeeklyResetStatus = FormatReset(snapshot.Weekly);
        CreditStatus = snapshot.AvailableCreditCount?.ToString(
            CultureInfo.InvariantCulture) ?? "알 수 없음";
        LastCheckedStatus = snapshot.StatusCode == "waiting"
            ? "확인 전"
            : snapshot.ObservedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

        OverallStatus = FormatOverallStatus(snapshot);
        OnPropertyChanged(nameof(CurrentSnapshot));
    }

    private void ApplyPersistedSettings(GuardSettings settings)
    {
        persistedSettings = settings;
        ThresholdText = settings.RemainingThresholdPercent.ToString(
            CultureInfo.InvariantCulture);
        PollIntervalText = settings.PollIntervalMinutes.ToString(
            CultureInfo.InvariantCulture);
        AutomationEnabled = settings.AutomationEnabled;
        OnPropertyChanged(nameof(RequiresAutomationEnableConfirmation));
        TryRefreshStartupState();
        monitor.RequestRefresh();
    }

    private void MergeFreshPersistedSettings(
        GuardSettings baselineSettings,
        GuardSettings freshSettings)
    {
        var thresholdWasUnedited = string.Equals(
            ThresholdText,
            baselineSettings.RemainingThresholdPercent.ToString(
                CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        var pollIntervalWasUnedited = string.Equals(
            PollIntervalText,
            baselineSettings.PollIntervalMinutes.ToString(
                CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        var automationWasUnedited =
            AutomationEnabled == baselineSettings.AutomationEnabled;

        persistedSettings = freshSettings;
        if (thresholdWasUnedited)
        {
            ThresholdText = freshSettings.RemainingThresholdPercent.ToString(
                CultureInfo.InvariantCulture);
        }

        if (pollIntervalWasUnedited)
        {
            PollIntervalText = freshSettings.PollIntervalMinutes.ToString(
                CultureInfo.InvariantCulture);
        }

        if (automationWasUnedited)
        {
            AutomationEnabled = freshSettings.AutomationEnabled;
        }

        OnPropertyChanged(nameof(RequiresAutomationEnableConfirmation));
    }

    private void RefreshStartupState()
    {
        var state = startupService.GetState();
        SetActualStartupStatus(state.Status);
    }

    private void TryRefreshStartupState()
    {
        try
        {
            RefreshStartupState();
        }
        catch (Exception exception) when (exception is
            IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            SetActualStartupStatus(null);
        }
    }

    private void SetActualStartupStatus(StartupStatus? status)
    {
        if (actualStartupStatus == status)
        {
            StartWithWindows = status == StartupStatus.Enabled;
            return;
        }

        actualStartupStatus = status;
        StartWithWindows = status == StartupStatus.Enabled;
        OnPropertyChanged(nameof(ActualStartupStatus));
        OnPropertyChanged(nameof(IsStartupActuallyEnabled));
        OnPropertyChanged(nameof(StartupStatusText));
    }

    private static string FormatReset(WindowReading? reading)
    {
        if (reading is null)
        {
            return "주간 한도 정보를 확인할 수 없습니다.";
        }

        var resetAt = DateTimeOffset.FromUnixTimeSeconds(reading.ResetsAt)
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);
        return $"다음 갱신 예정 · {resetAt}";
    }

    private static string FormatOverallStatus(MonitorSnapshot snapshot)
    {
        if (snapshot.ActionKind == CycleActionKind.ResetPending)
        {
            return snapshot.IsFailure
                ? "초기화 결과 확인 대기 · 이번 사용량 조회에 실패했습니다. 같은 처리 요청으로 자동 재시도합니다."
                : "초기화 결과 확인 대기 · 같은 처리 요청으로 다음 조회 때 자동 재시도합니다.";
        }

        if (snapshot.IsFailure)
        {
            return "조회 또는 판단이 불명확해 안전하게 중단했습니다.";
        }

        if (snapshot.ActionKind == CycleActionKind.ResetSucceeded)
        {
            return "초기화권 처리를 완료했습니다.";
        }

        if (snapshot.ActionKind == CycleActionKind.ResetNoEffect)
        {
            return "초기화권 요청을 마쳤지만 초기화할 사용량이 없거나 사용할 수 있는 초기화권이 없었습니다.";
        }

        if (!snapshot.Settings.AutomationEnabled)
        {
            return "사용량만 확인 중 · 자동 초기화 꺼짐";
        }

        return snapshot.StatusCode switch
        {
            "waiting" => "사용량을 확인할 예정입니다.",
            "no_action" => $"자동 초기화 켜짐 · 주간 잔여량 {snapshot.Settings.RemainingThresholdPercent}% 이하 감시 · 현재 초기화 조건이 아닙니다.",
            "duplicate_suppressed" => "이 주간 사용량 구간은 이미 처리했습니다.",
            _ => "자동 초기화 켜짐 · 사용량 상태를 확인했습니다.",
        };
    }

    private static string ToFriendlySettingsFailure(string code) => code switch
    {
        "threshold_out_of_range" => "잔여량 임계값은 1~100%로 설정하세요.",
        "poll_interval_out_of_range" => "조회 주기는 1~60분으로 설정하세요.",
        _ => $"설정값이 올바르지 않습니다: {code}",
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
