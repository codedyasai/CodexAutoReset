using System.Globalization;
using System.IO;
using System.Reflection;
using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Desktop.Tests;

[TestClass]
public sealed class CompatibilityPresentationTests
{
    [TestMethod]
    public async Task ReadUnsupported_ShowsHardWarningAndLastSuccessfulObservation()
    {
        await using var fixture = new ViewModelFixture();
        var lastSuccess = new DateTimeOffset(
            2026,
            7,
            28,
            1,
            23,
            0,
            TimeSpan.Zero);
        var snapshot = MonitorSnapshot.Waiting(GuardSettings.Default) with
        {
            ActionKind = CycleActionKind.Blocked,
            StatusCode = "invalid_response",
            IsFailure = true,
            CompatibilityState = CodexCompatibilityState.ReadUnsupported,
            LastSuccessfulObservationAt = lastSuccess,
        };

        ApplySnapshot(fixture.ViewModel, snapshot);

        Assert.IsTrue(fixture.ViewModel.IsCompatibilityWarning);
        Assert.IsTrue(fixture.ViewModel.IsOverallStatusTitleVisible);
        Assert.AreEqual(
            "현재 Codex 응답을 지원하지 않습니다",
            fixture.ViewModel.OverallStatusTitle);
        Assert.AreEqual(
            "Codex 응답을 이 버전의 CodexAutoReset이 안전하게 해석할 수 없습니다. 주간 사용량 확인과 초기화권 자동 사용을 중단했습니다. CodexAutoReset 업데이트를 확인해 주세요.",
            fixture.ViewModel.OverallStatus);
        Assert.AreEqual("—", fixture.ViewModel.WeeklyRemainingText);
        Assert.AreEqual(
            "주간 한도 정보를 확인할 수 없습니다.",
            fixture.ViewModel.WeeklyResetStatus);
        Assert.AreEqual(
            lastSuccess.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            fixture.ViewModel.LastCheckedStatus);
    }

    [TestMethod]
    public async Task MutationUnverified_KeepsReadableUsageAndShowsMutationWarning()
    {
        await using var fixture = new ViewModelFixture();
        var weekly = new WindowReading(
            37,
            63,
            10_080,
            10_080,
            DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());
        var snapshot = MonitorSnapshot.Waiting(GuardSettings.Default) with
        {
            Weekly = weekly,
            ActionKind = CycleActionKind.Blocked,
            StatusCode = "mutation_unverified",
            IsFailure = true,
            CompatibilityState = CodexCompatibilityState.MutationUnverified,
            LastSuccessfulObservationAt = DateTimeOffset.UtcNow,
        };

        ApplySnapshot(fixture.ViewModel, snapshot);

        Assert.IsTrue(fixture.ViewModel.IsCompatibilityWarning);
        Assert.AreEqual(
            "자동 초기화 호환성 확인 필요",
            fixture.ViewModel.OverallStatusTitle);
        Assert.AreEqual(
            "주간 사용량은 정상적으로 확인되지만, 현재 Codex 버전의 초기화권 처리 형식은 검증되지 않았습니다. 안전을 위해 초기화권 자동 사용을 중단했습니다. CodexAutoReset 업데이트를 확인해 주세요.",
            fixture.ViewModel.OverallStatus);
        Assert.AreEqual("63%", fixture.ViewModel.WeeklyRemainingText);
        Assert.AreEqual(63, fixture.ViewModel.WeeklyRemainingPercent);
    }

    [TestMethod]
    public async Task VerificationPending_ShowsOnlyTheAgreedRetryWarning()
    {
        await using var fixture = new ViewModelFixture();
        var snapshot = MonitorSnapshot.Waiting(GuardSettings.Default) with
        {
            ActionKind = CycleActionKind.Blocked,
            StatusCode = "compatibility_verification_pending",
            IsFailure = true,
            CompatibilityState = CodexCompatibilityState.VerificationPending,
        };

        ApplySnapshot(fixture.ViewModel, snapshot);

        Assert.IsTrue(fixture.ViewModel.IsCompatibilityWarning);
        Assert.IsFalse(fixture.ViewModel.IsOverallStatusTitleVisible);
        Assert.AreEqual(string.Empty, fixture.ViewModel.OverallStatusTitle);
        Assert.AreEqual(
            "Codex 응답을 다시 확인하고 있습니다. 안전을 위해 이번 자동 초기화는 실행하지 않습니다.",
            fixture.ViewModel.OverallStatus);
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

    private sealed class ViewModelFixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly GuardMonitorService monitor;

        public ViewModelFixture()
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                $"CodexAutoReset-compatibility-view-model-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var settingsStore = new JsonSettingsStore(
                Path.Combine(directory, "settings.json"));
            monitor = new GuardMonitorService(
                settingsStore,
                new NoOpCycleExecutor(),
                GuardSettings.Default);
            ViewModel = new MainWindowViewModel(
                settingsStore,
                new StartupService(new EmptyRegistryStore()),
                monitor,
                GuardSettings.Default,
                () => null);
        }

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
