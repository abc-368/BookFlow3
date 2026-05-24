using System;
using System.Collections.Generic;

namespace BookFlow.Shared.Analytics
{
    public enum MicrostructureSignalType : byte { Spoofing = 1, Iceberg = 2, LiquidityWithdrawal = 3, AggressionImbalance = 4 }
    public enum MicrostructureSide : byte { None = 0, Bid = 1, Ask = 2 }

    /// <summary>Predicted near-term price pressure implied by a signal.</summary>
    public enum MicrostructureBias : byte { Neutral = 0, Up = 1, Down = 2 }

    /// <summary>One detected order-flow event. Level-anchored signals carry a Price.</summary>
    public sealed class MicrostructureSignal
    {
        public MicrostructureSignalType Type { get; set; }
        public MicrostructureSide Side { get; set; }
        public decimal? Price { get; set; }
        public double Score { get; set; }          // magnitude/severity (units depend on type)
        public MicrostructureBias Bias { get; set; } // likely resulting price direction
        public bool Strong { get; set; }            // magnitude well above threshold
        public long TimestampTicks { get; set; }
        public string Label { get; set; }

        // Historical prediction power of this signal TYPE, stamped by the engine at emit time
        // from the SignalReliabilityTracker (reflects past resolved outcomes only).
        public int ReliabilityBars { get; set; }    // 0..5 strength meter
        public double ReliabilityRatio { get; set; } // smoothed hit-rate
        public int ReliabilitySamples { get; set; } // resolved samples behind the score
        public bool ReliabilityLearning { get; set; } // too few samples to be confident yet
    }

    /// <summary>
    /// Heuristic microstructure detectors over the L2 analytics window. Stateless: the caller
    /// supplies two vicinity snapshots taken an interval apart; the detector works on the
    /// per-level deltas (events that occurred in that interval).
    ///
    /// HONEST SCOPE: NT8 L2 is aggregated depth (no per-order IDs / queue position), so these
    /// are heuristics, not definitive classifications. Thresholds are coarse defaults that
    /// should be tuned per instrument (volume scales differ across ES/NQ/CL...).
    /// </summary>
    public sealed class MicrostructureDetector
    {
        // Spoofing: churn (added+canceled) this big with at most this fraction traded, and the
        // cancel side dominating the add — phantom liquidity placed then pulled.
        public long SpoofMinChurn { get; set; } = 40;
        public double SpoofMaxTradedFraction { get; set; } = 0.15;

        // Iceberg: traded this much more than the displayed size dropped — hidden replenishment.
        public long IcebergMinRefill { get; set; } = 20;

        // Liquidity withdrawal: one-sided cancel-not-traded within this many ticks of the touch.
        public long WithdrawalMinSize { get; set; } = 60;
        public int WithdrawalRadiusTicks { get; set; } = 5;

        // Aggression imbalance: enough aggressive volume and a directional skew this strong.
        public long AggressionMinVolume { get; set; } = 30;
        public double AggressionMinImbalance { get; set; } = 0.6;

