using System.Globalization;
using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class CreditExpirationPresentationTests
{
    [TestMethod]
    public async Task MissingSummaryAndZeroCreditsHaveDistinctPlaceholders()
    {
        await using (var missing = await ViewModelFixture.CreateAsync(null))
        {
            Assert.AreEqual("-", missing.ViewModel.CreditStatus);
            CollectionAssert.AreEqual(
                new[] { "-" },
                missing.ViewModel.CreditExpirationItems.ToArray());
        }

        await using (var empty = await ViewModelFixture.CreateAsync(
            new ResetCreditSummary(0, [])))
        {
            Assert.AreEqual("0", empty.ViewModel.CreditStatus);
            CollectionAssert.AreEqual(
                new[] { "만료 예정 없음" },
                empty.ViewModel.CreditExpirationItems.ToArray());
        }
    }

    [TestMethod]
    public async Task PartialDetailsShowKnownExpiryAndUnlistedCount()
    {
        const long expiration = 2_000_000_000;
        await using var fixture = await ViewModelFixture.CreateAsync(
            new ResetCreditSummary(
                3,
                [CreateCredit("known", expiration)]));
        var formattedExpiration = DateTimeOffset
            .FromUnixTimeSeconds(expiration)
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);

        Assert.AreEqual("3", fixture.ViewModel.CreditStatus);
        CollectionAssert.AreEqual(
            new[]
            {
                $"초기화권 1 · {formattedExpiration} 만료",
                "나머지 2개 · 만료일 정보 없음",
            },
            fixture.ViewModel.CreditExpirationItems.ToArray());
    }

    [TestMethod]
    public async Task MissingDetailListAndMissingItemExpiryRemainDistinguishable()
    {
        await using (var missingList = await ViewModelFixture.CreateAsync(
            new ResetCreditSummary(1, null)))
        {
            CollectionAssert.AreEqual(
                new[] { "만료일 정보 없음" },
                missingList.ViewModel.CreditExpirationItems.ToArray());
        }

        await using (var missingItemExpiry = await ViewModelFixture.CreateAsync(
            new ResetCreditSummary(
                1,
                [CreateCredit("without-expiry", null)])))
        {
            CollectionAssert.AreEqual(
                new[] { "초기화권 1 · 만료일 정보 없음" },
                missingItemExpiry.ViewModel.CreditExpirationItems.ToArray());
        }
    }

    [DataTestMethod]
    [DataRow(long.MinValue)]
    [DataRow(long.MaxValue)]
    public async Task InvalidUnixExpiryFallsBackWithoutBreakingTheSnapshot(
        long invalidExpiration)
    {
        await using var fixture = await ViewModelFixture.CreateAsync(null);
        fixture.ApplySnapshot(
            MonitorSnapshot.Waiting(GuardSettings.Default) with
            {
                AvailableCreditCount = 1,
                AvailableCreditExpirations = [invalidExpiration],
            });

        Assert.AreEqual("1", fixture.ViewModel.CreditStatus);
        CollectionAssert.AreEqual(
            new[] { "초기화권 1 · 만료일 정보 없음" },
            fixture.ViewModel.CreditExpirationItems.ToArray());
    }

    private static ResetCredit CreateCredit(string id, long? expiresAt) => new(
        id,
        "codexRateLimits",
        "available",
        1_900_000_000,
        expiresAt,
        null,
        null);

    private sealed class ViewModelFixture : IAsyncDisposable
    {
        private readonly string directory;
        private readonly GuardMonitorService monitor;

        private ViewModelFixture(
            string directory,
            GuardMonitorService monitor,
            MainWindowViewModel viewModel)
        {
            this.directory = directory;
            this.monitor = monitor;
            ViewModel = viewModel;
        }

        public MainWindowViewModel ViewModel { get; }

        public void ApplySnapshot(MonitorSnapshot snapshot)
        {
            var applySnapshot = typeof(MainWindowViewModel).GetMethod(
                "ApplySnapshot",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(applySnapshot);
            applySnapshot.Invoke(ViewModel, [snapshot]);
        }

        public static async Task<ViewModelFixture> CreateAsync(
            ResetCreditSummary? summary)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"CodexAutoReset-credit-expiry-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var settingsStore = new JsonSettingsStore(
                    Path.Combine(directory, "settings.json"));
                await settingsStore.SaveAsync(
                    GuardSettings.Default,
                    CancellationToken.None);
                var result = CreateResult(summary);
                var monitor = new GuardMonitorService(
                    settingsStore,
                    new FixedResultExecutor(result),
                    GuardSettings.Default);
                var viewModel = new MainWindowViewModel(
                    settingsStore,
                    new StartupService(new MemoryRegistryStore()),
                    monitor,
                    GuardSettings.Default);
                await monitor.RefreshAsync();
                return new ViewModelFixture(directory, monitor, viewModel);
            }
            catch
            {
                Directory.Delete(directory, recursive: true);
                throw;
            }
        }

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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static GuardCycleResult CreateResult(ResetCreditSummary? summary)
        {
            var observedAt = DateTimeOffset.UtcNow;
            var accountRateLimits = new AccountRateLimits(
                new RateLimitSnapshot("codex", "Codex", null, null),
                null,
                summary,
                observedAt);
            var evaluation = new EvaluationResult(
                null,
                new GuardDecision(
                    DecisionKind.NoAction,
                    DecisionReason.AboveThreshold,
                    null,
                    null,
                    null),
                summary?.AvailableCount);
            return new GuardCycleResult(
                accountRateLimits,
                evaluation,
                CycleActionKind.None,
                "no_action");
        }
    }

    private sealed class FixedResultExecutor : IGuardCycleExecutor
    {
        private readonly GuardCycleResult result;

        public FixedResultExecutor(GuardCycleResult result)
        {
            this.result = result;
        }

        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken) => Task.FromResult(result);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryRegistryStore : ICurrentUserRegistryStore
    {
        private readonly Dictionary<(string Key, string Name), string> values = [];

        public CurrentUserRegistryValue ReadValue(string subKey, string valueName) =>
            values.TryGetValue((subKey, valueName), out var value)
                ? CurrentUserRegistryValue.FromString(value)
                : CurrentUserRegistryValue.Missing;

        public void SetString(string subKey, string valueName, string value) =>
            values[(subKey, valueName)] = value;

        public void DeleteValue(string subKey, string valueName) =>
            values.Remove((subKey, valueName));
    }
}
