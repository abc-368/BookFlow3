using System.Collections.Generic;

namespace BookFlow.Shared.Analytics
{
    /// <summary>
    /// Offline re-scoring of recorded signal outcomes (<see cref="SignalOutcome"/>): given the real
    /// forward paths, computes the win-rate and gross expectancy (ticks, fees aside) of ANY
    /// target/stop bracket via first-passage, and suggests the bracket that maximizes expectancy
    /// over a grid — gated by a minimum sample size. This is the engine of the feedback loop:
    /// every candidate is evaluated on the SAME recorded population, so no live trial-and-error
    /// (and no per-setting sample fragmentation) is needed.
    /// </summary>
    public static class BracketReScorer
    {
        public struct Score
        {
            public int TargetTicks;
            public int StopTicks;
            public int Wins;
            public int Losses;
            public int Unresolved;   // never reached either barrier within the recorded horizon
            public int Samples;      // Wins + Losses (resolved only)
            public double WinRate;
            public double ExpectancyTicks;
        }

        public static Score Evaluate(IReadOnlyList<SignalOutcome> outcomes, int targetTicks, int stopTicks)
        {
            int wins = 0, losses = 0, unresolved = 0;
            if (outcomes != null)
            {
                foreach (var o in outcomes)
                {
                    int r = FirstPassage(o.FavorableTicks, targetTicks, stopTicks);
                    if (r > 0) wins++;
                    else if (r < 0) losses++;
                    else unresolved++;
                }
            }
            int total = wins + losses;
            double wr = total > 0 ? (double)wins / total : 0.0;
            double exp = total > 0 ? wr * targetTicks - (1.0 - wr) * stopTicks : 0.0;
            return new Score
            {
                TargetTicks = targetTicks, StopTicks = stopTicks,
                Wins = wins, Losses = losses, Unresolved = unresolved,
                Samples = total, WinRate = wr, ExpectancyTicks = exp,
            };
        }

        // +1 favorable target reached first, -1 adverse stop reached first, 0 neither within horizon.
        private static int FirstPassage(int[] path, int targetTicks, int stopTicks)
        {
            if (path == null) return 0;
            foreach (var v in path)
            {
                if (v >= targetTicks) return 1;
                if (v <= -stopTicks) return -1;
            }
            return 0;
        }

        /// <summary>
        /// Grid-search the bracket maximizing gross expectancy, requiring at least
        /// <paramref name="minSamples"/> resolved samples. Returns a zero-sample Score if nothing qualifies.
        /// </summary>
        public static Score Suggest(IReadOnlyList<SignalOutcome> outcomes, int minSamples = 20, int maxTicks = 12)
        {
            Score best = default;
            bool have = false;
            for (int t = 2; t <= maxTicks; t++)
            {
                for (int s = 2; s <= maxTicks; s++)
                {
                    var sc = Evaluate(outcomes, t, s);
                    if (sc.Samples < minSamples) continue;
                    if (!have || sc.ExpectancyTicks > best.ExpectancyTicks ||
                        (sc.ExpectancyTicks == best.ExpectancyTicks && sc.StopTicks < best.StopTicks))
                    {
                        best = sc; have = true;
                    }
                }
            }
            return best;
        }
    }
}
