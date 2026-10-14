using FoxData.Worker;

namespace FoxData.RecoveryTests;

public sealed class M6RecoveryExitGateTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(1, 0, 0, false)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 0, 1, false)]
    [InlineData(0, 2, 3, false)]
    public void QualityRecoveryQuiescenceRequiresZeroProgressAndZeroOutstanding(
        int terminalCompleted,
        int deferred,
        int versionBlocked,
        bool expectedQuiescent)
    {
        var result = new WarApiMapQualityRecoveryResult(
            terminalCompleted,
            deferred,
            versionBlocked);

        Assert.Equal(
            terminalCompleted,
            result.ProgressCount);
        Assert.Equal(
            deferred + versionBlocked,
            result.OutstandingCount);
        Assert.Equal(
            expectedQuiescent,
            result.IsQuiescent);
    }
}
