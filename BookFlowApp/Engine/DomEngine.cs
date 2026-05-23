using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using BookFlow.App.Interfaces;
using BookFlow.Shared.Contracts;
using BookFlow.Shared.Service;

namespace BookFlow.App.Engine
{
    /// <summary>
    /// High-performance DOM engine that processes market data for a single instrument.
    /// Implements the direct connection architecture with local message filtering,
    /// double-buffering for thread-safe reads, and intelligent conflation for UI updates.
    /// </summary>
    public class DomEngine : IDomEngine, IDisposable
    {
        private readonly object _syncLock = new object();
        private readonly SortedDictionary<decimal, PriceLevel> _bidBook = new();
        private readonly SortedDictionary<decimal, PriceLevel> _askBook = new();
        
        // Lock-free snapshot publication: the writer (timer/ForceUpdate) builds a fresh
        // immutable BookSnapshot and atomically publishes it via a volatile reference.
        // Readers (UI poll / statistics) just read the reference — no lock, no contention.
        // Concurrent writers are serialized by _writerLock so they don't duplicate work.
        private volatile BookSnapshot _readerSnapshot;
        private readonly object _writerLock = new object();
        
        // Reactive streams
        private readonly Subject<LadderUpdate> _ladderUpdatesSubject = new();
        private readonly Subject<StatisticsUpdate> _statisticsUpdatesSubject = new();
        
        // Market state
        private decimal? _bestBid;
        private decimal? _bestAsk;
        private decimal? _lastTradedPrice;
        private long _lastTradedVolume;
        private decimal? _lastBidHitPrice; // last price a market sell executed against (hit the bid)
        private decimal? _lastAskHitPrice; // last price a market buy executed against (lifted the ask)
        private decimal _tickSize = 0.25m; // Provided from controller
        private decimal _pointValue = 50m;  // Provided from controller
        
        // Initialization flags
        private bool _hasReceivedFirstTrade = false;
        private int _priceDecimalPlaces = 2; // Display precision
        
        // Track when actual market data changes occur to prevent spam logging
        private long _lastMarketDataChangeSequence = 0;
        private long _lastPublishedSequence = 0;
        
        // Debug logging flag - static for performance (compile-time constant behavior)
        private static readonly bool _debugLoggingEnabled = false;
        
        // Performance tracking
        private long _messagesProcessed = 0;
        private long _sequenceNumber = 0;
        private readonly ConcurrentQueue<long> _latencyMeasurements = new();
        private DateTime _lastStatisticsUpdate = DateTime.UtcNow;
        
        // Configuration
        private int _visibleLevelsAbove = 20;
        private int _visibleLevelsBelow = 20;
        private decimal? _fixedCenterPrice = null; // Used when CenterMode is None
        private TimeSpan _conflationInterval = TimeSpan.FromMilliseconds(16); // ~60fps
        private readonly DomSettings _settings;
        
        // State
        private volatile bool _isConnected = false;
        private volatile bool _disposed = false;
        private IDataFeed? _dataFeed;
        private ITradingService? _tradingService;
        private IDisposable? _dataSubscription;
        private IDisposable? _portfolioSubscription;
        private System.Threading.Timer? _statisticsTimer;
        private System.Threading.Timer? _snapshotTimer;
        private PropertyChangedEventHandler? _settingsChangedHandler;
        private int _disposeGuard; // 0 = live, 1 = disposed (idempotency)

        // Q4 snapshot handshake: buffer live ticks while seeding the book from the server
        // snapshot, then drain (discarding ticks already in the snapshot) and go live.
        private readonly object _seedLock = new object();
        private readonly ConcurrentQueue<UnifiedMarketDataMessage> _seedBuffer = new();
        private volatile bool _seeding;

        public string InstrumentName { get; }
        public byte TickerId { get; }
        public bool IsConnected => _isConnected;
        public int PriceDecimalPlaces => _priceDecimalPlaces;
        public decimal TickSize => _tickSize;
        public decimal PointValue => _pointValue;
        
        public event EventHandler<bool>? ConnectionStatusChanged;
        
        // Observable streams with intelligent conflation
        public IObservable<LadderUpdate> LadderUpdates { get; }
        public IObservable<StatisticsUpdate> StatisticsUpdates { get; }
        
        public DomEngine(string instrumentName, byte tickerId, ITradingService tradingService, decimal tickSize = 0.25m, decimal pointValue = 50m, DomSettings? settings = null)
        {
            InstrumentName = instrumentName ?? throw new ArgumentNullException(nameof(instrumentName));
            TickerId = tickerId;
            _tradingService = tradingService;
            _tickSize = tickSize > 0 ? tickSize : 0.25m;
            _pointValue = pointValue > 0 ? pointValue : 50m;
            _settings = settings ?? new DomSettings();

            // Initialize display precision from tick-size so ladder advances on correct grid
            _priceDecimalPlaces = Math.Max(_priceDecimalPlaces, GetDecimalPlacesForStep(_tickSize));
            
            UpdateConfigurationFromSettings();
            
            // Initialize empty snapshot
            _readerSnapshot = CreateEmptySnapshot();
            
            // Set up conflated observable streams
            LadderUpdates = _ladderUpdatesSubject
                .Buffer(_conflationInterval)
                .Where(buffer => buffer.Any())
                .Select(buffer => buffer.Last()) // Take the most recent update
                .DistinctUntilChanged(x => x.SequenceNumber);
                
            // Note: No initial ladder publication - wait for real market data first
                
            StatisticsUpdates = _statisticsUpdatesSubject.AsObservable();
            
            // Set up periodic timers - much less aggressive timing
            _statisticsTimer = new System.Threading.Timer(UpdateStatistics, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            _snapshotTimer = new System.Threading.Timer(UpdateSnapshot, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)); // 100ms for smooth 10fps updates
        }

