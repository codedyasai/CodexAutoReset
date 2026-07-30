using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using CodexAutoReset.AppServer;
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
    private string fiveHourThresholdText;
    private string pollIntervalText;
    private string? codexExecutablePath;
    private bool automationEnabled;
    private bool fiveHourAutomationEnabled;
    private bool notifyOnUsageReset;
    private bool startWithWindows;
    private string weeklyRemainingText = "-";
    private double weeklyRemainingPercent;
    private string weeklyResetStatus = "다음 갱신 예정 · -";
    private string fiveHourRemainingText = "-";
    private double fiveHourRemainingPercent;
    private string fiveHourResetStatus = "다음 갱신 예정 · -";
    private string creditStatus = "-";
    private string lastCheckedStatus = "확인 전";
    private string overallStatusTitle = string.Empty;
    private string overallStatus = string.Empty;
    private bool isCompatibilityWarning;
    private string saveStatus = string.Empty;
    private string codexConnectionStatus = string.Empty;
    private bool isCodexConnectionSaving;
    private bool isRefreshing;
    private StartupStatus? actualStartupStatus;
    private MonitorSnapshot currentSnapshot;

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
        thresholdText = FormatThreshold(
            initialSettings.RemainingThresholdPercent);
        fiveHourThresholdText = FormatThreshold(
            initialSettings.FiveHourRemainingThresholdPercent);
        pollIntervalText = initialSettings.PollIntervalMinutes.ToString(
            CultureInfo.InvariantCulture);
        codexExecutablePath = initialSettings.CodexExecutablePath;
        automationEnabled =
            initialSettings.IsAutomationEnabled(TriggerLimit.Weekly);
        fiveHourAutomationEnabled =
            initialSettings.IsAutomationEnabled(TriggerLimit.FiveHour);
        notifyOnUsageReset = initialSettings.NotifyOnUsageReset;
        startWithWindows = initialSettings.StartWithWindows;
        currentSnapshot = monitor.CurrentSnapshot;

        TryRefreshStartupState();
        monitor.SnapshotChanged += OnSnapshotChanged;
        ApplySnapshot(currentSnapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ThresholdText
    {
        get => thresholdText;
        set => SetField(ref thresholdText, value);
    }

    public string FiveHourThresholdText
    {
        get => fiveHourThresholdText;
        set => SetField(ref fiveHourThresholdText, value);
    }

    public string PollIntervalText
    {
        get => pollIntervalText;
        set => SetField(ref pollIntervalText, value);
    }

    public string? ConfiguredCodexExecutablePath => codexExecutablePath;

    public bool HasCustomCodexExecutablePath => codexExecutablePath is not null;

    public string CodexExecutableDisplayText
    {
        get
        {
            var resolvedPath = codexExecutablePath
                ?? CodexExecutableLocator.TryGetFilePickerExecutablePath(
                    configuredPath: null);
            return resolvedPath is null
                ? "Codex.exe 경로를 확인할 수 없습니다."
                : FormatExecutablePathForDisplay(resolvedPath);
        }
    }

    public bool AutomationEnabled
    {
        get => automationEnabled;
        set
        {
            if (SetField(ref automationEnabled, value))
            {
                OnPropertyChanged(nameof(RequiresAutomationEnableConfirmation));
                OnPropertyChanged(nameof(AnyAutomationEnabled));
            }
        }
    }

    public bool RequiresAutomationEnableConfirmation =>
        AutomationEnabled
        && !persistedSettings.IsAutomationEnabled(TriggerLimit.Weekly);

    public bool FiveHourAutomationEnabled
    {
        get => fiveHourAutomationEnabled;
        set
        {
            if (SetField(ref fiveHourAutomationEnabled, value))
            {
                OnPropertyChanged(
                    nameof(RequiresFiveHourAutomationEnableConfirmation));
                OnPropertyChanged(nameof(AnyAutomationEnabled));
            }
        }
    }

    public bool RequiresFiveHourAutomationEnableConfirmation =>
        FiveHourAutomationEnabled
        && !persistedSettings.IsAutomationEnabled(
            TriggerLimit.FiveHour);

    public bool AnyAutomationEnabled =>
        AutomationEnabled || FiveHourAutomationEnabled;

    public bool NotifyOnUsageReset
    {
        get => notifyOnUsageReset;
        set => SetField(ref notifyOnUsageReset, value);
    }

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

    public string FiveHourRemainingText
    {
        get => fiveHourRemainingText;
        private set => SetField(ref fiveHourRemainingText, value);
    }

    public double FiveHourRemainingPercent
    {
        get => fiveHourRemainingPercent;
        private set => SetField(ref fiveHourRemainingPercent, value);
    }

    public string FiveHourResetStatus
    {
        get => fiveHourResetStatus;
        private set => SetField(ref fiveHourResetStatus, value);
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
        private set
        {
            if (SetField(ref overallStatus, value))
            {
                OnPropertyChanged(nameof(IsOverallStatusVisible));
            }
        }
    }

    public bool IsOverallStatusVisible =>
        !string.IsNullOrWhiteSpace(OverallStatus);

    public string OverallStatusTitle
    {
        get => overallStatusTitle;
        private set
        {
            if (SetField(ref overallStatusTitle, value))
            {
                OnPropertyChanged(nameof(IsOverallStatusTitleVisible));
            }
        }
    }

    public bool IsOverallStatusTitleVisible =>
        !string.IsNullOrWhiteSpace(OverallStatusTitle);

    public bool IsCompatibilityWarning
    {
        get => isCompatibilityWarning;
        private set => SetField(ref isCompatibilityWarning, value);
    }

    public string SaveStatus
    {
        get => saveStatus;
        private set => SetField(ref saveStatus, value);
    }

    public string CodexConnectionStatus
    {
        get => codexConnectionStatus;
        private set => SetField(ref codexConnectionStatus, value);
    }

    public bool CanEditCodexConnection => !isCodexConnectionSaving;

    public bool IsRefreshing
    {
        get => isRefreshing;
        private set
        {
            if (SetField(ref isRefreshing, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(IsRefreshAnimationActive));
                OnPropertyChanged(nameof(RefreshAutomationName));
                OnPropertyChanged(nameof(RefreshStatusText));
            }
        }
    }

    public bool CanRefresh => !IsRefreshing;

    public bool IsRefreshAnimationActive =>
        IsRefreshing && System.Windows.SystemParameters.ClientAreaAnimation;

    public string RefreshAutomationName => IsRefreshing
        ? "사용량 새로고침 중"
        : "사용량 새로고침";

    public string RefreshStatusText => IsRefreshing
        ? "확인 중…"
        : string.Empty;

    public MonitorSnapshot CurrentSnapshot => currentSnapshot;

    public void RequestRefresh() => monitor.RequestRefresh();

    public async Task RefreshNowAsync()
    {
        if (Volatile.Read(ref stopping) != 0 || IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await monitor.RefreshAsync(CancellationToken.None);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public bool TrySetCodexExecutablePath(string? path, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(
                Path.GetFileName(path),
                "codex.exe",
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            errorMessage = "파일 이름이 codex.exe인 실제 실행 파일을 선택하세요.";
            SaveStatus = errorMessage;
            CodexConnectionStatus = errorMessage;
            return false;
        }

        try
        {
            SetCodexExecutablePath(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is
            ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
        {
            errorMessage = "선택한 실행 파일 경로를 안전하게 확인할 수 없습니다.";
            SaveStatus = errorMessage;
            CodexConnectionStatus = errorMessage;
            return false;
        }

        errorMessage = string.Empty;
        CodexConnectionStatus = "Codex 연결 경로를 저장하는 중입니다.";
        return true;
    }

    public void UseAutomaticCodexExecutablePath()
    {
        SetCodexExecutablePath(null);
        OnPropertyChanged(nameof(CodexExecutableDisplayText));
        CodexConnectionStatus = "자동 찾기 설정을 저장하는 중입니다.";
    }

    public async Task<bool> SaveCodexExecutablePathAsync()
    {
        if (Volatile.Read(ref stopping) != 0)
        {
            return false;
        }

        if (isCodexConnectionSaving)
        {
            CodexConnectionStatus = "Codex 연결 설정을 저장 중입니다.";
            return false;
        }

        SetCodexConnectionSaving(true);
        var gateAcquired = false;
        try
        {
            await saveGate.WaitAsync();
            gateAcquired = true;
            if (Volatile.Read(ref stopping) != 0)
            {
                RestorePersistedCodexExecutablePath();
                return false;
            }

            var baselineSettings = persistedSettings;
            try
            {
                var updatedSettings = await monitor.SetCodexExecutablePathAsync(
                    settingsUpdateService,
                    codexExecutablePath,
                    CancellationToken.None);
                MergeFreshPersistedSettings(baselineSettings, updatedSettings);
                SaveStatus = "Codex 연결 설정을 저장했습니다.";
                CodexConnectionStatus = "연결 경로를 저장했습니다. 다음 확인부터 사용합니다.";
                return true;
            }
            catch (SettingsConflictException exception)
            {
                MergeFreshPersistedSettings(
                    baselineSettings,
                    exception.CurrentSettings);
                SaveStatus = "설정 파일이 다시 변경되어 Codex 연결 설정을 저장하지 않았습니다.";
                RestorePersistedCodexExecutablePath();
                CodexConnectionStatus = "다른 설정 변경을 먼저 반영했습니다. 연결 경로를 다시 선택하세요.";
            }
            catch (SettingsException exception)
            {
                SaveStatus = $"Codex 연결 설정 저장 실패: {ToFriendlySettingsFailure(exception.ReasonCode)}";
                RestorePersistedCodexExecutablePath();
                CodexConnectionStatus = SaveStatus;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                SaveStatus = "Codex 연결 설정을 안전하게 저장하지 못했습니다.";
                RestorePersistedCodexExecutablePath();
                CodexConnectionStatus = SaveStatus;
            }
            catch (Exception)
            {
                SaveStatus = "예상하지 못한 로컬 오류로 Codex 연결 설정을 저장하지 않았습니다.";
                RestorePersistedCodexExecutablePath();
                CodexConnectionStatus = SaveStatus;
            }

            return false;
        }
        finally
        {
            if (gateAcquired)
            {
                saveGate.Release();
            }

            SetCodexConnectionSaving(false);
        }
    }

    public async Task<bool> SetAutomationEnabledAsync(
        bool enabled,
        bool automationEnableConfirmed = false) =>
        await SetAutomationEnabledAsync(
            TriggerLimit.Weekly,
            enabled,
            automationEnableConfirmed);

    public async Task<bool> SetFiveHourAutomationEnabledAsync(
        bool enabled,
        bool automationEnableConfirmed = false) =>
        await SetAutomationEnabledAsync(
            TriggerLimit.FiveHour,
            enabled,
            automationEnableConfirmed);

    private async Task<bool> SetAutomationEnabledAsync(
        TriggerLimit triggerLimit,
        bool enabled,
        bool automationEnableConfirmed)
    {
        if (Volatile.Read(ref stopping) != 0)
        {
            return false;
        }

        var persistedEnabled =
            persistedSettings.IsAutomationEnabled(triggerLimit);
        if (enabled
            && !persistedEnabled
            && !automationEnableConfirmed)
        {
            SetAutomationEnabled(triggerLimit, persistedEnabled);
            SaveStatus = "초기화권 자동 사용을 켜려면 초기화권 사용 가능성을 확인해야 합니다.";
            return false;
        }

        SetAutomationEnabled(triggerLimit, enabled);
        await saveGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                SetAutomationEnabled(
                    triggerLimit,
                    persistedSettings.IsAutomationEnabled(triggerLimit));
                return false;
            }

            var baselineSettings = persistedSettings;
            try
            {
                var updatedSettings = await monitor.SetAutomationEnabledAsync(
                    settingsUpdateService,
                    triggerLimit,
                    enabled,
                    CancellationToken.None);
                MergeFreshPersistedSettings(baselineSettings, updatedSettings);
                SetAutomationEnabled(
                    triggerLimit,
                    updatedSettings.IsAutomationEnabled(triggerLimit));
                SaveStatus = string.Empty;
                return true;
            }
            catch (SettingsException exception)
            {
                SaveStatus = ToFriendlySettingsFailure(exception.ReasonCode);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                SaveStatus = "초기화권 자동 사용 설정을 안전하게 적용하지 못했습니다.";
            }
            catch (Exception)
            {
                SaveStatus = "예상하지 못한 로컬 오류로 초기화권 자동 사용 설정을 적용하지 않았습니다.";
            }

            SetAutomationEnabled(
                triggerLimit,
                persistedSettings.IsAutomationEnabled(triggerLimit));
            return false;
        }
        finally
        {
            saveGate.Release();
        }
    }

    public async Task<bool> SetNotifyOnUsageResetAsync(bool enabled)
    {
        if (Volatile.Read(ref stopping) != 0)
        {
            return false;
        }

        NotifyOnUsageReset = enabled;
        await saveGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                NotifyOnUsageReset = persistedSettings.NotifyOnUsageReset;
                return false;
            }

            var baselineSettings = persistedSettings;
            try
            {
                var updatedSettings = await monitor.SetNotifyOnUsageResetAsync(
                    settingsUpdateService,
                    enabled,
                    CancellationToken.None);
                MergeFreshPersistedSettings(baselineSettings, updatedSettings);
                NotifyOnUsageReset = updatedSettings.NotifyOnUsageReset;
                SaveStatus = string.Empty;
                return true;
            }
            catch (SettingsException exception)
            {
                SaveStatus = ToFriendlySettingsFailure(exception.ReasonCode);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                SaveStatus = "사용량 초기화 알림 설정을 안전하게 적용하지 못했습니다.";
            }
            catch (Exception)
            {
                SaveStatus = "예상하지 못한 로컬 오류로 사용량 초기화 알림 설정을 적용하지 않았습니다.";
            }

            NotifyOnUsageReset = persistedSettings.NotifyOnUsageReset;
            return false;
        }
        finally
        {
            saveGate.Release();
        }
    }

    public bool RequiresThresholdChangeConfirmation() =>
        RequiresThresholdChangeConfirmation(TriggerLimit.Weekly);

    public bool RequiresFiveHourThresholdChangeConfirmation() =>
        RequiresThresholdChangeConfirmation(TriggerLimit.FiveHour);

    private bool RequiresThresholdChangeConfirmation(TriggerLimit triggerLimit)
    {
        return int.TryParse(
                GetThresholdText(triggerLimit),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var threshold)
            && threshold >= GuardSettings.MinimumThreshold
            && threshold <= GuardSettings.MaximumThreshold
            && IsPotentialImmediateResetThreshold(triggerLimit, threshold);
    }

    public void CancelThresholdChange() =>
        CancelThresholdChange(TriggerLimit.Weekly);

    public void CancelFiveHourThresholdChange() =>
        CancelThresholdChange(TriggerLimit.FiveHour);

    private void CancelThresholdChange(TriggerLimit triggerLimit)
    {
        SetThresholdText(
            triggerLimit,
            FormatThreshold(
                persistedSettings.GetRemainingThresholdPercent(
                    triggerLimit)));
        SaveStatus = "잔여량 임계값을 변경하지 않았습니다.";
    }

    public async Task<bool> SaveThresholdAsync(
        bool immediateResetRiskConfirmed = false) =>
        await SaveThresholdAsync(
            TriggerLimit.Weekly,
            immediateResetRiskConfirmed);

    public async Task<bool> SaveFiveHourThresholdAsync(
        bool immediateResetRiskConfirmed = false) =>
        await SaveThresholdAsync(
            TriggerLimit.FiveHour,
            immediateResetRiskConfirmed);

    private async Task<bool> SaveThresholdAsync(
        TriggerLimit triggerLimit,
        bool immediateResetRiskConfirmed)
    {
        var requestedText = GetThresholdText(triggerLimit);
        if (!TryParseThreshold(
                requestedText,
                allowEmpty: true,
                out var threshold))
        {
            SaveStatus =
                triggerLimit == TriggerLimit.FiveHour
                    ? $"5시간 잔여량 임계값은 공란 또는 {GuardSettings.MinimumThreshold}~{GuardSettings.MaximumThreshold}%의 정수로 입력하세요."
                    : $"주간 잔여량 임계값은 공란 또는 {GuardSettings.MinimumThreshold}~{GuardSettings.MaximumThreshold}%의 정수로 입력하세요.";
            return false;
        }

        if (Volatile.Read(ref stopping) != 0)
        {
            return false;
        }

        await saveGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                return false;
            }

            if (threshold
                == persistedSettings.GetRemainingThresholdPercent(triggerLimit))
            {
                return true;
            }

            if (threshold is { } requestedThreshold
                && IsPotentialImmediateResetThreshold(
                    triggerLimit,
                    requestedThreshold)
                && !immediateResetRiskConfirmed)
            {
                SaveStatus = "이 임계값을 적용하면 초기화권이 바로 사용될 수 있어 확인이 필요합니다.";
                RestoreThresholdTextAfterFailedSave(
                    triggerLimit,
                    requestedText);
                return false;
            }

            var baselineSettings = persistedSettings;
            try
            {
                var updatedSettings =
                    await monitor.SetRemainingThresholdPercentAsync(
                        settingsUpdateService,
                        triggerLimit,
                        threshold,
                        CancellationToken.None);
                MergeFreshPersistedSettings(
                    baselineSettings,
                    updatedSettings,
                    expectedThresholdText:
                        triggerLimit == TriggerLimit.Weekly
                            ? requestedText
                            : null,
                    expectedFiveHourThresholdText:
                        triggerLimit == TriggerLimit.FiveHour
                            ? requestedText
                            : null);
                var limitLabel = triggerLimit == TriggerLimit.FiveHour
                    ? "5시간 한도"
                    : "주간 한도";
                SaveStatus = threshold is { } savedThreshold
                    ? $"{limitLabel} 잔여량 임계값을 {savedThreshold}%로 적용했습니다."
                    : string.Empty;
                return true;
            }
            catch (SettingsException exception)
            {
                SaveStatus = ToFriendlySettingsFailure(exception.ReasonCode);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                SaveStatus = "잔여량 임계값을 안전하게 적용하지 못했습니다.";
            }
            catch (Exception)
            {
                SaveStatus = "예상하지 못한 로컬 오류로 잔여량 임계값을 적용하지 않았습니다.";
            }

            RestoreThresholdTextAfterFailedSave(
                triggerLimit,
                requestedText);
            return false;
        }
        finally
        {
            saveGate.Release();
        }
    }

    public async Task<bool> SavePollIntervalAsync()
    {
        var requestedText = PollIntervalText;
        if (!int.TryParse(
                requestedText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var pollInterval)
            || pollInterval is < 1 or > 60)
        {
            SaveStatus = "확인 주기는 1~60분의 정수로 입력하세요.";
            return false;
        }

        if (Volatile.Read(ref stopping) != 0)
        {
            return false;
        }

        await saveGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                return false;
            }

            if (pollInterval == persistedSettings.PollIntervalMinutes)
            {
                return true;
            }

            var baselineSettings = persistedSettings;
            try
            {
                var updatedSettings = await monitor.SetPollIntervalMinutesAsync(
                    settingsUpdateService,
                    pollInterval,
                    CancellationToken.None);
                MergeFreshPersistedSettings(
                    baselineSettings,
                    updatedSettings,
                    expectedPollIntervalText: requestedText);
                SaveStatus = $"확인 주기를 {pollInterval}분으로 적용했습니다.";
                return true;
            }
            catch (SettingsException exception)
            {
                SaveStatus = ToFriendlySettingsFailure(exception.ReasonCode);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                SaveStatus = "확인 주기를 안전하게 적용하지 못했습니다.";
            }
            catch (Exception)
            {
                SaveStatus = "예상하지 못한 로컬 오류로 확인 주기를 적용하지 않았습니다.";
            }

            RestoreNumericTextAfterFailedSave(
                requestedText,
                isThreshold: false);
            return false;
        }
        finally
        {
            saveGate.Release();
        }
    }

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
            if ((RequiresAutomationEnableConfirmation
                    || RequiresFiveHourAutomationEnableConfirmation)
                && !automationEnableConfirmed)
            {
                SaveStatus = "초기화권 자동 사용을 켜려면 초기화권 사용 가능성을 확인해야 합니다.";
                return;
            }

            if (!TryParseThreshold(
                    ThresholdText,
                    allowEmpty: true,
                    out var weeklyThreshold)
                || !TryParseThreshold(
                    FiveHourThresholdText,
                    allowEmpty: true,
                    out var fiveHourThreshold))
            {
                SaveStatus =
                    $"주간·5시간 임계값은 공란 또는 {GuardSettings.MinimumThreshold}~{GuardSettings.MaximumThreshold}% 정수로 입력하세요.";
                return;
            }

            var settings = persistedSettings with
            {
                RemainingThresholdPercent = weeklyThreshold,
                FiveHourRemainingThresholdPercent = fiveHourThreshold,
                PollIntervalMinutes = GuardSettings.FixedPollIntervalMinutes,
                StartWithWindows = StartWithWindows,
                CodexExecutablePath = codexExecutablePath,
                AutomationEnabled =
                    weeklyThreshold.HasValue
                    && AutomationEnabled,
                FiveHourAutomationEnabled =
                    fiveHourThreshold.HasValue
                    && FiveHourAutomationEnabled,
                NotifyOnUsageReset = NotifyOnUsageReset,
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
        if (Volatile.Read(ref stopping) != 0)
        {
            return;
        }

        await saveGate.WaitAsync();
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
                SaveStatus = string.Empty;
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
        try
        {
            await TryPersistPendingNumericSettingsOnShutdownAsync();
        }
        finally
        {
            saveGate.Release();
        }
    }

    public void CancelAutomationEnable()
    {
        AutomationEnabled =
            persistedSettings.IsAutomationEnabled(TriggerLimit.Weekly);
        SaveStatus = "초기화권 자동 사용을 켜지 않았습니다.";
    }

    public void CancelFiveHourAutomationEnable()
    {
        FiveHourAutomationEnabled =
            persistedSettings.IsAutomationEnabled(
                TriggerLimit.FiveHour);
        SaveStatus = "초기화권 자동 사용을 켜지 않았습니다.";
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
        currentSnapshot = snapshot;
        WeeklyRemainingText = snapshot.Weekly is null
            ? "-"
            : $"{snapshot.Weekly.RemainingPercent:F0}%";
        WeeklyRemainingPercent = snapshot.Weekly is null
            ? 0
            : Math.Clamp(snapshot.Weekly.RemainingPercent, 0, 100);
        WeeklyResetStatus = FormatReset(snapshot.Weekly);
        if (snapshot.FiveHour is not null)
        {
            FiveHourRemainingText =
                $"{snapshot.FiveHour.RemainingPercent:F0}%";
            FiveHourRemainingPercent =
                Math.Clamp(snapshot.FiveHour.RemainingPercent, 0, 100);
            FiveHourResetStatus = FormatReset(snapshot.FiveHour);
        }
        else if (snapshot.IsFailure
            || IsCompatibilityWarningState(snapshot.CompatibilityState))
        {
            FiveHourRemainingText = "-";
            FiveHourRemainingPercent = 0;
            FiveHourResetStatus = "다음 갱신 예정 · -";
        }
        else if (snapshot.LastSuccessfulObservationAt is not null)
        {
            FiveHourRemainingText = "-";
            FiveHourRemainingPercent = 0;
            FiveHourResetStatus = "다음 갱신 예정 · -";
        }
        else
        {
            FiveHourRemainingText = "-";
            FiveHourRemainingPercent = 0;
            FiveHourResetStatus = "다음 갱신 예정 · -";
        }
        CreditStatus = snapshot.AvailableCreditCount?.ToString(
            CultureInfo.InvariantCulture) ?? "-";
        LastCheckedStatus = snapshot.LastSuccessfulObservationAt is { } lastSuccess
            ? lastSuccess.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "확인 전";

        IsCompatibilityWarning = IsCompatibilityWarningState(
            snapshot.CompatibilityState);
        OverallStatusTitle = FormatOverallStatusTitle(snapshot);
        OverallStatus = FormatOverallStatus(snapshot);
        OnPropertyChanged(nameof(CurrentSnapshot));
    }

    private void ApplyPersistedSettings(GuardSettings settings)
    {
        persistedSettings = settings;
        ThresholdText = FormatThreshold(
            settings.RemainingThresholdPercent);
        FiveHourThresholdText = FormatThreshold(
            settings.FiveHourRemainingThresholdPercent);
        PollIntervalText = settings.PollIntervalMinutes.ToString(
            CultureInfo.InvariantCulture);
        SetCodexExecutablePath(settings.CodexExecutablePath);
        AutomationEnabled =
            settings.IsAutomationEnabled(TriggerLimit.Weekly);
        FiveHourAutomationEnabled =
            settings.IsAutomationEnabled(TriggerLimit.FiveHour);
        NotifyOnUsageReset = settings.NotifyOnUsageReset;
        OnPropertyChanged(nameof(RequiresAutomationEnableConfirmation));
        OnPropertyChanged(
            nameof(RequiresFiveHourAutomationEnableConfirmation));
        TryRefreshStartupState();
        monitor.RequestRefresh();
    }

    private void RestorePersistedCodexExecutablePath() =>
        SetCodexExecutablePath(persistedSettings.CodexExecutablePath);

    private async Task TryPersistPendingNumericSettingsOnShutdownAsync()
    {
        try
        {
            if (TryParseThreshold(
                    ThresholdText,
                    allowEmpty: true,
                    out var threshold)
                && threshold != persistedSettings.RemainingThresholdPercent)
            {
                persistedSettings =
                    await monitor.SetRemainingThresholdPercentAsync(
                        settingsUpdateService,
                        TriggerLimit.Weekly,
                        threshold,
                        CancellationToken.None);
            }

            if (TryParseThreshold(
                    FiveHourThresholdText,
                    allowEmpty: true,
                    out var fiveHourThreshold)
                && fiveHourThreshold
                    != persistedSettings.FiveHourRemainingThresholdPercent)
            {
                persistedSettings =
                    await monitor.SetRemainingThresholdPercentAsync(
                        settingsUpdateService,
                        TriggerLimit.FiveHour,
                        fiveHourThreshold,
                        CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Shutdown must not be blocked by a best-effort final settings flush.
        }
    }

    private void RestoreNumericTextAfterFailedSave(
        string requestedText,
        bool isThreshold)
    {
        if (isThreshold)
        {
            if (string.Equals(ThresholdText, requestedText, StringComparison.Ordinal))
            {
                ThresholdText = FormatThreshold(
                    persistedSettings.RemainingThresholdPercent);
            }

            return;
        }

        if (string.Equals(PollIntervalText, requestedText, StringComparison.Ordinal))
        {
            PollIntervalText = persistedSettings.PollIntervalMinutes.ToString(
                CultureInfo.InvariantCulture);
        }
    }

    private void RestoreThresholdTextAfterFailedSave(
        TriggerLimit triggerLimit,
        string requestedText)
    {
        if (string.Equals(
            GetThresholdText(triggerLimit),
            requestedText,
            StringComparison.Ordinal))
        {
            SetThresholdText(
                triggerLimit,
                FormatThreshold(
                    persistedSettings.GetRemainingThresholdPercent(
                        triggerLimit)));
        }
    }

    private void SetCodexConnectionSaving(bool value)
    {
        if (SetField(ref isCodexConnectionSaving, value))
        {
            OnPropertyChanged(nameof(CanEditCodexConnection));
        }
    }

    private bool IsPotentialImmediateResetThreshold(
        TriggerLimit triggerLimit,
        int requestedThreshold)
    {
        var remaining = triggerLimit switch
        {
            TriggerLimit.Weekly => CurrentSnapshot.Weekly?.RemainingPercent,
            TriggerLimit.FiveHour => CurrentSnapshot.FiveHour?.RemainingPercent,
            _ => null,
        };
        var persistedThreshold =
            persistedSettings.GetRemainingThresholdPercent(triggerLimit);
        return persistedSettings.IsAutomationEnabled(triggerLimit)
            && persistedThreshold is { } currentThreshold
            && CurrentSnapshot.AvailableCreditCount is > 0
            && requestedThreshold > currentThreshold
            && remaining is not null
            && currentThreshold < remaining.Value
            && requestedThreshold >= remaining.Value;
    }

    private void MergeFreshPersistedSettings(
        GuardSettings baselineSettings,
        GuardSettings freshSettings,
        string? expectedThresholdText = null,
        string? expectedFiveHourThresholdText = null,
        string? expectedPollIntervalText = null)
    {
        var thresholdWasUnedited = string.Equals(
            ThresholdText,
            expectedThresholdText
                ?? FormatThreshold(
                    baselineSettings.RemainingThresholdPercent),
            StringComparison.Ordinal);
        var fiveHourThresholdWasUnedited = string.Equals(
            FiveHourThresholdText,
            expectedFiveHourThresholdText
                ?? FormatThreshold(
                    baselineSettings.FiveHourRemainingThresholdPercent),
            StringComparison.Ordinal);
        var pollIntervalWasUnedited = string.Equals(
            PollIntervalText,
            expectedPollIntervalText
                ?? baselineSettings.PollIntervalMinutes.ToString(
                    CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        var automationWasUnedited =
            AutomationEnabled
                == baselineSettings.IsAutomationEnabled(
                    TriggerLimit.Weekly);
        var fiveHourAutomationWasUnedited =
            FiveHourAutomationEnabled
                == baselineSettings.IsAutomationEnabled(
                    TriggerLimit.FiveHour);
        var notificationWasUnedited =
            NotifyOnUsageReset == baselineSettings.NotifyOnUsageReset;
        var codexExecutableWasUnedited = string.Equals(
            codexExecutablePath,
            baselineSettings.CodexExecutablePath,
            StringComparison.OrdinalIgnoreCase);

        persistedSettings = freshSettings;
        if (thresholdWasUnedited)
        {
            ThresholdText = FormatThreshold(
                freshSettings.RemainingThresholdPercent);
        }

        if (fiveHourThresholdWasUnedited)
        {
            FiveHourThresholdText = FormatThreshold(
                freshSettings.FiveHourRemainingThresholdPercent);
        }

        if (pollIntervalWasUnedited)
        {
            PollIntervalText = freshSettings.PollIntervalMinutes.ToString(
                CultureInfo.InvariantCulture);
        }

        if (automationWasUnedited)
        {
            AutomationEnabled =
                freshSettings.IsAutomationEnabled(TriggerLimit.Weekly);
        }

        if (fiveHourAutomationWasUnedited)
        {
            FiveHourAutomationEnabled =
                freshSettings.IsAutomationEnabled(
                    TriggerLimit.FiveHour);
        }

        if (notificationWasUnedited)
        {
            NotifyOnUsageReset = freshSettings.NotifyOnUsageReset;
        }

        if (codexExecutableWasUnedited)
        {
            SetCodexExecutablePath(freshSettings.CodexExecutablePath);
        }

        OnPropertyChanged(nameof(RequiresAutomationEnableConfirmation));
        OnPropertyChanged(
            nameof(RequiresFiveHourAutomationEnableConfirmation));
    }

    private string GetThresholdText(TriggerLimit triggerLimit) =>
        triggerLimit switch
        {
            TriggerLimit.Weekly => ThresholdText,
            TriggerLimit.FiveHour => FiveHourThresholdText,
            _ => throw new ArgumentOutOfRangeException(nameof(triggerLimit)),
        };

    private void SetThresholdText(
        TriggerLimit triggerLimit,
        string value)
    {
        if (triggerLimit == TriggerLimit.Weekly)
        {
            ThresholdText = value;
            return;
        }

        if (triggerLimit == TriggerLimit.FiveHour)
        {
            FiveHourThresholdText = value;
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(triggerLimit));
    }

    private void SetAutomationEnabled(
        TriggerLimit triggerLimit,
        bool enabled)
    {
        if (triggerLimit == TriggerLimit.Weekly)
        {
            AutomationEnabled = enabled;
            return;
        }

        if (triggerLimit == TriggerLimit.FiveHour)
        {
            FiveHourAutomationEnabled = enabled;
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(triggerLimit));
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
            return "다음 갱신 예정 · -";
        }

        var resetAt = DateTimeOffset.FromUnixTimeSeconds(reading.ResetsAt)
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);
        return $"다음 갱신 예정 · {resetAt}";
    }

    private static string FormatOverallStatus(MonitorSnapshot snapshot)
    {
        var compatibilityStatus = snapshot.CompatibilityState switch
        {
            CodexCompatibilityState.VerificationPending =>
                "Codex 응답을 다시 확인하고 있습니다. 안전을 위해 이번 자동 초기화는 실행하지 않습니다.",
            CodexCompatibilityState.ReadUnsupported =>
                "Codex 응답을 이 버전의 CodexAutoReset이 안전하게 해석할 수 없습니다. 사용량 확인과 초기화권 자동 사용을 중단했습니다. CodexAutoReset 업데이트를 확인해 주세요.",
            CodexCompatibilityState.MutationUnverified =>
                "사용량은 정상적으로 확인되지만, 현재 Codex 버전의 초기화권 처리 형식은 검증되지 않았습니다. 안전을 위해 초기화권 자동 사용을 중단했습니다. CodexAutoReset 업데이트를 확인해 주세요.",
            _ => null,
        };
        if (compatibilityStatus is not null)
        {
            return compatibilityStatus;
        }

        if (snapshot.ActionKind == CycleActionKind.ResetPending)
        {
            return snapshot.IsFailure
                ? "초기화 결과 확인 대기 · 이번 사용량 조회에 실패했습니다. 같은 처리 요청으로 자동 재시도합니다."
                : "초기화 결과 확인 대기 · 같은 처리 요청으로 다음 조회 때 자동 재시도합니다.";
        }

        var safetyStatus = snapshot.StatusCode switch
        {
            "live_recovery_pending" =>
                "초기화권 사용 후 설정한 한도들의 잔여량 회복을 확인하고 있습니다. 확인 전에는 추가 초기화권을 사용하지 않습니다.",
            "usage_reset_settling" =>
                "사용량 초기화를 감지했습니다. 최신 잔여량이 안정적으로 반영될 때까지 초기화권 자동 사용을 잠시 보류합니다.",
            "usage_reset_state_unavailable" =>
                "사용량 초기화 확인 기록을 읽을 수 없어 초기화권 자동 사용을 안전하게 중단했습니다. 문제가 계속되면 앱을 다시 시작하고 로컬 설정 데이터의 접근 권한을 확인하세요.",
            "scheduled_reset_imminent" =>
                "정기 초기화 시각이 임박해 초기화권을 사용하지 않고 다음 사용량 갱신을 기다립니다.",
            _ => null,
        };
        if (safetyStatus is not null)
        {
            return safetyStatus;
        }

        if (snapshot.IsFailure)
        {
            return snapshot.StatusCode switch
            {
                "executable_not_found" =>
                    "Codex CLI를 찾지 못했습니다. CLI 설치를 확인하거나 Codex 연결에서 Codex.exe 직접 찾기를 사용하세요.",
                "executable_became_unavailable" =>
                    "Codex가 업데이트되어 실행 경로가 바뀐 것 같습니다. 잠시 후 다시 확인하세요.",
                "start_failed" =>
                    "Codex CLI를 시작하지 못했습니다. Codex가 정상 실행·로그인되는지 확인하세요.",
                _ => "조회 또는 판단이 불명확해 안전하게 중단했습니다.",
            };
        }

        if (snapshot.ActionKind == CycleActionKind.ResetSucceeded)
        {
            return "초기화권 처리를 완료했습니다.";
        }

        if (snapshot.ActionKind == CycleActionKind.ResetNoEffect)
        {
            return "초기화권 요청을 마쳤지만 초기화할 사용량이 없거나 사용할 수 있는 초기화권이 없었습니다.";
        }

        return snapshot.StatusCode switch
        {
            "duplicate_suppressed" => "이 사용량 한도 구간은 이미 처리했습니다.",
            _ => string.Empty,
        };
    }

    private static string FormatOverallStatusTitle(MonitorSnapshot snapshot) =>
        snapshot.CompatibilityState switch
        {
            CodexCompatibilityState.ReadUnsupported =>
                "현재 Codex 응답을 지원하지 않습니다",
            CodexCompatibilityState.MutationUnverified =>
                "자동 초기화 호환성 확인 필요",
            _ => string.Empty,
        };

    private static bool IsCompatibilityWarningState(
        CodexCompatibilityState state) => state is
            CodexCompatibilityState.VerificationPending
            or CodexCompatibilityState.ReadUnsupported
            or CodexCompatibilityState.MutationUnverified;

    private static string FormatThreshold(int? threshold) =>
        threshold?.ToString(CultureInfo.InvariantCulture)
        ?? string.Empty;

    private static bool TryParseThreshold(
        string text,
        bool allowEmpty,
        out int? threshold)
    {
        if (allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            threshold = null;
            return true;
        }

        if (int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            && parsed >= GuardSettings.MinimumThreshold
            && parsed <= GuardSettings.MaximumThreshold)
        {
            threshold = parsed;
            return true;
        }

        threshold = null;
        return false;
    }

    private static string ToFriendlySettingsFailure(string code) => code switch
    {
        "threshold_out_of_range" =>
            $"잔여량 임계값은 {GuardSettings.MinimumThreshold}~{GuardSettings.MaximumThreshold}%로 설정하세요.",
        "poll_interval_out_of_range" => "조회 주기는 1~60분으로 설정하세요.",
        "codex_executable_path_invalid" => "선택한 codex.exe를 찾을 수 없습니다. 다시 선택하세요.",
        "settings_access_denied" => "설정 파일에 접근할 수 없습니다. 앱 권한과 보안 프로그램을 확인하세요.",
        "settings_io_error" => "설정 파일을 읽거나 쓸 수 없습니다. 잠시 후 다시 시도하세요.",
        "settings_invalid_json" or "settings_empty" or "settings_schema_unsupported" =>
            "설정 파일이 손상되었거나 지원되지 않습니다.",
        "settings_path_invalid" or "settings_path_forbidden" =>
            "설정 파일 위치를 안전하게 사용할 수 없습니다.",
        "settings_too_large" => "설정 파일 크기가 비정상적으로 큽니다.",
        _ => "설정값을 저장할 수 없습니다.",
    };

    private void SetCodexExecutablePath(string? path)
    {
        if (!SetField(ref codexExecutablePath, path, nameof(ConfiguredCodexExecutablePath)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasCustomCodexExecutablePath));
        OnPropertyChanged(nameof(CodexExecutableDisplayText));
    }

    private static string FormatExecutablePathForDisplay(string path)
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (TryMakePrivacySafePath(path, localAppData, "%LOCALAPPDATA%", out var displayPath))
        {
            return displayPath;
        }

        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (TryMakePrivacySafePath(path, userProfile, "%USERPROFILE%", out displayPath))
        {
            return displayPath;
        }

        return $"…\\{Path.GetFileName(path)}";
    }

    private static bool TryMakePrivacySafePath(
        string path,
        string baseDirectory,
        string placeholder,
        out string displayPath)
    {
        displayPath = string.Empty;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(baseDirectory, path);
        if (Path.IsPathFullyQualified(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        displayPath = $"{placeholder}\\{relativePath}";
        return true;
    }

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
