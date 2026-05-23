using System;
using System.Collections.Generic;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.Service;

namespace BookFlow.NT8DataEngine.Service
{
    /// <summary>
    /// Authoritative per-ticker L2 depth book maintained server-side from the same
    /// market-data stream the clients consume (Q4). Updated exclusively by the AddOn's
    /// single ring-drain thread, so writes need no cross-thread coordination; snapshot
    /// reads (WCF worker threads) take a short lock.
    ///
    /// This closes the "blank ladder on late connect" gap: NT8 only replays the book as
    /// Add operations to whoever is subscribed at that instant (our indicator). A client
    /// connecting later missed that burst — so it pulls this snapshot to seed its ladder,
    /// then aligns the live MMF stream via the global sequence number.
    /// </summary>
    internal sealed class ServerBookRegistry
    {
        private readonly Dictionary<byte, InstrumentBook> _books = new Dictionary<byte, InstrumentBook>();
        private readonly object _registryGate = new object();

        private InstrumentBook GetOrAdd(byte tickerId)
        {
            // Called only from the drain thread, but guard against the registry dictionary
            // resizing while a snapshot enumerates it.
            lock (_registryGate)
            {
                if (!_books.TryGetValue(tickerId, out var book))
                {
                    book = new InstrumentBook();
                    _books[tickerId] = book;
                }
                return book;
            }
        }

        /// <summary>Applies one market-data message (drain thread). <paramref name="globalSeq"/>
        /// is the monotonic sequence stamped into the ring.</summary>
        public void Apply(ref UnifiedMarketDataMessage msg, long globalSeq)
        {
            var book = GetOrAdd(msg.TickerId);
            book.Apply(ref msg, globalSeq);
        }

        public DomSnapshotResponse BuildSnapshot(byte tickerId, string instrumentName)
        {
            InstrumentBook book;
            lock (_registryGate) { _books.TryGetValue(tickerId, out book); }
            if (book == null)
                return new DomSnapshotResponse { TickerId = tickerId, InstrumentName = instrumentName, ServerUtcTime = DateTime.UtcNow };
            return book.BuildSnapshot(tickerId, instrumentName);
        }

        public void Clear()
        {
            lock (_registryGate) _books.Clear();
        }

        private sealed class InstrumentBook
        {
            private readonly object _gate = new object();
            private readonly SortedDictionary<double, long> _bids = new SortedDictionary<double, long>();
            private readonly SortedDictionary<double, long> _asks = new SortedDictionary<double, long>();
            private long _lastSequence;
            private double _lastTradePrice;

            public void Apply(ref UnifiedMarketDataMessage msg, long globalSeq)
            {
                lock (_gate)
                {
                    _lastSequence = globalSeq;

                    if (msg.Category == MessageCategory.L2Data)
                    {
                        var side = (L2MarketSide)msg.MarketDataType;
                        var op = (L2Operation)msg.Operation;
                        var book = side == L2MarketSide.Bid ? _bids : _asks;
                        if (op == L2Operation.Remove || msg.Volume <= 0)
                            book.Remove(msg.Price);
                        else
                            book[msg.Price] = msg.Volume; // Add or Update
                    }
                    else if (msg.Category == MessageCategory.L1Data &&
                             (L1MarketDataType)msg.MarketDataType == L1MarketDataType.Last)
                    {
                        _lastTradePrice = msg.Price;
                    }
                }
            }

            public DomSnapshotResponse BuildSnapshot(byte tickerId, string instrumentName)
            {
                var resp = new DomSnapshotResponse
                {
                    TickerId = tickerId,
                    InstrumentName = instrumentName,
                    ServerUtcTime = DateTime.UtcNow,
                };
                lock (_gate)
                {
                    resp.LastSequence = _lastSequence;
                    resp.LastTradePrice = _lastTradePrice;
                    foreach (var kv in _bids) resp.Bids.Add(new DomSnapshotLevel { Price = kv.Key, Volume = kv.Value });
                    foreach (var kv in _asks) resp.Asks.Add(new DomSnapshotLevel { Price = kv.Key, Volume = kv.Value });
                }
                return resp;
            }
        }
    }
}