        public async Task<bool> StartAsync(IDataFeed dataFeed)
        {
            if (_disposed)
                return false;
            
            _dataFeed = dataFeed ?? throw new ArgumentNullException(nameof(dataFeed));
            
            try
            {
                if (!_dataFeed.IsConnected)
                {
                    if (!await _dataFeed.ConnectAsync())
                    {
                        OnError($"Failed to connect data feed for {InstrumentName}", new InvalidOperationException("Data feed connection failed"));
                        return false;
                    }
                }
                
                // Start buffering live ticks before requesting the snapshot, so nothing is
                // lost in the window between snapshot capture and going live (Q4 handshake).
                _seeding = true;
                _dataSubscription = _dataFeed.MarketDataStream
                    .Where(msg => msg.TickerId == TickerId)
                    .Subscribe(OnMarketMessage,
                              ex => OnError("Market data stream error", ex),
                              () => OnCompleted("Market data stream completed"));

                _portfolioSubscription = _dataFeed.PortfolioStream
                    .Subscribe(ProcessPortfolioUpdate,
                              ex => OnError("Portfolio stream error", ex));

                if (_tradingService != null)
                {
                    _tradingService.OrderBookChanged += OnOrderBookChanged;
                }

                _isConnected = true;
                ConnectionStatusChanged?.Invoke(this, true);

                // Seed the ladder from the authoritative server book, then drain buffered ticks.
                try
                {
                    var snapshot = await _dataFeed.RequestDomSnapshotAsync(TickerId);
                    SeedFromSnapshot(snapshot);
                }
                catch (Exception ex)
                {
                    OnError("DOM snapshot seed failed", ex);
                    SeedFromSnapshot(null); // degrade gracefully: go live, rebuild from stream
                }

                try { await _dataFeed.RequestPortfolioStateAsync(); } catch (Exception ex) { OnError("Failed to request initial portfolio state", ex); }

                return true;
            }
            catch (Exception ex)
            {
                OnError("Failed to start DomEngine", ex);
                return false;
            }
        }
        
        public async Task StopAsync()
        {
            _isConnected = false;
            ConnectionStatusChanged?.Invoke(this, false);

            if (_tradingService != null)
                _tradingService.OrderBookChanged -= OnOrderBookChanged;

            _dataSubscription?.Dispose();
            _portfolioSubscription?.Dispose();
            _dataSubscription = null;
            _portfolioSubscription = null;

            await Task.CompletedTask;
        }
        
        public void ProcessMessage(UnifiedMarketDataMessage message)
        {
            if (_disposed)
                return;
                
            try
            {
                Interlocked.Increment(ref _messagesProcessed);
                
                if (_debugLoggingEnabled && _messagesProcessed % 100 == 0)
                {
                    OnInfo($"Processed {_messagesProcessed} messages for TickerId {TickerId} (Message TickerId: {message.TickerId})");
                }
                
                if (message.NtReceiveTime > 0 && message.IpcQueueTime > 0)
                {
                    var latencyTicks = DateTime.UtcNow.Ticks - message.NtReceiveTime;
                    _latencyMeasurements.Enqueue(latencyTicks);
                    if (_latencyMeasurements.Count > 1000)
                        _latencyMeasurements.TryDequeue(out _);
                }
                
                lock (_syncLock)
                {
                    ProcessMarketDataMessage(message);
                }
            }
            catch (Exception ex)
            {
                OnError($"Error processing message for {InstrumentName}", ex);
            }
        }

        public void ForceUpdate()
        {
            UpdateSnapshot(null);
        }

        // Entry point for the live stream. While seeding, ticks are buffered; once the
        // snapshot is applied they are drained (deduped by global sequence) and we go live.
        private void OnMarketMessage(UnifiedMarketDataMessage message)
        {
            if (_seeding)
            {
                lock (_seedLock)
                {
                    if (_seeding) { _seedBuffer.Enqueue(message); return; }
                }
            }
            ProcessMessage(message);
        }

        // Seeds the books from the server L2 snapshot, then drains buffered live ticks,
        // discarding any whose global sequence (Reserved1) is already reflected in the
        // snapshot, and flips to live processing. Null/empty snapshot degrades to the old
        // behavior (build purely from the live stream).
        private void SeedFromSnapshot(DomSnapshotResponse? snapshot)
        {
            long lastSeq = snapshot?.LastSequence ?? 0;

            if (snapshot != null && (snapshot.Bids.Count > 0 || snapshot.Asks.Count > 0))
            {
                lock (_syncLock)
                {
                    _bidBook.Clear();
                    _askBook.Clear();
                    foreach (var b in snapshot.Bids)
                    {
                        var price = AlignToTick((decimal)b.Price);
                        var lvl = PriceLevel.CreateEmpty(price);
                        lvl.UpdateBid(b.Volume, 1);
                        _bidBook[price] = lvl;
                    }
                    foreach (var a in snapshot.Asks)
                    {
                        var price = AlignToTick((decimal)a.Price);
                        var lvl = PriceLevel.CreateEmpty(price);
                        lvl.UpdateAsk(a.Volume, 1);
                        _askBook[price] = lvl;
                    }
                    UpdateBestPricesFromBook();
                    if (snapshot.LastTradePrice > 0)
                        _lastTradedPrice = AlignToTick((decimal)snapshot.LastTradePrice);
                    Interlocked.Increment(ref _lastMarketDataChangeSequence);
                }
                OnInfo($"Seeded ladder from snapshot: {snapshot.Bids.Count} bids, {snapshot.Asks.Count} asks @ seq {lastSeq}");
            }

            // Drain buffered live ticks under the seed lock and flip to live atomically so
            // no message is lost or reordered against the snapshot.
            lock (_seedLock)
            {
                while (_seedBuffer.TryDequeue(out var m))
                {
                    if (m.Reserved1 > lastSeq) ProcessMessage(m);
                }
                _seeding = false;
            }

            ForceUpdate();
        }
        