        public List<MicrostructureSignal> Detect(
            IReadOnlyList<L2AnalyticsSlot> prev,
            IReadOnlyList<L2AnalyticsSlot> curr,
            decimal? bestBid,
            decimal? bestAsk,
            decimal tickSize,
            long nowTicks)
        {
            var signals = new List<MicrostructureSignal>();
            if (curr == null || curr.Count == 0) return signals;
            if (tickSize <= 0) tickSize = 0.25m;

            var prevByTicks = new Dictionary<long, L2AnalyticsSlot>();
            if (prev != null)
                foreach (var p in prev) prevByTicks[p.PriceTicks] = p;

            decimal? mid = (bestBid.HasValue && bestAsk.HasValue)
                ? (bestBid.Value + bestAsk.Value) / 2m
                : (bestBid ?? bestAsk);

            long withdrawBid = 0, withdrawAsk = 0;
            long aggrBuy = 0, aggrSell = 0;

            foreach (var c in curr)
            {
                if (!prevByTicks.TryGetValue(c.PriceTicks, out var p))
                    continue; // slot is new/reset this interval — no comparable baseline

                long addedD = c.AddedVolume - p.AddedVolume;
                long cancD = c.CanceledVolume - p.CanceledVolume;
                long tradD = c.TradedVolume - p.TradedVolume;
                long bidTradD = c.BidTradedVolume - p.BidTradedVolume;
                long askTradD = c.AskTradedVolume - p.AskTradedVolume;
                if (addedD < 0 || cancD < 0 || tradD < 0)
                    continue; // counters reset (ring aliasing) between snapshots

                bool isBidSide = mid.HasValue ? c.Price <= mid.Value : c.BidSize >= c.AskSize;
                long sizeNow = isBidSide ? c.BidSize : c.AskSize;
                long sizePrev = isBidSide ? p.BidSize : p.AskSize;
                long sizeDrop = Math.Max(0, sizePrev - sizeNow);

                string sideTxt = isBidSide ? "bid" : "ask";

                // --- Spoofing / layering ---
                // Fake liquidity placed then pulled without trading. When it vanishes the
                // apparent pressure reverses, so a spoof bid implies DOWN, a spoof ask UP.
                long churn = addedD + cancD;
                if (churn >= SpoofMinChurn &&
                    tradD <= SpoofMaxTradedFraction * churn &&
                    cancD >= addedD * 0.5)
                {
                    signals.Add(new MicrostructureSignal
                    {
                        Type = MicrostructureSignalType.Spoofing,
                        Side = isBidSide ? MicrostructureSide.Bid : MicrostructureSide.Ask,
                        Price = c.Price,
                        Score = churn,
                        Bias = isBidSide ? MicrostructureBias.Down : MicrostructureBias.Up,
                        Strong = churn >= 2 * SpoofMinChurn,
                        TimestampTicks = nowTicks,
                        Label = $"Spoof {sideTxt} +{addedD}/-{cancD} @ {FormatPrice(c.Price)}",
                    });
                }

                // --- Iceberg / absorption (traded more than the displayed size dropped) ---
                // A hidden buyer on the bid defends/accumulates (UP); a hidden seller on the ask
                // distributes (DOWN).
                long refill = tradD - sizeDrop;
                if (tradD >= IcebergMinRefill && refill >= IcebergMinRefill)
                {
                    signals.Add(new MicrostructureSignal
                    {
                        Type = MicrostructureSignalType.Iceberg,
                        Side = isBidSide ? MicrostructureSide.Bid : MicrostructureSide.Ask,
                        Price = c.Price,
                        Score = refill,
                        Bias = isBidSide ? MicrostructureBias.Up : MicrostructureBias.Down,
                        Strong = refill >= 2 * IcebergMinRefill,
                        TimestampTicks = nowTicks,
                        Label = $"Iceberg {sideTxt} {refill} hidden @ {FormatPrice(c.Price)}",
                    });
                }

                // --- Withdrawal accumulation near the touch ---
                if (mid.HasValue)
                {
                    var distTicks = Math.Abs((c.Price - mid.Value) / tickSize);
                    if (distTicks <= WithdrawalRadiusTicks)
                    {
                        long cancelNotTraded = Math.Max(0, cancD - tradD);
                        if (isBidSide) withdrawBid += cancelNotTraded; else withdrawAsk += cancelNotTraded;
                    }
                }

                aggrBuy += askTradD;  // aggressive buys lift the ask
                aggrSell += bidTradD; // aggressive sells hit the bid
            }

            // Pulling bids removes support (DOWN); pulling asks removes resistance (UP).
            if (withdrawBid >= WithdrawalMinSize)
                signals.Add(new MicrostructureSignal { Type = MicrostructureSignalType.LiquidityWithdrawal, Side = MicrostructureSide.Bid, Price = bestBid, Score = withdrawBid, Bias = MicrostructureBias.Down, Strong = withdrawBid >= 2 * WithdrawalMinSize, TimestampTicks = nowTicks, Label = $"Bid pulled {withdrawBid} (≤{WithdrawalRadiusTicks}t of {FormatPrice(bestBid)})" });
            if (withdrawAsk >= WithdrawalMinSize)
                signals.Add(new MicrostructureSignal { Type = MicrostructureSignalType.LiquidityWithdrawal, Side = MicrostructureSide.Ask, Price = bestAsk, Score = withdrawAsk, Bias = MicrostructureBias.Up, Strong = withdrawAsk >= 2 * WithdrawalMinSize, TimestampTicks = nowTicks, Label = $"Ask pulled {withdrawAsk} (≤{WithdrawalRadiusTicks}t of {FormatPrice(bestAsk)})" });

            long aggrTotal = aggrBuy + aggrSell;
            if (aggrTotal >= AggressionMinVolume)
            {
                double imbalance = (double)(aggrBuy - aggrSell) / aggrTotal;
                if (Math.Abs(imbalance) >= AggressionMinImbalance)
                {
                    bool buySide = imbalance > 0;
                    signals.Add(new MicrostructureSignal
                    {
                        Type = MicrostructureSignalType.AggressionImbalance,
                        Side = buySide ? MicrostructureSide.Ask : MicrostructureSide.Bid,
                        Price = null,
                        Score = Math.Abs(imbalance),
                        Bias = buySide ? MicrostructureBias.Up : MicrostructureBias.Down,
                        Strong = Math.Abs(imbalance) >= 0.85,
                        TimestampTicks = nowTicks,
                        Label = buySide ? $"Buy aggression {aggrBuy} vs {aggrSell}" : $"Sell aggression {aggrSell} vs {aggrBuy}",
                    });
                }
            }

            return signals;
        }

        private static string FormatPrice(decimal? price)
            => price.HasValue ? price.Value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) : "?";
    }
}
