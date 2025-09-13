using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using BookFlow.App.Interfaces;
using BookFlow.Shared.Contracts;

namespace BookFlow.App.Engine
{
    /// <summary>
    /// High-performance DOM engine that processes market data for a single instrument.
    /// Implements the direct connection architecture with local message filtering,
    /// double-buffering for thread-safe reads, and intelligent conflation for UI updates.
    /// </summary>
    public class DomEngine : IDomEngine
    {
        private readonly object _syncLock = new object();
        private readonly SortedDictionary<decimal, PriceLevel> _bidBook = new();
        private readonly SortedDictionary<decimal, PriceLevel> _askBook = new();
        
        // Double-buffering for thread-safe snapshots
        private volatile BookSnapshot _readerSnapshot;
        private volatile BookSnapshot _writerSnapshot;
        private readonly ReaderWriterLockSlim _snapshotLock = new();
        
        // Reactive streams
        private readonly Subject<LadderUpdate> _ladderUpdatesSubject = new();
        private readonly Subject<StatisticsUpdate> _statisticsUpdatesSubject = new();
        
        // Market state
        private decimal? _bestBid;
        private decimal? _bestAsk;
        private decimal? _lastTradedPrice;
        private long _lastTradedVolume;
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
        
        // Price scale guard to protect against half/double price ladders
        private decimal? _priceScaleBaseline = null; // baseline raw price observed
        private decimal _priceScaleCorrection = 1m;  // 1=normal, 2=double raw, 0.5=half raw
        private const decimal ScaleTolerance = 0.02m; // 2% tolerance
        private int _scaleHalfHits = 0;
        private int _scaleDoubleHits = 0;
        private const int ScaleConfirmThreshold = 5; // require a few hits before switching

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
            
            // Initialize empty snapshots
            _readerSnapshot = CreateEmptySnapshot();
            _writerSnapshot = CreateEmptySnapshot();
            
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
                
                _dataSubscription = _dataFeed.MarketDataStream
                    .Where(msg => msg.TickerId == TickerId)
                    .Subscribe(ProcessMessage, 
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
            
            _dataSubscription?.Dispose();
            _portfolioSubscription?.Dispose();
            
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
            // Dynamically detect and update price precision from observed prices
            UpdatePricePrecisionFromObservedPrice(rawPrice);
            // Do not apply auto scale correction; use raw prices aligned to tick size
            var price = AlignToTick(rawPrice);
            var volume = message.Volume;
            
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
                        
                        // Mark that actual market data changed
                        Interlocked.Increment(ref _lastMarketDataChangeSequence);
                    }
                    else
                    {
                        book.Remove(price);
                        
                        // Mark that actual market data changed
                        Interlocked.Increment(ref _lastMarketDataChangeSequence);
                    }
                    break;
                    
                case L2Operation.Remove:
                    book.Remove(price);
                    
                    // Mark that actual market data changed
                    Interlocked.Increment(ref _lastMarketDataChangeSequence);
                    break;
            }
            
