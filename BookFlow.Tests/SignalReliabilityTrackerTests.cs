using BookFlow.Shared.Analytics;
using Xunit;

namespace BookFlow.Tests
{
    public class SignalReliabilityTrackerTests
    {
        [Fact]
        public void UpBias_PriceReachesTarget_CountsWin()
        {
            var t = new SignalReliabilityTracker { TargetTicks = 4, StopTicks = 4, MinSamples = 1 };
            t.RegisterSignal(MicrostructureSignalType.AggressionImbalance, MicrostructureBias.Up, anchorTicks: 100);
            t.OnPriceSample(104); // +4 ticks -> target hit -> win
            var r = t.GetReliability(MicrostructureSignalType.AggressionImbalance);
            Assert.Equal(1, r.Samples);
            Assert.True(r.Ratio > 0.5);
            Assert.False(r.Learning);
            Assert.True(r.Bars >= 3);
        }

        [Fact]
        public void DownBias_PriceRises_CountsLoss()
        {
            var t = new SignalReliabilityTracker { TargetTicks = 4, StopTicks = 4, MinSamples = 1 };
            t.RegisterSignal(MicrostructureSignalType.Spoofing, MicrostructureBias.Down, 100);
            t.OnPriceSample(104); // rose -> stop hit -> loss for a DOWN call
            var r = t.GetReliability(MicrostructureSignalType.Spoofing);
            Assert.Equal(1, r.Samples);
            Assert.True(r.Ratio < 0.5);
        }

        [Fact]
        public void InsideBarriers_NotYetResolved()
        {
            var t = new SignalReliabilityTracker { TargetTicks = 4, StopTicks = 4 };
            t.RegisterSignal(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100);
            t.OnPriceSample(102); // only +2 ticks; neither barrier reached
            var r = t.GetReliability(MicrostructureSignalType.Iceberg);
            Assert.Equal(0, r.Samples);
            Assert.True(r.Learning);
        }

        [Fact]
        public void BelowMinSamples_BarsGatedToZero()
        {
            var t = new SignalReliabilityTracker { TargetTicks = 2, StopTicks = 2, MinSamples = 5 };
            t.RegisterSignal(MicrostructureSignalType.AggressionImbalance, MicrostructureBias.Up, 100);
            t.OnPriceSample(102); // win, but only one resolved sample
            var r = t.GetReliability(MicrostructureSignalType.AggressionImbalance);
            Assert.Equal(1, r.Samples);
            Assert.Equal(0, r.Bars);
            Assert.True(r.Learning);
        }

        [Fact]
        public void NeutralBias_NotTracked()
        {
            var t = new SignalReliabilityTracker { TargetTicks = 2, StopTicks = 2, MinSamples = 1 };
            t.RegisterSignal(MicrostructureSignalType.Spoofing, MicrostructureBias.Neutral, 100);
            t.OnPriceSample(110);
            var r = t.GetReliability(MicrostructureSignalType.Spoofing);
            Assert.Equal(0, r.Samples);
        }

        [Fact]
        public void ResolvedPrediction_DoesNotResolveTwice()
        {
            var t = new SignalReliabilityTracker { TargetTicks = 3, StopTicks = 3, MinSamples = 1 };
            t.RegisterSignal(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100);
            t.OnPriceSample(103); // win
            t.OnPriceSample(120); // already resolved & removed -> no double count
            var r = t.GetReliability(MicrostructureSignalType.Iceberg);
            Assert.Equal(1, r.Samples);
        }

        [Fact]
        public void AsymmetricBarrier_TargetAndStopAreIndependent()
        {
            // Up call, target +3 / stop -10 from anchor 100 => target 103, stop 90.
            var t = new SignalReliabilityTracker { TargetTicks = 3, StopTicks = 10, MinSamples = 1 };
            t.RegisterSignal(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100);
            t.OnPriceSample(95);  // -5: past a symmetric -3 stop, but inside the -10 stop => not resolved
            Assert.Equal(0, t.GetReliability(MicrostructureSignalType.Iceberg).Samples);
            t.OnPriceSample(103); // +3 target reached => win
            var r = t.GetReliability(MicrostructureSignalType.Iceberg);
            Assert.Equal(1, r.Samples);
            Assert.True(r.Ratio > 0.5);
        }

        [Fact]
        public void Reset_ClearsStatsAndPending()
        {
            var t = new SignalReliabilityTracker { TargetTicks = 2, StopTicks = 2, MinSamples = 1 };
            t.RegisterSignal(MicrostructureSignalType.Spoofing, MicrostructureBias.Down, 100);
            t.OnPriceSample(98);
            t.Reset();
            var r = t.GetReliability(MicrostructureSignalType.Spoofing);
            Assert.Equal(0, r.Samples);
        }
    }
}
