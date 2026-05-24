using System;
using BookFlow.Shared.Analytics;
using Xunit;

namespace BookFlow.Tests
{
    public class TradingContextTests
    {
        [Theory]
        [InlineData(9, TradingSession.US)]
        [InlineData(16, TradingSession.US)]
        [InlineData(3, TradingSession.London)]
        [InlineData(7, TradingSession.London)]
        [InlineData(17, TradingSession.Asia)]
        [InlineData(23, TradingSession.Asia)]
        [InlineData(2, TradingSession.Asia)]
        public void Classify_ByEasternHour(int hour, TradingSession expected)
        {
            Assert.Equal(expected, TradingContext.Classify(new DateTime(2026, 1, 1, hour, 0, 0)));
        }

        [Theory]
        [InlineData("ES 06-26", "ES")]
        [InlineData("MES 12-25", "MES")]
        [InlineData("CL 07-26", "CL")]
        [InlineData("6E 09-26", "6E")]
        [InlineData("ES", "ES")]
        [InlineData("  NQ 03-27  ", "NQ")]
        public void Root_StripsContractSuffix(string input, string expected)
        {
            Assert.Equal(expected, TradingContext.Root(input));
        }
    }
}
