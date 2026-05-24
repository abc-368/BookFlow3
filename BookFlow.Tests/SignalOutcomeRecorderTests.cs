using BookFlow.Shared.Analytics;
using Xunit;

namespace BookFlow.Tests
{
    public class SignalOutcomeRecorderTests
    {
        [Fact]
        public void Records_DirectionAdjustedPath_OverHorizon()
        {
            var r = new SignalOutcomeRecorder { HorizonSamples = 3 };
            r.Register(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, anchorTicks: 100, predictedRatio: 0.7, fireTicks: 0, session: TradingSession.US);

            r.OnPriceSample(101); // +1 favorable
            r.OnPriceSample(102); // +2
            Assert.Equal(0, r.CompletedCount); // horizon not yet reached
            r.OnPriceSample(98);  // -2 (against) -> 3rd sample completes it

            Assert.Equal(1, r.CompletedCount);
            var o = r.Snapshot()[0];
            Assert.Equal(MicrostructureSignalType.Iceberg, o.Type);
            Assert.Equal(new[] { 1, 2, -2 }, o.FavorableTicks);
        }

        [Fact]
        public void DownBias_FavorableIsInverted()
        {
            var r = new SignalOutcomeRecorder { HorizonSamples = 2 };
            r.Register(MicrostructureSignalType.Spoofing, MicrostructureBias.Down, anchorTicks: 100, predictedRatio: 0.6, fireTicks: 0, session: TradingSession.London);
            r.OnPriceSample(98);  // price fell 2 -> favorable +2 for a DOWN call
            r.OnPriceSample(101); // price rose 1 -> favorable -1
            Assert.Equal(new[] { 2, -1 }, r.Snapshot()[0].FavorableTicks);
        }

        [Fact]
        public void NeutralBias_NotRecorded()
        {
            var r = new SignalOutcomeRecorder { HorizonSamples = 1 };
            r.Register(MicrostructureSignalType.AggressionImbalance, MicrostructureBias.Neutral, 100, 0.9, 0, TradingSession.Asia);
            r.OnPriceSample(105);
            Assert.Equal(0, r.CompletedCount);
        }
    }
}
