using System.Collections.Generic;
using BookFlow.Shared.Analytics;
using Xunit;

namespace BookFlow.Tests
{
    public class BracketReScorerTests
    {
        private static SignalOutcome Outcome(params int[] path) =>
            new SignalOutcome { Type = MicrostructureSignalType.Iceberg, Bias = MicrostructureBias.Up, FavorableTicks = path };

        [Fact]
        public void FirstPassage_TargetBeforeStop_IsWin()
        {
            // Rises to +5 (never down to -4 first) → win at 4/4 and at 5/4.
            var o = new List<SignalOutcome> { Outcome(0, 1, 2, 5, 3) };
            Assert.Equal(1, BracketReScorer.Evaluate(o, 4, 4).Wins);
            Assert.Equal(1, BracketReScorer.Evaluate(o, 5, 4).Wins);
            // Target 6 never reached, stop -4 never reached → unresolved.
            var s6 = BracketReScorer.Evaluate(o, 6, 4);
            Assert.Equal(0, s6.Samples);
            Assert.Equal(1, s6.Unresolved);
        }

        [Fact]
        public void FirstPassage_StopBeforeTarget_IsLoss()
        {
            // Dips to -4 before reaching +8.
            var o = new List<SignalOutcome> { Outcome(0, -2, -4, 8) };
            var s = BracketReScorer.Evaluate(o, 8, 4);
            Assert.Equal(1, s.Losses);
            Assert.Equal(0, s.Wins);
        }

        [Fact]
        public void Evaluate_Expectancy_MatchesWinRate()
        {
            // 3 wins (reach +4) and 1 loss (reach -4 first) at 4/4 → wr 0.75, exp 0.75*4 - 0.25*4 = 2.0.
            var o = new List<SignalOutcome>
            {
                Outcome(0, 4), Outcome(0, 4), Outcome(0, 4), Outcome(0, -4),
            };
            var s = BracketReScorer.Evaluate(o, 4, 4);
            Assert.Equal(3, s.Wins);
            Assert.Equal(1, s.Losses);
            Assert.Equal(0.75, s.WinRate, 5);
            Assert.Equal(2.0, s.ExpectancyTicks, 5);
        }

        [Fact]
        public void Suggest_PicksHighestExpectancy_AboveMinSamples()
        {
            // 20 paths that rise straight to +10. A wide target captures more reward at no extra risk.
            var o = new List<SignalOutcome>();
            for (int i = 0; i < 20; i++) o.Add(Outcome(0, 2, 4, 6, 8, 10));
            var best = BracketReScorer.Suggest(o, minSamples: 20, maxTicks: 12);
            Assert.True(best.Samples >= 20);
            Assert.Equal(10, best.TargetTicks); // 10 is the largest target all paths still hit
            Assert.Equal(1.0, best.WinRate, 5);
        }

        [Fact]
        public void Suggest_ReturnsEmpty_WhenBelowMinSamples()
        {
            var o = new List<SignalOutcome> { Outcome(0, 4), Outcome(0, 4) }; // only 2 samples
            var best = BracketReScorer.Suggest(o, minSamples: 20);
            Assert.Equal(0, best.Samples);
        }
    }
}
