using System.Collections.Generic;
using System.Threading.Tasks;
using BookFlow.App.Engine;
using BookFlow.Shared.Analytics;
using BookFlow.Shared.Contracts;
using Xunit;

namespace BookFlow.Tests
{
    public class SignalAutoTraderTests
    {
        private sealed class Capture
        {
            public int Calls;
            public bool IsBuy, EntryIsLimit;
            public double EntryPrice;
            public int Qty, Target, Stop, Timeout;
            public AutoTradeSubmit Submit => (isBuy, isLimit, price, qty, tgt, stop, to) =>
            {
                Calls++; IsBuy = isBuy; EntryIsLimit = isLimit; EntryPrice = price;
                Qty = qty; Target = tgt; Stop = stop; Timeout = to;
                return Task.CompletedTask;
            };
        }

        private static MicrostructureSignal Sig(MicrostructureSignalType type, MicrostructureBias bias,
            decimal? price, double ratio, bool learning = false) =>
            new MicrostructureSignal
            {
                Type = type, Bias = bias, Price = price,
                ReliabilityRatio = ratio, ReliabilityLearning = learning, ReliabilitySamples = learning ? 1 : 50,
            };

        private static DomSettings ArmedSettings()
        {
            var s = new DomSettings { AutoTradeArmed = true, ReliabilityGreenPercent = 60 };
            s.IcebergAutoTrade.Enabled = true;
            s.IcebergAutoTrade.TargetTicks = 8;
            s.IcebergAutoTrade.StopTicks = 4;
            s.IcebergAutoTrade.Size = 2;
            s.IcebergAutoTrade.OffsetTicks = -2;
            return s;
        }

        [Fact]
        public void GreenEnabledArmed_Fires_LimitWithOffset()
        {
            var cap = new Capture();
            var trader = new SignalAutoTrader(ArmedSettings(), 0.25m, cap.Submit);

            // Iceberg bid → Bias.Up (buy), ratio 0.8 (green), anchor 100.00, offset -2 ticks => 99.50
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));

            Assert.Equal(1, cap.Calls);
            Assert.True(cap.IsBuy);
            Assert.True(cap.EntryIsLimit);
            Assert.Equal(99.50, cap.EntryPrice, 5);
            Assert.Equal(2, cap.Qty);
            Assert.Equal(8, cap.Target);
            Assert.Equal(4, cap.Stop);
            Assert.Equal(10, cap.Timeout); // default AutoTradeEntryTimeoutSeconds for a limit entry
        }

        [Fact]
        public void NotArmed_DoesNotFire()
        {
            var s = ArmedSettings(); s.AutoTradeArmed = false;
            var cap = new Capture();
            var trader = new SignalAutoTrader(s, 0.25m, cap.Submit);
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));
            Assert.Equal(0, cap.Calls);
        }

        [Fact]
        public void TypeDisabled_DoesNotFire()
        {
            var s = ArmedSettings(); s.IcebergAutoTrade.Enabled = false;
            var cap = new Capture();
            var trader = new SignalAutoTrader(s, 0.25m, cap.Submit);
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));
            Assert.Equal(0, cap.Calls);
        }

        [Fact]
        public void BelowGreen_DoesNotFire()
        {
            var cap = new Capture();
            var trader = new SignalAutoTrader(ArmedSettings(), 0.25m, cap.Submit);
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.55)); // 55% < 60%
            Assert.Equal(0, cap.Calls);
        }

        [Fact]
        public void Learning_DoesNotFire()
        {
            var cap = new Capture();
            var trader = new SignalAutoTrader(ArmedSettings(), 0.25m, cap.Submit);
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.90, learning: true));
            Assert.Equal(0, cap.Calls);
        }

        [Fact]
        public void Aggression_ForcesMarket()
        {
            var s = ArmedSettings();
            s.AggressionAutoTrade.Enabled = true;
            s.AggressionAutoTrade.TargetTicks = 10;
            s.AggressionAutoTrade.StopTicks = 5;
            var cap = new Capture();
            var trader = new SignalAutoTrader(s, 0.25m, cap.Submit);

            // Aggression has Price == null → market entry, sell on Bias.Down
            trader.OnSignal(Sig(MicrostructureSignalType.AggressionImbalance, MicrostructureBias.Down, null, 0.75));

            Assert.Equal(1, cap.Calls);
            Assert.False(cap.IsBuy);
            Assert.False(cap.EntryIsLimit);
            Assert.Equal(0, cap.Timeout); // market entry → no entry timeout
        }

        [Fact]
        public void Ofi_Enabled_FiresMarket()
        {
            var s = ArmedSettings();
            s.OfiAutoTrade.Enabled = true;
            s.OfiAutoTrade.TargetTicks = 6;
            s.OfiAutoTrade.StopTicks = 6;
            var cap = new Capture();
            var trader = new SignalAutoTrader(s, 0.25m, cap.Submit);

            // OFI carries no price level => market entry, buy on Bias.Up.
            trader.OnSignal(Sig(MicrostructureSignalType.OrderFlowImbalance, MicrostructureBias.Up, null, 0.75));

            Assert.Equal(1, cap.Calls);
            Assert.True(cap.IsBuy);
            Assert.False(cap.EntryIsLimit);
            Assert.Equal(0, cap.Timeout); // market => no entry timeout
        }

        [Fact]
        public void OnePositionPerInstrument_BlocksWhilePendingOrOpen()
        {
            var cap = new Capture();
            var trader = new SignalAutoTrader(ArmedSettings(), 0.25m, cap.Submit);

            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));
            Assert.Equal(1, cap.Calls);

            // Still pending (no fill yet) → blocked.
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));
            Assert.Equal(1, cap.Calls);

            // Position opens, then more signals → still blocked.
            trader.OnPositionChanged(2);
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));
            Assert.Equal(1, cap.Calls);

            // Position closes (OCO) → next green signal fires again.
            trader.OnPositionChanged(0);
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));
            Assert.Equal(2, cap.Calls);
        }

        [Fact]
        public void ToxicFlow_AboveMax_DoesNotFire()
        {
            var s = ArmedSettings();
            s.AutoTradeMaxToxicity = 0.8;
            var cap = new Capture();
            var trader = new SignalAutoTrader(s, 0.25m, cap.Submit, toxicity: () => 0.9); // 0.9 > 0.8
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));
            Assert.Equal(0, cap.Calls);
        }

        [Fact]
        public void CalmFlow_BelowMax_Fires()
        {
            var s = ArmedSettings();
            s.AutoTradeMaxToxicity = 0.8;
            var cap = new Capture();
            var trader = new SignalAutoTrader(s, 0.25m, cap.Submit, toxicity: () => 0.2); // 0.2 <= 0.8
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Up, 100.00m, 0.80));
            Assert.Equal(1, cap.Calls);
        }

        [Fact]
        public void NeutralBias_DoesNotFire()
        {
            var cap = new Capture();
            var trader = new SignalAutoTrader(ArmedSettings(), 0.25m, cap.Submit);
            trader.OnSignal(Sig(MicrostructureSignalType.Iceberg, MicrostructureBias.Neutral, 100.00m, 0.90));
            Assert.Equal(0, cap.Calls);
        }
    }
}
