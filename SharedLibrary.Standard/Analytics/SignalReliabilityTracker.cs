using System;
using System.Collections.Generic;

namespace BookFlow.Shared.Analytics
{
    /// <summary>
    /// Forward-evaluates microstructure signals by PRICE-LEVEL barriers (not time) and tracks a
    /// per-type hit-rate.
    ///
    /// A signal predicts a direction (its <see cref="MicrostructureBias"/>). When it fires we begin
    /// watching price from its anchor: if price travels <see cref="TargetTicks"/> ticks in the
    /// predicted direction before <see cref="StopTicks"/> ticks against, that is a WIN; the opposite
    /// is a LOSS (a triple-barrier / first-passage test). Resolved outcomes feed a running
    /// reliability that is used to stamp FUTURE signals — already-printed feed lines are never
    /// revised, so there is no look-ahead bias and the UI stays append-only.
    ///
    /// Stateful but cheap: <see cref="RegisterSignal"/>, <see cref="OnPriceSample"/> and
    /// <see cref="GetReliability"/> are O(pending) / O(1) and are meant to be driven off the
    /// existing 4 Hz microstructure tick — never the hot market-data path.
    /// </summary>
    public sealed class SignalReliabilityTracker
    {
        private struct Pending
        {
            public MicrostructureSignalType Type;
            public int Dir;            // +1 = up, -1 = down
            public long TargetLevel;   // price level (ticks) of the favorable barrier
            public long StopLevel;     // price level (ticks) of the adverse barrier
        }

        private struct Stat { public int Wins; public int Total; }

        /// <summary>
        /// Defines "success": a win is price travelling <see cref="TargetTicks"/> ticks in the
        /// predicted direction before <see cref="StopTicks"/> ticks against. Asymmetric is allowed
        /// (e.g. target 6 / stop 10) so the meter can be tuned to match the bracket actually traded.
        /// </summary>
        public int TargetTicks { get; set; } = 4;
        public int StopTicks { get; set; } = 4;

        /// <summary>Below this many resolved samples a type is "still learning" (bars gated to 0).</summary>
        public int MinSamples { get; set; } = 5;

        /// <summary>Caps memory; oldest unresolved predictions are dropped (not scored) past this.</summary>
        public int MaxPending { get; set; } = 64;

        private readonly LinkedList<Pending> _pending = new();
        private readonly Dictionary<MicrostructureSignalType, Stat> _stats = new();

        public void Reset()
        {
            _pending.Clear();
            _stats.Clear();
        }

        /// <summary>Seed a type's resolved win/total (e.g. rebuilt from persisted history on startup).</summary>
        public void Seed(MicrostructureSignalType type, int wins, int total)
        {
            if (total <= 0) return;
            if (wins < 0) wins = 0;
            if (wins > total) wins = total;
            _stats[type] = new Stat { Wins = wins, Total = total };
        }

        /// <summary>Begin forward-tracking a freshly emitted signal. Neutral bias is ignored.</summary>
        public void RegisterSignal(MicrostructureSignalType type, MicrostructureBias bias, long anchorTicks)
        {
            int dir = bias == MicrostructureBias.Up ? 1 : bias == MicrostructureBias.Down ? -1 : 0;
            if (dir == 0) return;
            int tk = Math.Max(1, TargetTicks);
            int sk = Math.Max(1, StopTicks);
            _pending.AddLast(new Pending
            {
                Type = type,
                Dir = dir,
                TargetLevel = anchorTicks + dir * tk,
                StopLevel = anchorTicks - dir * sk,
            });
            while (_pending.Count > MaxPending) _pending.RemoveFirst(); // discard oldest unresolved
        }

        /// <summary>
        /// Resolve any pending predictions against the current mid (in ticks). Call once per
        /// 4 Hz tick. Sampling cadence only affects how quickly a crossing is noticed; the
        /// win/loss criterion itself is purely price-level.
        /// </summary>
        public void OnPriceSample(long midTicks)
        {
            var node = _pending.First;
            while (node != null)
            {
                var next = node.Next;
                var p = node.Value;
                bool win = p.Dir > 0 ? midTicks >= p.TargetLevel : midTicks <= p.TargetLevel;
                bool loss = p.Dir > 0 ? midTicks <= p.StopLevel : midTicks >= p.StopLevel;
                if (win || loss)
                {
                    var s = _stats.TryGetValue(p.Type, out var cur) ? cur : new Stat();
                    s.Total++;
                    if (win) s.Wins++;
                    _stats[p.Type] = s;
                    _pending.Remove(node);
                }
                node = next;
            }
        }

        /// <summary>
        /// Reliability for a type as known right now: a 0..5 bar count, the smoothed hit-rate, the
        /// number of resolved samples, and whether it is still learning. Uses a weak Beta(2,2)
        /// prior so one lucky sample can't read as full confidence, and gates bars to 0 until
        /// <see cref="MinSamples"/> have resolved.
        /// </summary>
        public (int Bars, double Ratio, int Samples, bool Learning) GetReliability(MicrostructureSignalType type)
        {
            if (!_stats.TryGetValue(type, out var s) || s.Total == 0)
                return (0, 0.5, 0, true);

            double ratio = (s.Wins + 2.0) / (s.Total + 4.0); // Beta(2,2) posterior mean
            bool learning = s.Total < MinSamples;
            int bars = learning ? 0 : (int)Math.Round(ratio * 5.0, MidpointRounding.AwayFromZero);
            if (bars < 0) bars = 0;
            if (bars > 5) bars = 5;
            return (bars, ratio, s.Total, learning);
        }
    }
}
