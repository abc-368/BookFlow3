using BookFlow.App.Persistence;
using BookFlow.Shared.Contracts;
using Xunit;

namespace BookFlow.Tests
{
    public class DomSettingsSnapshotTests
    {
        [Fact]
        public void RoundTrip_PreservesTuning_AndDoesNotRestoreArmed()
        {
            var src = new DomSettings();
            src.SpoofMinChurn = 99;
            src.OfiMinImbalance = 222;
            src.ReliabilityGreenPercent = 70;
            src.AutoTradeEntryTimeoutSeconds = 15;
            src.AutoTradeMaxToxicity = 0.8;
            src.ShowOfiSignals = false;
            src.IcebergAutoTrade.Enabled = true;
            src.IcebergAutoTrade.TargetTicks = 9;
            src.IcebergAutoTrade.StopTicks = 3;
            src.IcebergAutoTrade.Size = 2;
            src.AutoTradeArmed = true; // must NOT be restored

            var dst = new DomSettings();
            DomSettingsSnapshot.From(src).ApplyTo(dst);

            Assert.Equal(99, dst.SpoofMinChurn);
            Assert.Equal(222, dst.OfiMinImbalance);
            Assert.Equal(70, dst.ReliabilityGreenPercent);
            Assert.Equal(15, dst.AutoTradeEntryTimeoutSeconds);
            Assert.Equal(0.8, dst.AutoTradeMaxToxicity, 5);
            Assert.False(dst.ShowOfiSignals);
            Assert.True(dst.IcebergAutoTrade.Enabled);
            Assert.Equal(9, dst.IcebergAutoTrade.TargetTicks);
            Assert.Equal(3, dst.IcebergAutoTrade.StopTicks);
            Assert.Equal(2, dst.IcebergAutoTrade.Size);
            Assert.False(dst.AutoTradeArmed); // arming never persists
        }
    }
}
