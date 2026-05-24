using System.Collections.Generic;

namespace BookFlow.Shared.Analytics
{
    /// <summary>One recorded signal: its meter prediction plus the realized forward price path.</summary>
    public sealed class SignalOutcome
    {
        public MicrostructureSignalType Type;
        public MicrostructureBias Bias;
        public TradingSession Session;    // session in effect when the signal fired (for later segmentation)
        public double PredictedRatio;     // meter hit-rate for this type at fire time
        public long FireTimestampTicks;
        // Favorable mid excursion in ticks at each sample (direction-adjusted: + = toward the
        // prediction, - = against). Lets us re-score ANY target/stop offline via first-passage.
        public int[] FavorableTicks = System.Array.Empty<int>();
    }

    /// <summary>
    /// Captures, for each fired signal, the forward mid path over a fixed horizon — independent of
    /// the live meter's (early-resolving) barrier — so any candidate target/stop can be re-scored
    /// offline against the real recorded population (see <see cref="BracketReScorer"/>). The data
    /// substrate for the per-type feedback loop. Fed off the existing 4 Hz tick; off by default.
    /// </summary>
    public sealed class SignalOutcomeRecorder
    {
        public int HorizonSamples { get; set; } = 80;   // ~20 s at 4 Hz
        public int MaxConcurrent { get; set; } = 64;     // bound in-flight memory
        public int MaxCompleted { get; set; } = 5000;    // bound the retained sample

        private sealed class Active
        {
            public MicrostructureSignalType Type;
            public MicrostructureBias Bias;
            public TradingSession Session;
            public int Dir;
            public long AnchorTicks;
            public double PredictedRatio;
            public long FireTicks;
            public List<int> Path = new List<int>();
        }

        private readonly LinkedList<Active> _active = new LinkedList<Active>();
        private readonly List<SignalOutcome> _completed = new List<SignalOutcome>();
        private readonly object _completedGate = new object(); // _completed is read off other threads (persistence)

        public int CompletedCount { get { lock (_completedGate) return _completed.Count; } }

        /// <summary>Thread-safe copy of completed outcomes (the live list is mutated on the 4 Hz thread).</summary>
        public List<SignalOutcome> Snapshot() { lock (_completedGate) return new List<SignalOutcome>(_completed); }

        public void Reset() { _active.Clear(); lock (_completedGate) _completed.Clear(); }

        public void Register(MicrostructureSignalType type, MicrostructureBias bias, long anchorTicks,
            double predictedRatio, long fireTicks, TradingSession session)
        {
            int dir = bias == MicrostructureBias.Up ? 1 : bias == MicrostructureBias.Down ? -1 : 0;
            if (dir == 0) return;
            _active.AddLast(new Active
            {
                Type = type, Bias = bias, Session = session, Dir = dir, AnchorTicks = anchorTicks,
                PredictedRatio = predictedRatio, FireTicks = fireTicks,
            });
            while (_active.Count > MaxConcurrent) _active.RemoveFirst();
        }

        /// <summary>Preload persisted outcomes (e.g. on session startup when keeping history).</summary>
        public void LoadCompleted(IEnumerable<SignalOutcome> outcomes)
        {
            if (outcomes == null) return;
            lock (_completedGate)
            {
                foreach (var o in outcomes) _completed.Add(o);
                if (_completed.Count > MaxCompleted) _completed.RemoveRange(0, _completed.Count - MaxCompleted);
            }
        }

        /// <summary>Append the current mid to every in-flight path; complete those that hit the horizon.</summary>
        public void OnPriceSample(long midTicks)
        {
            var node = _active.First;
            while (node != null)
            {
                var next = node.Next;
                var a = node.Value;
                a.Path.Add((int)(a.Dir * (midTicks - a.AnchorTicks)));
                if (a.Path.Count >= HorizonSamples)
                {
                    var outcome = new SignalOutcome
                    {
                        Type = a.Type, Bias = a.Bias, Session = a.Session, PredictedRatio = a.PredictedRatio,
                        FireTimestampTicks = a.FireTicks, FavorableTicks = a.Path.ToArray(),
                    };
                    lock (_completedGate)
                    {
                        _completed.Add(outcome);
                        if (_completed.Count > MaxCompleted) _completed.RemoveRange(0, _completed.Count - MaxCompleted);
                    }
                    _active.Remove(node);
                }
                node = next;
            }
        }
    }
}
