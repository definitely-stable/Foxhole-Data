using FoxData.Worker;

namespace FoxData.RecoveryTests;

// M6-H completion proof only. This does not change ordinary Worker scheduling.
// A zero-progress sweep is NOT quiescence when either recovery stage reports
// deferred or version-blocked durable work.
internal static class M6CompletionConvergence
{
    public static async Task<int> RunAsync(
        Func<CancellationToken, Task<WarApiCoverageRecoveryResult>> recoverCoverage,
        Func<CancellationToken, Task<WarApiMapQualityRecoveryResult>> recoverQuality,
        CancellationToken cancellationToken,
        int maxPasses = 32)
    {
        ArgumentNullException.ThrowIfNull(recoverCoverage);
        ArgumentNullException.ThrowIfNull(recoverQuality);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPasses, 1);

        for (var pass = 1; pass <= maxPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Always evaluate both layers; canonical recovery can unblock
            // quality recovery within the same deterministic sweep.
            var coverage = await recoverCoverage(cancellationToken);
            var quality = await recoverQuality(cancellationToken);

            var progress =
                (long)coverage.ProgressCount + quality.ProgressCount;
            var deferred =
                (long)coverage.CanonicalDeferred + quality.Deferred;
            var blocked = quality.VersionBlocked;

            if (progress != 0)
            {
                continue;
            }

            if (deferred != 0 || blocked != 0)
            {
                throw new InvalidOperationException(
                    $"M6-H recovery stalled: deferred={deferred}, " +
                    $"version_blocked={blocked}; zero progress is not completion.");
            }

            return pass;
        }

        throw new InvalidOperationException(
            $"M6-H recovery did not converge within {maxPasses} passes.");
    }
}

public sealed class M6CompletionConvergenceTests
{
    [Fact]
    public async Task ConvergesAfterBothRecoveryStagesStopMakingProgress()
    {
        var coveragePasses = 0;
        var qualityPasses = 0;

        var passes = await M6CompletionConvergence.RunAsync(
            _ => Task.FromResult(
                ++coveragePasses == 1
                    ? Coverage(canonicalCompleted: 1)
                    : Coverage()),
            _ => Task.FromResult(
                ++qualityPasses == 1
                    ? new WarApiMapQualityRecoveryResult(1, 1, 0)
                    : new WarApiMapQualityRecoveryResult(0, 0, 0)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, passes);
        Assert.Equal(2, coveragePasses);
        Assert.Equal(2, qualityPasses);
    }

    [Fact]
    public async Task ZeroProgressWithCanonicalDeferredIsNotQuiescent()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => M6CompletionConvergence.RunAsync(
                _ => Task.FromResult(Coverage(canonicalDeferred: 1)),
                _ => Task.FromResult(
                    new WarApiMapQualityRecoveryResult(0, 0, 0)),
                TestContext.Current.CancellationToken));

        Assert.Contains("deferred=1", error.Message);
    }

    [Fact]
    public async Task ZeroProgressWithQualityDeferredIsNotQuiescent()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => M6CompletionConvergence.RunAsync(
                _ => Task.FromResult(Coverage()),
                _ => Task.FromResult(
                    new WarApiMapQualityRecoveryResult(0, 1, 0)),
                TestContext.Current.CancellationToken));

        Assert.Contains("deferred=1", error.Message);
    }

    [Fact]
    public async Task ZeroProgressWithVersionBlockedIsNotQuiescent()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => M6CompletionConvergence.RunAsync(
                _ => Task.FromResult(Coverage()),
                _ => Task.FromResult(
                    new WarApiMapQualityRecoveryResult(0, 0, 1)),
                TestContext.Current.CancellationToken));

        Assert.Contains("version_blocked=1", error.Message);
    }

    [Fact]
    public async Task InfiniteProgressFailsAtBoundedPassLimit()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => M6CompletionConvergence.RunAsync(
                _ => Task.FromResult(Coverage(canonicalCompleted: 1)),
                _ => Task.FromResult(
                    new WarApiMapQualityRecoveryResult(0, 0, 0)),
                TestContext.Current.CancellationToken,
                maxPasses: 2));

        Assert.Contains("within 2 passes", error.Message);
    }

    private static WarApiCoverageRecoveryResult Coverage(
        int canonicalCompleted = 0,
        int canonicalDeferred = 0) =>
        new(
            CoverageRecorded: 0,
            ParseRunsRepaired: 0,
            ContinuityApplied: 0,
            ContinuityRejected: 0,
            CanonicalCompleted: canonicalCompleted,
            CanonicalDeferred: canonicalDeferred);
}