            // Update best bid/ask from book
            UpdateBestPricesFromBook();
        }

        private void UpdateBestBid(decimal price, long volume)
        {
            if (volume > 0)
            {
                // Validate that new bid doesn't create crossed market
                if (_bestAsk > 0 && price >= _bestAsk)
                {
                    if (_debugLoggingEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DomEngine] REJECTING CROSSED BID: {price:F2} >= Ask {_bestAsk:F2}");
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
                _bestBid = _bidBook.Keys.LastOrDefault();
                
                // After removing bid, validate integrity with current ask
                if (_bestBid > 0 && _bestAsk > 0 && _bestBid >= _bestAsk)
                {
                    // Find next valid bid
                    _bestBid = _bidBook.Keys.Where(p => p < _bestAsk).LastOrDefault();
                    
                    if (_debugLoggingEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DomEngine] CORRECTED BID after removal: {_bestBid:F2}");
                    }
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
                if (_bestBid > 0 && price <= _bestBid)
                {
                    if (_debugLoggingEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DomEngine] REJECTING CROSSED ASK: {price:F2} <= Bid {_bestBid:F2}");
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
                _bestAsk = _askBook.Keys.FirstOrDefault();
                
                // After removing ask, validate integrity with current bid
                if (_bestBid > 0 && _bestAsk > 0 && _bestBid >= _bestAsk)
                {
                    // Find next valid ask
                    _bestAsk = _askBook.Keys.Where(p => p > _bestBid).FirstOrDefault();
                    
                    if (_debugLoggingEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DomEngine] CORRECTED ASK after removal: {_bestAsk:F2}");
                    }
                }
            }
            
            // Mark that actual market data changed
            Interlocked.Increment(ref _lastMarketDataChangeSequence);
        }
        
        private void UpdateLastTrade(decimal price, long volume)
        {
            // If this is the first trade, detect decimal precision and enable publishing
            if (!_hasReceivedFirstTrade)
            {
                // Respect display precision already set from tick-size
                _hasReceivedFirstTrade = true;
                CenterDom();
            }
            
            _lastTradedPrice = price;
            _lastTradedVolume = volume;
            
            // Mark that actual market data changed
            Interlocked.Increment(ref _lastMarketDataChangeSequence);
            
            // Record directional trade data for DOM display
            // Determine if trade hit bid or ask based on price relative to best bid/ask
            bool hitBid = price <= _bestBid; // Market sell hitting bid
            
            // Update the price level's trade history
            var allBooks = new Dictionary<decimal, PriceLevel>();
            foreach (var kvp in _bidBook) allBooks[kvp.Key] = kvp.Value;
            foreach (var kvp in _askBook) allBooks[kvp.Key] = kvp.Value;
            
            if (allBooks.ContainsKey(price))
            {
                var level = allBooks[price];
                level.RecordTrade(volume, hitBid);
                
                // Update back to the appropriate book
                if (_bidBook.ContainsKey(price))
                    _bidBook[price] = level;
                else if (_askBook.ContainsKey(price))
                    _askBook[price] = level;
            }
        }
        
        private void UpdateBestPricesFromBook()
        {
            var candidateBestBid = _bidBook.Keys.LastOrDefault();
            var candidateBestAsk = _askBook.Keys.FirstOrDefault();
            
            // Validate market integrity - best bid must be lower than best ask
            if (candidateBestBid > 0 && candidateBestAsk > 0 && candidateBestBid >= candidateBestAsk)
            {
                // Crossed market detected - log warning and correct
                if (_debugLoggingEnabled)
                {
                    System.Diagnostics.Debug.WriteLine($"[DomEngine] CROSSED MARKET DETECTED: Bid={candidateBestBid:F2} >= Ask={candidateBestAsk:F2}");
                }
                
                // Find the highest valid bid that's lower than the lowest ask
                var validBid = _bidBook.Keys.Where(price => price < candidateBestAsk).LastOrDefault();
                
                // Find the lowest valid ask that's higher than the highest bid
                var validAsk = _askBook.Keys.Where(price => price > candidateBestBid).FirstOrDefault();
                
                // Use the corrected values, ensuring minimum 1 tick spread
                if (validBid > 0 && validAsk > validBid + _tickSize)
                {
                    _bestBid = validBid;
                    _bestAsk = validAsk;
                    
                    if (_debugLoggingEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DomEngine] CORRECTED to: Bid={_bestBid:F2} Ask={_bestAsk:F2} (Spread={_bestAsk - _bestBid:F2})");
                    }
                }
                else
                {
                    // If we can't find valid prices with proper spread, use one side only
                    if (validBid > 0)
                    {
                        _bestBid = validBid;
                        _bestAsk = 0; // Clear invalid ask
                    }
                    else if (validAsk > 0)
                    {
                        _bestAsk = validAsk;
                        _bestBid = 0; // Clear invalid bid
                    }
                    else
                    {
                        // Clear both if no valid combination exists
                        _bestBid = 0;
                        _bestAsk = 0;
                    }
                    
                    if (_debugLoggingEnabled)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DomEngine] SINGLE SIDE ONLY: Bid={_bestBid:F2} Ask={_bestAsk:F2}");
                    }
                }
            }
            else
            {
                // Normal case - no crossed market
                _bestBid = candidateBestBid;
                _bestAsk = candidateBestAsk;
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
                
            try
            {
                Dictionary<decimal, PriceLevel> bidBookSnapshot;
                Dictionary<decimal, PriceLevel> askBookSnapshot;
                decimal? centerPrice;
                
                // First, take snapshots under the processing lock to avoid enumeration issues
                lock (_syncLock)
                {
                    centerPrice = _bestBid ?? _bestAsk ?? _lastTradedPrice;
                    bidBookSnapshot = _bidBook.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    askBookSnapshot = _askBook.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                }
                
                // Now we can safely process the snapshots without locks
                _snapshotLock.EnterWriteLock();
                
                // Calculate price range around current market (limit to reasonable range)
                var maxLevelsPerSide = Math.Max(_visibleLevelsAbove, _visibleLevelsBelow) + 50; // Extra buffer beyond visible
                
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
                
                _writerSnapshot = new BookSnapshot(
                    combinedBook,
                    InstrumentName,
                    TickerId,
                    _tickSize,
                    _bestBid,
                    _bestAsk,
                    _lastTradedPrice,
                    _lastTradedVolume);
                
                // Swap buffers
                var temp = _readerSnapshot;
                _readerSnapshot = _writerSnapshot;
                _writerSnapshot = temp;
            }
            finally
            {
                _snapshotLock.ExitWriteLock();
            }
            
            // Publish ladder update
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
            _snapshotLock.EnterReadLock();
            try
            {
                return _readerSnapshot;
            }
            finally
            {
                _snapshotLock.ExitReadLock();
            }
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
                
                // Clear snapshots
                _readerSnapshot = CreateEmptySnapshot();
                _writerSnapshot = CreateEmptySnapshot();

                // Reset any scale auto-detection state
                _priceScaleCorrection = 1m;
                _priceScaleBaseline = null;
                _scaleHalfHits = 0;
                _scaleDoubleHits = 0;
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
            if (_debugLoggingEnabled)
            {
                System.Diagnostics.Debug.WriteLine($"[DomEngine:{InstrumentName}] {message}: {ex.Message}");
            }
            // In a production system, this would use a proper logging framework
        }
        
        private void OnCompleted(string message)
        {
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
            
            // Subscribe to settings changes
            _settings.PropertyChanged += (sender, args) =>
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
            if (_disposed)
                return;
                
            _disposed = true;
            
            _statisticsTimer?.Dispose();
            _snapshotTimer?.Dispose();
            
            _dataSubscription?.Dispose();
            _portfolioSubscription?.Dispose();
            
            _ladderUpdatesSubject?.Dispose();
            _statisticsUpdatesSubject?.Dispose();
            
            _snapshotLock?.Dispose();
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