        private void ProcessMarketDataMessage(UnifiedMarketDataMessage message)
        {
            switch (message.Category)
            {
                case MessageCategory.L1Data:
                    ProcessL1Update(message);
                    break;
                case MessageCategory.L2Data:
                    ProcessL2Update(message);
                    break;
                case MessageCategory.EventData:
                    break;
            }
        }
        
        private void ProcessL1Update(UnifiedMarketDataMessage message)
        {
            var dataType = (L1MarketDataType)message.MarketDataType;
            // Use raw price to detect scale, then apply correction
            var rawPrice = (decimal)message.Price;
            // Dynamically detect and update price precision from observed prices
            UpdatePricePrecisionFromObservedPrice(rawPrice);
            // Do not apply auto scale correction; use raw prices aligned to tick size
            var price = AlignToTick(rawPrice);

            switch (dataType)
            {
                case L1MarketDataType.Bid:
                    UpdateBestBid(price, message.Volume);
                    break;
                case L1MarketDataType.Ask:
                    UpdateBestAsk(price, message.Volume);
                    break;
                case L1MarketDataType.Last:
                    UpdateLastTrade(price, message.Volume);
                    break;
            }
        }
        
        private void ProcessL2Update(UnifiedMarketDataMessage message)
        {
            var operation = (L2Operation)message.Operation;
            var side = (L2MarketSide)message.MarketDataType;
            var rawPrice = (decimal)message.Price;
            UpdatePricePrecisionFromObservedPrice(rawPrice);
            var price = AlignToTick(rawPrice);
            var volume = message.Volume;

            // Always process depth normally
            var book = side == L2MarketSide.Bid ? _bidBook : _askBook;

            switch (operation)
            {
                case L2Operation.Add:
                case L2Operation.Update:
                    if (volume > 0)
                    {
                        if (!book.TryGetValue(price, out var level))
                        {
                            level = PriceLevel.CreateEmpty(price);
                            book[price] = level;
                        }

                        if (side == L2MarketSide.Bid)
                            level.UpdateBid(volume, 1);
                        else
                            level.UpdateAsk(volume, 1);

                        book[price] = level;

                        Interlocked.Increment(ref _lastMarketDataChangeSequence);
                    }
                    else
                    {
                        book.Remove(price);
                        Interlocked.Increment(ref _lastMarketDataChangeSequence);
                    }
                    break;

                case L2Operation.Remove:
                    book.Remove(price);
                    Interlocked.Increment(ref _lastMarketDataChangeSequence);
                    break;
            }

            UpdateBestPricesFromBook();
            // NOTE: engine-level crossed-level pruning intentionally omitted. Best-price
            // resolution leaves a crossing level stranded in the spread (not >= bestAsk),
            // so a resolved-best prune is a no-op there, and raw-extreme pruning risks
            // removing valid depth (which leg is stale is ambiguous from depth alone).
            // Q3 is handled robustly at the display layer via IsBidZone/IsAskZone gating.
        }

        private void UpdateBestBid(decimal price, long volume)
        {
            if (volume > 0)
            {
                // Validate that new bid doesn't create crossed market
                if (_bestAsk.HasValue && price >= _bestAsk.Value)
                {
                    if (_debugLoggingEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DomEngine] REJECTING CROSSED BID: {price:F6} >= Ask {_bestAsk.Value:F6}");
                    }
                    return; // Reject bid update that would create crossed market
                }
                
                _bestBid = price;
                if (!_bidBook.ContainsKey(price))
                {
                    var level = PriceLevel.CreateEmpty(price);
                    level.UpdateBid(volume, 1, BookLevelFlags.IsTopOfBook);
                    _bidBook[price] = level;
                }
            }
            else
            {
                _bidBook.Remove(price);
                _bestBid = _bidBook.Any() ? _bidBook.Keys.Last() : (decimal?)null;
                
                // After removing bid, validate integrity with current ask
                if (_bestBid.HasValue && _bestAsk.HasValue && _bestBid.Value >= _bestAsk.Value)
                {
                    var validBid = _bidBook.Keys.Where(p => p < _bestAsk.Value).LastOrDefault();
                    _bestBid = _bidBook.Any() && validBid != default(decimal) ? validBid : (decimal?)null;
                }
            }
            
            // Mark that actual market data changed
            Interlocked.Increment(ref _lastMarketDataChangeSequence);
        }
        
        private void UpdateBestAsk(decimal price, long volume)
        {
            if (volume > 0)
            {
                // Validate that new ask doesn't create crossed market
                if (_bestBid.HasValue && price <= _bestBid.Value)
                {
                    if (_debugLoggingEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DomEngine] REJECTING CROSSED ASK: {price:F6} <= Bid {_bestBid.Value:F6}");
                    }
                    return; // Reject ask update that would create crossed market
                }
                
                _bestAsk = price;
                if (!_askBook.ContainsKey(price))
                {
                    var level = PriceLevel.CreateEmpty(price);
                    level.UpdateAsk(volume, 1, BookLevelFlags.IsTopOfBook);
                    _askBook[price] = level;
                }
            }
            else
            {
                _askBook.Remove(price);
                _bestAsk = _askBook.Any() ? _askBook.Keys.First() : (decimal?)null;
                
                // After removing ask, validate integrity with current bid
                if (_bestBid.HasValue && _bestAsk.HasValue && _bestBid.Value >= _bestAsk.Value)
                {
                    var validAsk = _askBook.Keys.Where(p => p > _bestBid.Value).FirstOrDefault();
                    _bestAsk = _askBook.Any() && validAsk != default(decimal) ? validAsk : (decimal?)null;
                }
            }
            
