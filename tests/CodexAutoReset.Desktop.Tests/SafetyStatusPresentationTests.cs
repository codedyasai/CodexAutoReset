using System.IO;
using System.Reflection;
using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Desktop.Tests;

[TestClass]
public sealed class SafetyStatusPresentationTests
{
    public static IEnumerable<object[]> SafetyStatuses
    {
        get
        {
            yield return
            [
                "live_recovery_pending",
                "초기화권 사용 후 주간 잔여량 회복을 확인하고 있습니다. 확인 전에는 추가 초기화권을 사용하지 않습니다.",
                "안전 대기 · 초기화 후 잔여량 회복 확인 중",
                false,
            ];
            yield return
            [
                "usage_reset_settling",
                "사용량 초기화를 감지했습니다. 최신 잔여량이 안정적으로 반영될 때까지 초기화권 자동 사용을 잠시 보류합니다.",
                "안전 대기 · 사용량 초기화 반영 확인 중",
                false,
            ];
            yield return
            [
                "usage_reset_state_unavailable",
                "사용량 초기화 확인 기록을 읽을 수 없어 초기화권 자동 사용을 안전하게 중단했습니다. 문제가 계속되면 앱을 다시 시작하고 로컬 설정 데이터의 접근 권한을 확인하세요.",
                "안전 차단 · 초기화 확인 기록 오류",
                true,
            ];
            yield return
            [
                "scheduled_reset_imminent",
                "정기 초기화 시각이 임박해 초기화권을 사용하지 않고 다음 사용량 갱신을 기다립니다.",
                "안전 대기 · 정기 초기화 임박",
                false,
            ];
        }
    }

    [TestMethod]
    [DynamicData(nameof(SafetyStatuses))]
    public async Task SafetyStatus_IsExplainedInMainWindowAndTray(
        string statusCode,
        string expectedMainWindowStatus,
        string expectedTrayStatus,
        bool isFailure)
    {
        await using var fixture = new ViewModelFixture();
        var snapshot = MonitorSnapshot.Waiting(GuardSettings.Default) with
        {
            ActionKind = isFailure ? CycleActionKind.Blocked : CycleActionKind.None,
            StatusCode = statusCode,
            IsFailure = isFailure,
        };

        ApplySnapshot(fixture.ViewModel, snapshot);

        Assert.IsTrue(fixture.ViewModel.IsOverallStatusVisible);
        Assert.AreEqual(expectedMainWindowStatus, fixture.ViewModel.OverallStatus);
        Assert.AreEqual(expectedTrayStatus, FormatTrayStatus(statusCode));
    }

    [TestMethod]
    public async Task ThresholdAtOneHundred_IsRejectedWithCurrentRange()
    {
        await using var fixture = new ViewModelFixture();
        fixture.ViewModel.ThresholdText = "100";

        var saved = await fixture.ViewModel.SaveThresholdAsync();

        Assert.IsFalse(saved);
        Assert.AreEqual(
            "잔여량 임계값은 1~99%의 정수로 입력하세요.",
            fixture.ViewModel.SaveStatus);
    }

    [TestMethod]
    public async Task ThresholdAtNinetyNine_IsAcceptedAndPersisted()
    {
        await using var fixture = new ViewModelFixture();
        fixture.ViewModel.ThresholdText = "99";

        var saved = await fixture.ViewModel.SaveThresholdAsync();
        var persisted = await fixture.SettingsStore.LoadAsync(
            CancellationToken.None);

        Assert.IsTrue(saved);
        Assert.AreEqual(99, persisted.RemainingThresholdPercent);
        Assert.AreEqual(
            "잔여량 임계값을 99%로 적용했습니다.",
            fixture.ViewModel.SaveStatus);
    }

    private static void ApplySnapshot(
        MainWindowViewModel viewModel,
        MonitorSnapshot snapshot)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ApplySnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        method.Invoke(viewModel, [snapshot]);
    }

    private static string FormatTrayStatus(string statusCode)
    {
        var method = typeof(TrayIconHost).GetMethod(
            "FormatStatus",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        return (string)method.Invoke(null, [statusCode])!;
    }

    private sealed class ViewModelFixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly GuardMonitorService monitor;

        public ViewModelFixture()
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                $"CodexAutoReset-safety-view-model-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            SettingsStore = new JsonSettingsStore(
                Path.Combine(directory, "settings.json"));
            SettingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            monitor = new GuardMonitorService(
                SettingsStore,
                new NoOpCycleExecutor(),
                GuardSettings.Default);
            ViewModel = new MainWindowViewModel(
                SettingsStore,
                new StartupService(new EmptyRegistryStore()),
                monitor,
                GuardSettings.Default,
                () => null);
        }

        public JsonSettingsStore SettingsStore { get; }

        public MainWindowViewModel ViewModel { get; }

        public async ValueTask DisposeAsync()
        {
            await monitor.DisposeAsync();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class NoOpCycleExecutor : IGuardCycleExecutor
    {
        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyRegistryStore : ICurrentUserRegistryStore
    {
        public CurrentUserRegistryValue ReadValue(string subKey, string valueName) =>
            CurrentUserRegistryValue.Missing;

        public void SetString(string subKey, string valueName, string value) =>
            throw new NotSupportedException();

        public void DeleteValue(string subKey, string valueName) =>
            throw new NotSupportedException();
    }
}
