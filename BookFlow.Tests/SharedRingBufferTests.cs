using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.IPC;
using Xunit;

namespace BookFlow.Tests
{
    public class SharedRingBufferTests
    {
        private static string Name() => "BFTest_" + Guid.NewGuid().ToString("N");
        private static UnifiedMarketDataMessage Msg(long seq) =>
            new UnifiedMarketDataMessage { Category = MessageCategory.L1Data, TickerId = 1, Sequence = seq, Price = seq, Volume = seq };

        [Fact]
        public void ProduceConsume_NoLoss_InOrder()
        {
            using var ring = new SharedRingBuffer(Name(), 1024);
            const int n = 500;
            for (long i = 1; i <= n; i++) { var m = Msg(i); Assert.True(ring.TryWrite(ref m)); }
            for (long i = 1; i <= n; i++)
            {
                Assert.True(ring.TryRead(out var m));
                Assert.Equal(i, m.Sequence);
            }
            Assert.False(ring.TryRead(out _)); // empty
        }

        [Fact]
        public void WriteWhenFull_ReturnsFalse()
        {
            using var ring = new SharedRingBuffer(Name(), 8);
            int written = 0;
            for (long i = 1; i <= 100; i++) { var m = Msg(i); if (ring.TryWrite(ref m)) written++; else break; }
            Assert.Equal(7, written); // capacity - 1 usable slots
        }

        [Fact]
        public void WrapAround_PreservesContinuity()
        {
            using var ring = new SharedRingBuffer(Name(), 8);
            long writeSeq = 1, expect = 1;
            for (int round = 0; round < 6; round++)
            {
                for (int k = 0; k < 4; k++) { var m = Msg(writeSeq++); Assert.True(ring.TryWrite(ref m)); }
                for (int k = 0; k < 4; k++) { Assert.True(ring.TryRead(out var m)); Assert.Equal(expect++, m.Sequence); }
            }
        }

        [Fact]
        public async Task ConcurrentSpsc_AllDeliveredInOrder()
        {
            using var ring = new SharedRingBuffer(Name(), 1024);
            const int n = 100_000;
            var consumed = new List<long>(n);

            var reader = Task.Run(() =>
            {
                long got = 0;
                while (got < n)
                {
                    if (ring.TryRead(out var m)) { consumed.Add(m.Sequence); got++; }
                    else Thread.SpinWait(5);
                }
            });
            var writer = Task.Run(() =>
            {
                for (long i = 1; i <= n; i++)
                {
                    var m = Msg(i);
                    while (!ring.TryWrite(ref m)) Thread.SpinWait(10);
                }
            });

            await Task.WhenAll(writer, reader);

            Assert.Equal(n, consumed.Count);
            for (int i = 0; i < n; i++) Assert.Equal(i + 1, consumed[i]);
        }
    }
}