            // Mark that actual market data changed
            Interlocked.Increment(ref _lastMarketDataChangeSequence);
        }
        
        private void UpdateLastTrade(decimal price, long volume)
        {
            if (!_hasReceivedFirstTrade)
            {
                _hasReceivedFirstTrade = true;
                CenterDom();
            }

            _lastTradedPrice = price;
            _lastTradedVolume = volume;

            Interlocked.Increment(ref _lastMarketDataChangeSequence);

            // Record the aggressor-side execution price (set after hitBid is resolved below).

            // Determine trade side using best bid/ask when available; otherwise use proximity
            bool hitBid;
            if (_bestBid.HasValue && _bestAsk.HasValue && _bestBid > 0 && _bestAsk > 0)
            {
                // If price is below/at bid -> hit bid; above/at ask -> hit ask; otherwise closer side
                if (price <= _bestBid.Value) hitBid = true;
                else if (price >= _bestAsk.Value) hitBid = false;
                else hitBid = Math.Abs(price - _bestBid.Value) <= Math.Abs(price - _bestAsk.Value);
            }
            else if (_bestBid.HasValue && _bestBid > 0)
            {
                hitBid = price <= _bestBid.Value;
            }
            else if (_bestAsk.HasValue && _bestAsk > 0)
            {
                hitBid = price < _bestAsk.Value;
            }
            else
            {
                // Fallback: assume bid hit
                hitBid = true;
            }

            // Q1: remember the last execution price per aggressor side.
            if (hitBid) _lastBidHitPrice = price;
            else _lastAskHitPrice = price;

            if (_bidBook.Count == 0 && _askBook.Count == 0)
                return;

            // `price` is already tick-aligned (ProcessL1Update -> AlignToTick), so the level
            // is almost always the exact key: O(log N) dictionary lookups, not an O(N) scan
            // of both books on every trade tick (Antigravity 2.1).
            decimal nearestKey;
            if (_bidBook.ContainsKey(price) || _askBook.ContainsKey(price))
            {
                nearestKey = price;
            }
            else
            {
                // Off-grid fallback: nearest key across both books.
                nearestKey = 0m;
                decimal minDiff = decimal.MaxValue;
                foreach (var key in _bidBook.Keys)
                {
                    var diff = Math.Abs(key - price);
                    if (diff < minDiff) { minDiff = diff; nearestKey = key; }
                }
                foreach (var key in _askBook.Keys)
                {
                    var diff = Math.Abs(key - price);
                    if (diff < minDiff) { minDiff = diff; nearestKey = key; }
                }
                var maxAllowedDiff = _tickSize > 0 ? _tickSize / 2m : 0.0000001m;
                if (minDiff > maxAllowedDiff) return; // too far off-grid to attribute
            }

            // Mutate each side's level INDEPENDENTLY. On a locked market the same price
            // exists in both books; writing a merged level back to both would clobber the
            // opposite side's depth (audit P0). Each book keeps its own volume.
            if (_bidBook.TryGetValue(nearestKey, out var bidLevel))
            {
                bidLevel.RecordTrade(volume, hitBid);
                _bidBook[nearestKey] = bidLevel;
            }
            if (_askBook.TryGetValue(nearestKey, out var askLevel))
            {
                askLevel.RecordTrade(volume, hitBid);
                _askBook[nearestKey] = askLevel;
            }
        }
        
        private void UpdateBestPricesFromBook()
        {
            var candidateBestBid = _bidBook.Any() ? _bidBook.Keys.Last() : (decimal?)null;
            var candidateBestAsk = _askBook.Any() ? _askBook.Keys.First() : (decimal?)null;
            
            // Normal case - assign directly
            _bestBid = candidateBestBid;
            _bestAsk = candidateBestAsk;
            
            // Validate market integrity - best bid must be lower than best ask
            if (_bestBid.HasValue && _bestAsk.HasValue && _bestBid.Value >= _bestAsk.Value)
            {
                // Crossed market detected - find nearest valid combination
                var validBid = _bidBook.Keys.Where(price => price < _bestAsk.Value).LastOrDefault();
                var validAsk = _askBook.Keys.Where(price => price > _bestBid.Value).FirstOrDefault();
                
                _bestBid = _bidBook.Any() && validBid != default(decimal) ? validBid : (decimal?)null;
                _bestAsk = _askBook.Any() && validAsk != default(decimal) ? validAsk : (decimal?)null;
            }
        }
        
        private void OnOrderBookChanged()
        {
            try
            {
                // Force a ladder publish quickly to reflect order changes/fills without waiting on market data ticks
                PublishLadderUpdate();
            }
            catch { }
        }
        
        private void ProcessPortfolioUpdate(PortfolioStateMessage portfolio)
        {
            // This is now handled by the NT8TradingService
        }
        
        private void UpdateSnapshot(object? state)
        {
            if (_disposed)
                return;
                
            // Serialize writers so two concurrent UpdateSnapshot calls (timer + ForceUpdate)
            // don't duplicate the build. Readers never take this lock.
            lock (_writerLock)
            {
                Dictionary<decimal, PriceLevel> bidBookSnapshot = new();
                Dictionary<decimal, PriceLevel> askBookSnapshot = new();
                decimal? centerPrice;

                // Calculate price range around current market (limit to reasonable range)
                var maxLevelsPerSide = Math.Max(_visibleLevelsAbove, _visibleLevelsBelow) + 50; // Extra buffer beyond visible

                // Take snapshots of only relevant price levels under the processing lock to avoid
                // cloning the entire book and minimize lock duration (Antigravity 2.2).
                lock (_syncLock)
                {
                    centerPrice = _bestBid ?? _bestAsk ?? _lastTradedPrice;
                    if (centerPrice.HasValue)
                    {
                        var minBidPrice = centerPrice.Value - (maxLevelsPerSide * _tickSize);
                        var maxAskPrice = centerPrice.Value + (maxLevelsPerSide * _tickSize);
                        
                        foreach (var kvp in _bidBook)
                        {
                            if (kvp.Key >= minBidPrice)
                                bidBookSnapshot[kvp.Key] = kvp.Value;
                        }
                        foreach (var kvp in _askBook)
                        {
                            if (kvp.Key <= maxAskPrice)
                                askBookSnapshot[kvp.Key] = kvp.Value;
                        }
                    }
                    else
                    {
                        // Fallback: copy everything if no center price
                        foreach (var kvp in _bidBook) bidBookSnapshot[kvp.Key] = kvp.Value;
                        foreach (var kvp in _askBook) askBookSnapshot[kvp.Key] = kvp.Value;
                    }
                }
                
                // Create new snapshot from current state - only include relevant price levels
                var combinedBook = new Dictionary<decimal, PriceLevel>();
                
                // Get relevant bid levels (top N levels only) - now safe from enumeration
                var relevantBids = centerPrice.HasValue 
                    ? bidBookSnapshot.Where(kvp => kvp.Key >= centerPrice - (maxLevelsPerSide * _tickSize))
                                    .OrderByDescending(kvp => kvp.Key) // Highest prices first for bids
                                    .Take(maxLevelsPerSide)
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                    : bidBookSnapshot.OrderByDescending(kvp => kvp.Key)
                                    .Take(maxLevelsPerSide)
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                
                // Get relevant ask levels (top N levels only) - now safe from enumeration
                var relevantAsks = centerPrice.HasValue 
                    ? askBookSnapshot.Where(kvp => kvp.Key <= centerPrice + (maxLevelsPerSide * _tickSize))
                                    .OrderBy(kvp => kvp.Key) // Lowest prices first for asks
                                    .Take(maxLevelsPerSide)
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                    : askBookSnapshot.OrderBy(kvp => kvp.Key)
                                    .Take(maxLevelsPerSide)
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                
                // Merge bid and ask books
                foreach (var kvp in relevantBids)
                {
                    combinedBook[kvp.Key] = kvp.Value;
                }
                
                foreach (var kvp in relevantAsks)
                {
                    if (combinedBook.TryGetValue(kvp.Key, out var existing))
                    {
                        // Merge ask data into existing bid level
                        existing.UpdateAsk(kvp.Value.AskVolume, kvp.Value.AskCount);
                        combinedBook[kvp.Key] = existing;
                    }
                    else
                    {
                        combinedBook[kvp.Key] = kvp.Value;
                    }
                }
                
                // Debug logging if we're processing too many levels
                if (combinedBook.Count > 200)
                {
                    OnError($"Warning: Processing {combinedBook.Count} price levels (BidBook: {_bidBook.Count}, AskBook: {_askBook.Count})", 
                           new InvalidOperationException("Excessive price levels detected"));
                }

                // Update our own order counts in the book (only if trading service is connected)
                if (_tradingService != null && _tradingService.IsConnected)
                {
                    UpdateOrderCountsInBook(combinedBook);
                }
                
                // Atomically publish the freshly-built immutable snapshot. The volatile
                // reference write makes it visible to all readers without a lock.
                _readerSnapshot = new BookSnapshot(
                    combinedBook,
                    InstrumentName,
                    TickerId,
                    _tickSize,
                    _bestBid,
                    _bestAsk,
                    _lastTradedPrice,
                    _lastTradedVolume);
            }

            // Publish ladder update (outside the writer lock).
            PublishLadderUpdate();
        }

        private void UpdateOrderCountsInBook(Dictionary<decimal, PriceLevel> book)
        {
            if (_tradingService == null) return;

            // First, reset all our order counts to zero for existing levels
            foreach (var price in book.Keys.ToList())
            {
                var level = book[price];
                level.MyBidOrderCount = 0;
                level.MyAskOrderCount = 0;
                book[price] = level;
            }

            // Now, recount based on the current working orders, creating levels if they don't exist
            foreach (var order in _tradingService.WorkingOrders)
            {
                if (order.Instrument != InstrumentName) continue;

                var price = (decimal)order.Price;
                var remaining = order.Quantity - order.FilledQuantity;
                if (remaining <= 0) continue;

                if (!book.TryGetValue(price, out var level))
                {
                    level = PriceLevel.CreateEmpty(price);
                }

                // Update the count for the correct side with sign convention:
                // - Buy orders increment Bid Ord (+)
                // - Sell orders decrement Ask Ord (-)
                if (order.Side == 1) // 1 == Buy
                {
                    level.MyBidOrderCount += remaining;
                }
                else // 2 == Sell
                {
                    level.MyAskOrderCount += remaining; // keep positive; UI formats with '-'
                }
                
                book[price] = level;
            }
        }
        
        private void PublishLadderUpdate()
        {
            try
            {
                // Previously required first trade; now allow publishing as soon as we have any book state
                var snapshot = GetBookSnapshot();

                var centerMode = _settings.CenterMode;
                List<PriceLevel> visibleLevels;
                switch (centerMode)
                {
                    case CenterMode.None:
                        if (!_fixedCenterPrice.HasValue)
                        {
                            if (_bestBid.HasValue && _bestAsk.HasValue)
                                _fixedCenterPrice = (_bestBid.Value + _bestAsk.Value) / 2;
                            else if (_lastTradedPrice.HasValue)
                                _fixedCenterPrice = _lastTradedPrice.Value;
                            else if (snapshot.BidDepthLevels > 0 || snapshot.AskDepthLevels > 0)
                                _fixedCenterPrice = (_bestBid ?? _bestAsk) ?? 0m;
                        }
                        visibleLevels = snapshot.GetVisibleLadder(_visibleLevelsAbove, _visibleLevelsBelow, centerMode, _fixedCenterPrice);
                        break;
                    case CenterMode.OneTime:
                        visibleLevels = snapshot.GetVisibleLadder(_visibleLevelsAbove, _visibleLevelsBelow, centerMode);
                        if (_bestBid.HasValue && _bestAsk.HasValue)
                            _fixedCenterPrice = (_bestBid.Value + _bestAsk.Value) / 2;
                        else if (_lastTradedPrice.HasValue)
                            _fixedCenterPrice = _lastTradedPrice.Value;
                        else if (snapshot.BidDepthLevels > 0 || snapshot.AskDepthLevels > 0)
                            _fixedCenterPrice = (_bestBid ?? _bestAsk) ?? 0m;
                        _settings.CenterMode = CenterMode.None;
                        break;
                    case CenterMode.Continuous:
                    default:
                        _fixedCenterPrice = null;
                        visibleLevels = snapshot.GetVisibleLadder(_visibleLevelsAbove, _visibleLevelsBelow, centerMode);
                        break;
                }

                var update = new LadderUpdate
                {
                    InstrumentName = InstrumentName,
                    TickerId = TickerId,
                    VisibleLevels = visibleLevels,
                    BestBid = _bestBid,
                    BestAsk = _bestAsk,
                    LastPrice = _lastTradedPrice,
                    LastVolume = _lastTradedVolume,
                    Spread = snapshot.GetSpread(),
                    LastBidHitPrice = _lastBidHitPrice,
                    LastAskHitPrice = _lastAskHitPrice,
                    SequenceNumber = Interlocked.Increment(ref _sequenceNumber)
                };

                for (int i = 0; i < visibleLevels.Count; i++)
                {
                    if (_bestBid.HasValue && visibleLevels[i].Price == _bestBid.Value && visibleLevels[i].BidVolume > 0)
                        update.BestBidIndex = i;
                    if (_bestAsk.HasValue && visibleLevels[i].Price == _bestAsk.Value && visibleLevels[i].AskVolume > 0)
                        update.BestAskIndex = i;
                }

                _ladderUpdatesSubject.OnNext(update);
                _lastPublishedSequence = _lastMarketDataChangeSequence;
            }
            catch (Exception ex)
            {
                OnError("Error publishing ladder update", ex);
            }
        }
        
        private void UpdateStatistics(object? state)
        {
            if (_disposed)
                return;
                
            try
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastStatisticsUpdate;
                _lastStatisticsUpdate = now;
                
                var messagesThisSecond = Interlocked.Exchange(ref _messagesProcessed, 0);
                
                // Prevent division by zero and overflow
                double messageRate = 0;
                if (elapsed.TotalSeconds > 0 && messagesThisSecond < long.MaxValue)
                {
                    messageRate = Math.Min(messagesThisSecond / elapsed.TotalSeconds, double.MaxValue);
                }
                
                // Calculate average latency with overflow protection
                double averageLatencyMicros = 0;
                if (_latencyMeasurements.Count > 0)
                {
                    var latencies = new List<long>();
                    int maxLatencies = Math.Min(1000, _latencyMeasurements.Count); // Limit to prevent memory issues
                    
                    for (int i = 0; i < maxLatencies && _latencyMeasurements.TryDequeue(out var latency); i++)
                    {
                        // Cap individual latencies to prevent overflow
                        latencies.Add(Math.Min(latency, long.MaxValue / 1000));
                    }
                    
                    if (latencies.Count > 0)
                    {
                        try
                        {
                            // Use double arithmetic to prevent overflow
                            double sum = latencies.Select(l => (double)l).Sum();
                            averageLatencyMicros = (sum / latencies.Count) / 10.0; // Ticks to microseconds
                            
                            // Cap the result to prevent ridiculous values
                            averageLatencyMicros = Math.Min(averageLatencyMicros, 1000000); // Max 1 second
                        }
                        catch (OverflowException)
                        {
                            averageLatencyMicros = 0; // Reset on overflow
                        }
                    }
                }
                
                // Get snapshot with additional error handling
                BookSnapshot snapshot;
                try
                {
                    snapshot = GetBookSnapshot();
                }
                catch (Exception ex)
                {
                    OnError("Error getting book snapshot for statistics", ex);
                    return; // Skip this statistics update
                }
                
                // Calculate order imbalance with error handling
                double orderImbalance = 0;
                try
                {
                    orderImbalance = snapshot.GetOrderImbalance();
                    
                    // Validate the result
                    if (double.IsNaN(orderImbalance) || double.IsInfinity(orderImbalance))
                    {
                        orderImbalance = 0;
                    }
                }
                catch (Exception ex)
                {
                    OnError("Error calculating order imbalance", ex);
                    orderImbalance = 0; // Use safe default
                }
                
                var update = new StatisticsUpdate
                {
                    InstrumentName = InstrumentName,
                    TickerId = TickerId,
                    TotalBidVolume = snapshot.TotalBidVolume,
                    TotalAskVolume = snapshot.TotalAskVolume,
                    BidLevels = snapshot.BidDepthLevels,
                    AskLevels = snapshot.AskDepthLevels,
                    OrderImbalance = orderImbalance,
                    MessageRate = Math.Max(0, messageRate), // Ensure non-negative
                    AverageLatencyMicros = Math.Max(0, averageLatencyMicros) // Ensure non-negative
                };
                
                _statisticsUpdatesSubject.OnNext(update);
            }
            catch (Exception ex)
            {
                OnError("Error updating statistics", ex);
            }
        }
        
        public BookSnapshot GetBookSnapshot()
        {
            // Lock-free: volatile read of the latest published immutable snapshot.
            return _readerSnapshot;
        }
        
        public StatisticsSnapshot GetStatisticsSnapshot()
        {
            var snapshot = GetBookSnapshot();
            return new StatisticsSnapshot(
                InstrumentName,
                TickerId,
                snapshot.TotalBidVolume,
                snapshot.TotalAskVolume,
                snapshot.BidDepthLevels,
                snapshot.AskDepthLevels,
                snapshot.GetOrderImbalance());
        }
        
        // Order management methods
        public async Task<OrderStatusMessage> PlaceMarketOrderAsync(OrderCommand.OrderAction action, int quantity)
        {
            if (_dataFeed == null)
                throw new InvalidOperationException("Not connected to data feed");
                
            var orderCommand = new OrderCommand
            {
                Action = action,
                Quantity = quantity,
                ClientOrderId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow
            };
            
            return await _dataFeed.SubmitOrderAsync(InstrumentName, orderCommand);
        }
        
        public async Task<OrderStatusMessage> PlaceLimitOrderAsync(OrderCommand.OrderAction action, int quantity, double limitPrice)
        {
            if (_dataFeed == null)
                throw new InvalidOperationException("Not connected to data feed");
                
            var orderCommand = new OrderCommand
            {
                Action = action == OrderCommand.OrderAction.BuyMarket ? OrderCommand.OrderAction.BuyLimit : OrderCommand.OrderAction.SellLimit,
                Quantity = quantity,
                LimitPrice = limitPrice,
                ClientOrderId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow
            };
            
            return await _dataFeed.SubmitOrderAsync(InstrumentName, orderCommand);
        }
        
        public async Task<OrderStatusMessage> CancelAllOrdersAsync()
        {
            if (_dataFeed == null)
                throw new InvalidOperationException("Not connected to data feed");
                
            var orderCommand = new OrderCommand
            {
                Action = OrderCommand.OrderAction.CancelAll,
                ClientOrderId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow
            };
            
            return await _dataFeed.SubmitOrderAsync(InstrumentName, orderCommand);
        }
        
        public async Task<OrderStatusMessage> FlattenPositionAsync()
        {
            if (_dataFeed == null)
                throw new InvalidOperationException("Not connected to data feed");
                
            var orderCommand = new OrderCommand
            {
                Action = OrderCommand.OrderAction.Flat,
                ClientOrderId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow
            };
            
            return await _dataFeed.SubmitOrderAsync(InstrumentName, orderCommand);
        }
        
        public void ClearAllData()
        {
            lock (_syncLock)
            {
                // Clear all order books
                _bidBook.Clear();
                _askBook.Clear();
                
                // Reset market data
                _bestBid = null;
                _bestAsk = null;
                _lastTradedPrice = null;
                _lastTradedVolume = 0;
                
                // Reset trade tracking
                _hasReceivedFirstTrade = false;
                _lastMarketDataChangeSequence = 0;
                _lastPublishedSequence = 0;
                
                // Reset performance counters
                _messagesProcessed = 0;
                
                // Clear snapshot
                _readerSnapshot = CreateEmptySnapshot();
            }
            
            // Publish empty ladder update to clear UI
            PublishLadderUpdate();
            
            if (_debugLoggingEnabled)
            {
                System.Diagnostics.Debug.WriteLine($"[DomEngine:{InstrumentName}] All data cleared");
            }
        }
        
        public void CenterDom()
        {
            System.Diagnostics.Debug.WriteLine("[DEBUG] CenterDom called - triggering one-time center");
            
            // Trigger an immediate center action without changing persistent mode
            PublishLadderUpdateWithOneTimeCenter();
            
            System.Diagnostics.Debug.WriteLine("[DEBUG] CenterDom completed");
        }
        
        private void PublishLadderUpdateWithOneTimeCenter()
        {
            try
            {
                var snapshot = GetBookSnapshot();

                // Compute a safe center price explicitly
                decimal? centerPrice = null;
                var bid = _bestBid.GetValueOrDefault();
                var ask = _bestAsk.GetValueOrDefault();
                var last = _lastTradedPrice.GetValueOrDefault();
                if (_bestBid.HasValue && _bestAsk.HasValue && bid > 0 && ask > 0)
                    centerPrice = (bid + ask) / 2m;
                else if (_bestBid.HasValue && bid > 0)
                    centerPrice = bid;
                else if (_bestAsk.HasValue && ask > 0)
                    centerPrice = ask;
                else if (_lastTradedPrice.HasValue && last > 0)
                    centerPrice = last;

                // Fallback to prior fixed center if computed center is unavailable
                if (!centerPrice.HasValue && _fixedCenterPrice.HasValue)
                    centerPrice = _fixedCenterPrice.Value;

                var visibleLevels = snapshot.GetVisibleLadder(_visibleLevelsAbove, _visibleLevelsBelow, CenterMode.None, centerPrice);

                // Maintain _fixedCenterPrice when CenterMode.None is active
                if (centerPrice.HasValue)
                    _fixedCenterPrice = centerPrice.Value;

                var update = new LadderUpdate
                {
                    InstrumentName = InstrumentName,
                    TickerId = TickerId,
                    VisibleLevels = visibleLevels,
                    BestBid = _bestBid,
                    BestAsk = _bestAsk,
                    LastPrice = _lastTradedPrice,
                    LastVolume = _lastTradedVolume,
                    Spread = snapshot.GetSpread(),
                    LastBidHitPrice = _lastBidHitPrice,
                    LastAskHitPrice = _lastAskHitPrice,
                    SequenceNumber = Interlocked.Increment(ref _sequenceNumber)
                };

                for (int i = 0; i < visibleLevels.Count; i++)
                {
                    if (_bestBid.HasValue && visibleLevels[i].Price == _bestBid.Value && visibleLevels[i].BidVolume > 0)
                        update.BestBidIndex = i;
                    if (_bestAsk.HasValue && visibleLevels[i].Price == _bestAsk.Value && visibleLevels[i].AskVolume > 0)
                        update.BestAskIndex = i;
                }

                _ladderUpdatesSubject.OnNext(update);
            }
            catch (Exception ex)
            {
                OnError("Error publishing one-time center ladder update", ex);
            }
        }
        
        public (double? BestBid, double? BestAsk) GetBestBidAsk()
        {
            return ((double?)_bestBid, (double?)_bestAsk);
        }
        
        public double? GetSpread()
        {
            if (_bestBid.HasValue && _bestAsk.HasValue)
                return (double)(_bestAsk.Value - _bestBid.Value);
            return null;
        }
        
        private BookSnapshot CreateEmptySnapshot()
        {
            return new BookSnapshot(
                new Dictionary<decimal, PriceLevel>(),
                InstrumentName,
                TickerId,
                _tickSize);
        }
        
        private void OnError(string message, Exception ex)
        {
            // Always persist errors — previously these vanished in production (audit P1-4).
            BookFlow.App.Diagnostics.BookFlowLog.Error($"DomEngine:{InstrumentName}", message, ex);
            if (_debugLoggingEnabled)
            {
                System.Diagnostics.Debug.WriteLine($"[DomEngine:{InstrumentName}] {message}: {ex.Message}");
            }
        }

        private void OnCompleted(string message)
        {
            // Data-stream completion is a meaningful signal (feed ended) — always record it.
            BookFlow.App.Diagnostics.BookFlowLog.Info($"DomEngine:{InstrumentName}", message);
            if (_debugLoggingEnabled)
            {
                System.Diagnostics.Debug.WriteLine($"[DomEngine:{InstrumentName}] {message}");
            }
        }
        
        private void OnInfo(string message)
        {
            if (_debugLoggingEnabled)
            {
                System.Diagnostics.Debug.WriteLine($"[DomEngine:{InstrumentName}] INFO: {message}");
            }
        }
        
        private void UpdateConfigurationFromSettings()
        {
            // Calculate visible levels based on settings
            var totalRows = _settings.MaxVisibleRows;
            var centerOffset = Math.Min(_settings.CenterPriceOffset, totalRows / 2);
            
            _visibleLevelsAbove = centerOffset;
            _visibleLevelsBelow = totalRows - centerOffset;
            _conflationInterval = TimeSpan.FromMilliseconds(_settings.RefreshRateMs);

            // Subscribe to settings changes. Stored as a field so Dispose can detach it —
            // otherwise DomSettings (which can outlive the engine) roots the engine forever.
            _settingsChangedHandler = (sender, args) =>
            {
                if (args.PropertyName == nameof(DomSettings.MaxVisibleRows) ||
                    args.PropertyName == nameof(DomSettings.CenterPriceOffset))
                {
                    var newTotalRows = _settings.MaxVisibleRows;
                    var newCenterOffset = Math.Min(_settings.CenterPriceOffset, newTotalRows / 2);

                    _visibleLevelsAbove = newCenterOffset;
                    _visibleLevelsBelow = newTotalRows - newCenterOffset;
                }
                else if (args.PropertyName == nameof(DomSettings.RefreshRateMs))
                {
                    _conflationInterval = TimeSpan.FromMilliseconds(_settings.RefreshRateMs);
                }
            };
            _settings.PropertyChanged += _settingsChangedHandler;
        }
        
        public DomSettings Settings => _settings;
        
        /// <summary>
        /// Detects the number of decimal places needed to properly display a price.
        /// Examples: 6000.25 → 2, 1.16335 → 5, 100.0 → 0
        /// </summary>
        private static int GetDecimalPlacesForStep(decimal step)
        {
            step = Math.Abs(step);
            if (step == 0) return 2;
            var s = step.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
            var idx = s.IndexOf('.');
            if (idx < 0) return 0;
            var decimals = s.Substring(idx + 1).TrimEnd('0').Length;
            return Math.Max(decimals, 2);
        }

        private int DetectDecimalPlaces(decimal price)
        {
            // Convert to string and analyze decimal places
            string priceString = price.ToString("G29"); // Use general format to avoid scientific notation
            
            int decimalIndex = priceString.IndexOf('.');
            if (decimalIndex == -1)
            {
                // No decimal point, probably an integer price
                return 0;
            }
            
            // Count meaningful decimal places (ignore trailing zeros)
            string decimalPart = priceString.Substring(decimalIndex + 1);
            int meaningfulDecimals = decimalPart.TrimEnd('0').Length;
            
            // For trading, ensure minimum practical precision
            // ES: 6000.25 → 2 decimals
            // 6E: 1.16335 → 5 decimals
            // Bonds: 132.125 → 3 decimals
            return Math.Max(meaningfulDecimals, 2); // Minimum 2 decimals for most futures
        }
        
        public void Dispose()
        {
            // Idempotent: guard against concurrent/double dispose.
            if (Interlocked.Exchange(ref _disposeGuard, 1) != 0)
                return;
            _disposed = true;

            // Detach event subscriptions that would otherwise root this engine.
            if (_tradingService != null)
                _tradingService.OrderBookChanged -= OnOrderBookChanged;
            if (_settingsChangedHandler != null)
            {
                _settings.PropertyChanged -= _settingsChangedHandler;
                _settingsChangedHandler = null;
            }

            _statisticsTimer?.Dispose();
            _snapshotTimer?.Dispose();

            _dataSubscription?.Dispose();
            _portfolioSubscription?.Dispose();

            _ladderUpdatesSubject?.Dispose();
            _statisticsUpdatesSubject?.Dispose();
        }
        
        // Align price to the configured tick size grid
        private decimal AlignToTick(decimal price)
        {
            if (_tickSize <= 0) return price;
            try
            {
                var steps = Math.Round(price / _tickSize, MidpointRounding.AwayFromZero);
                return steps * _tickSize;
            }
            catch
            {
                return price;
            }
        }

        // Increase price precision based on observed price decimals (never decrease to prevent UI thrash)
        private void UpdatePricePrecisionFromObservedPrice(decimal rawPrice)
        {
            try
            {
                if (rawPrice <= 0) return;
                int fromTick = GetDecimalPlacesForStep(_tickSize);
                int fromPrice = DetectDecimalPlaces(rawPrice);
                int desired = Math.Max(fromTick, fromPrice);
                desired = Math.Max(0, Math.Min(desired, 6)); // clamp 0..6
                if (desired > _priceDecimalPlaces)
                {
                    _priceDecimalPlaces = desired;
                }
            }
            catch { /* ignore */ }
        }
    }
}