# BookFlow5 Analysis Report

## Executive Summary
This document provides a comprehensive analysis of the BookFlow5 trading system architecture and implementation. The system consists of multiple components including a WPF client, .NET Framework 4.8 data engine, and shared library for inter-process communication.

---

## ⏱ Resolution Status (current reality — added post-implementation)

This report is a point-in-time audit (Rounds 1–3). Many findings have since been fixed. Verified against the current code:

**✅ Resolved**
- *SharedRingBuffer race / barriers* — the ring is now true **SPSC** (one AddOn drain thread writes, one client thread reads), so CAS is unnecessary; barriers fence the struct write before the pointer publish.
- *Missing Rx subscription cleanup (DomViewModel)* — subscriptions are stored and disposed; `Dispose` also detaches `OrderBookChanged`/`ConnectionStatusChanged`.
- *BuildFullLadder O(N) `Clear()`+`Add()` churn* — replaced by `RangeObservableCollection.ReplaceRange` (single `Reset`).
- *`DefaultPointValue = 50m` hardcoded* — removed; PnL uses `_domEngine.PointValue` (`DomViewModel.cs:78`).
- *Missing tests* — xUnit project added, **47 tests** green.
- *NEW P0-1 `OrderBookChanged` leak*, *P0-3 settings handler leak*, *P0-4 non-idempotent Dispose* — all fixed (`Dispose` detaches handlers; `Interlocked.Exchange` guard).
- *NEW P1-1 double-buffer not lock-free* — replaced with atomic-swap `volatile` snapshot; readers lock-free.
- *NEW P1-2 `UpdateLastTrade` Dictionary alloc + O(N) scan* — now O(1) align + O(log N) lookup, books mutated independently (also fixes the P0 locked-market corruption).
- *NEW P1-4 silent `OnError`* / *P3-7 `OnCompleted`* — now persisted via `BookFlowLog` regardless of the debug flag.
- *NEW P2-2 `EnterWriteLock` outside try* — moot; `ReaderWriterLockSlim` removed.
- *NEW P3-3 dead price-scale detection* — removed.
- *DotNet Reactor* — obfuscation removed from the build entirely, so the silent-skip concern is moot.

**⚠️ Partial**
- *SortedDictionary `Keys.Last()/First()`* — gone from the trade path, still used to derive best bid/ask in `UpdateBestPricesFromBook`.
- *NEW P0-2 `StopAsync` leaves timers running* — `Dispose` stops them, but `StopAsync` alone does not.
- *Allocation churn* — per-trade dict gone; `DetectDecimalPlaces` strings and the per-message latency queue remain.

**◻️ Still open / by-design (low priority)**
- Rx `Buffer` conflation over a ~100 ms publisher (P1-7 / NEW P1-5); `_fixedCenterPrice` cross-thread access (NEW P1-6); snapshot fires every 100 ms regardless of change + unimplemented `_lastPublishedSequence` skip (NEW P2-1 / P3-4); `LadderUpdate` list allocations (NEW P2-6); `book.Keys.ToList()` in `UpdateOrderCountsInBook` (NEW P2-5); magic `order.Side` ints and other magic numbers (NEW P3-2/6/9); empty `catch` in `OnOrderBookChanged`/`AlignToTick` (NEW P3-5/8); `ProcessPortfolioUpdate` stub + its subscription (NEW P3-10); `PriceLevelViewModel` dead class (P3).

> Also note: §3 "IPC Channel Issues" references three channels with a slow **TCP** path — that TCP event server (port 38755) and the regex-JSON `ControlPipe` have both been **removed** in favor of a WCF duplex `netNamedPipe` service (see `ARCHITECTURE.md` / `NT8DataEngine.md`).

---

## Key Technical Issues

### 1. Structural Concerns
- **Mixed Framework Approach**: The solution uses .NET 9.0 for the WPF client and .NET Framework 4.8 for the data engine, creating architectural complexity
- **God Classes**: DomEngine.cs contains over 1187 lines with multiple responsibilities
- **Documentation Drift**: ARCHITECTURE.md is out-of-date with actual implementation

### 2. Implementation Problems
#### Performance Issues
- **SortedDictionary Usage**: Multiple `SortedDictionary.Keys.Last()` and `First()` calls cause O(N) performance degradation
- **Memory Allocation**: Dictionary merging operations cause unnecessary allocations
- **Rx Conflation Mismatch**: 16ms buffer vs 100ms stream creates ineffective conflation

#### Synchronization Problems
- **Race Conditions**: SharedRingBuffer lacks proper volatile reads for head/tail positions
- **Memory Barriers**: Missing proper synchronization primitives in IPC components
- **Thread Safety**: Potential issues in NT8DataEngine components

#### Data Structure Concerns
- **Struct Layout**: PriceLevel structs require precise memory alignment and marshaling
- **Mutation Patterns**: Value-type struct mutations may cause stale copy issues
- **Dead Code**: Unused decimal place detection methods remain in codebase

### 3. IPC Channel Issues
- **Performance Variability**: Three IPC channels with TCP being slowest for critical updates
- **Channel Design**: TCP event channel design issues in both DomEngine and NT8DataEngine
- **Failure Handling**: Silent failures across IPC boundaries

### 4. Deployment Challenges
- **Hardcoded Paths**: NinjaTrader installation paths create deployment fragility
- **Framework Dependencies**: Mixed .NET Framework versions complicate deployment

## Recommendations
1. Replace SortedDictionary with more efficient data structures for book management
2. Implement proper volatile reads/writes in SharedRingBuffer
3. Eliminate dead code and streamline DomEngine class
4. Refactor large classes into more manageable components
5. Address documentation discrepancies
6. Optimize IPC channel selection for performance-critical operations

---

## Detailed Actionable Findings (Round 2)

### P0 — Critical: Race Condition in SharedRingBuffer
**File:** `SharedLibrary.Standard\IPC\SharedRingBuffer.cs:32-40`

**Issue:** `TryWrite` reads head (line 32) and tail (line 33) separately, then writes without atomic compare-and-swap. Under concurrent producers or consumers this can produce lost writes or corrupted ring state.

**Impact:** Silent data loss during peak throughput.

**Fix:** Use `Interlocked.CompareExchange` at offset 0 for head pointer:
```csharp
long nextHead = (head + 1) % _capacity;
if (nextHead == tail) return false;
// ... write message ...
int result = Interlocked.CompareExchange(ref head, nextHead, head);
if (result != head) return false; // another writer took the slot
```
Same pattern for `TryRead` on the tail.

---

### P0 — Critical: Missing Rx Subscription Cleanup  
**File:** `BookFlowApp\ViewModels\DomViewModel.cs:94`

**Issue:** The `Dispose` method (line 1034) stops the timer and disposes `_domEngine`, but never disposes the `_domEngine.LadderUpdates.Subscribe(...)` IDisposables. With short-lived DOM windows, each uncleaned subscription keeps its closure (the ViewModel) rooted in memory.

**Impact:** Memory leak — closed DOM windows never GC until `_domEngine` is disposed.

**Fix:**
```csharp
private readonly IDisposable _ladderSubscription;

// In constructor:
_ladderSubscription = _domEngine.LadderUpdates.Subscribe(OnLadderUpdate);

// In Dispose:
_ladderSubscription?.Dispose();
```

---

### P0 — Critical: DomRowData generates 30+ OnPropertyChanged events per tick
**File:** `BookFlowApp\Models\DomRowData.cs:653-733`

**Issue:** `UpdateFromPriceLevel` sets 20+ properties sequentially. Each setter fires `OnPropertyChanged`, driving WPF property binding evaluation. On a 15-column grid under high-frequency data, this floods the Dispatcher queue causing UI lag and dropped frames.

**Impact:** 16ms timer (line 100 DomViewModel.cs) cannot keep up — UI stutters during market opens/auctions.

**Fix:** Batch property changes with `INotifyCollectionChanged`-style deferral:
```csharp
public void UpdateBatch(Action<DomRowData> mutator)
{
    mutator(this);
    // Fire a single "all properties" notification, or only the ones that changed
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); // null = notify all
}
```
Alternatively, use `Binding.DoNothing` and batch via `CollectionView.DeferRefresh()`.

---

### P1 — High: BuildFullLadder allocates O(N) per full update
**File:** `BookFlowApp\ViewModels\DomViewModel.cs:459-539`

**Issue:** The method creates `existingRowsByPrice` Dictionary (line 472), then `newRows` list, then iterates `DomRows.Clear()` + re-adds every row. Each WPF ObservableCollection Add/Clear fires CollectionChanged events individually. For a 1000-level ladder this is ~2000 event fires per update.

**Impact:** Excessive WPF layout passes. CPU spikes during book rebuilds.

**Fix:** Use a single `DeferRefresh` or replace the backing store entirely:
```csharp
using (var scope = CollectionViewSource.GetDefaultView(DomRows)?.DeferRefresh())
{
    // or create a fresh ObservableCollection and swap reference
    var newItem = new ObservableCollection<DomRowData>(newRows);
    FieldInfo fi = typeof(ObservableCollection<DomRowData>).GetField("_items", ...);
    fi.SetValue(DomRows, newItem._items); // avoid events
}
```
Or implement an `ISupportIncrementalLoading` wrapper.

---

### P1 — High: NonThreadSafe `Interlocked` gap in head/tail writes
**File:** `SharedLibrary.Standard\IPC\SharedRingBuffer.cs:17-18`

**Issue:** `HeadPosition` (0) and `TailPosition` (8) are `long` offsets in a memory-mapped view. `ReadInt64`/`WriteInt64` on `MemoryMappedViewAccessor` are **not** guaranteed atomic on all runtimes. The memory barrier on line 38 and 50 is placed **after** the write but **before** the pointer update, which is backwards — the barrier should fence the message write **before** publishing the new head/tail.

**Impact:** Consumers may read partially-written struct.

**Fix:** Reverse barrier order:
```csharp
_accessor.Write(position, ref message);
Thread.MemoryBarrier();        // ensure message fully flushed
_accessor.Write(HeadPosition, nextHead);
```
For read:
```csharp
_accessor.Read(position, out message);
long nextTail = (tail + 1) % _capacity;
Thread.MemoryBarrier();
_accessor.Write(TailPosition, nextTail);
```

---

### P1 — High: String interning churn in UpdateOrderAnnotations  
**File:** `BookFlowApp\ViewModels\DomViewModel.cs:666-701`

**Issue:** `string.Join` with `Select(o => $"{o.Quantity - o.FilledQuantity}/...")` runs per row per annotation update (every `OrderBookChanged` event). For 1000 rows this is 1000 string allocations + 1000 property change events.

**Impact:** GC pressure under high-frequency order flow.

**Fix:** Pre-allocate `StringBuilder` per row, or only update rows where orders changed:
```csharp
var changedRows = new HashSet<decimal>();
foreach (var o in changedOrders) changedRows.Add((decimal)o.Price);
foreach (var row in DomRows)
{
    if (!changedRows.Contains(row.Price)) continue;
    // ... update only matching rows
}
```

---

### P2 — Medium: Random-based order ID generation  
**File:** `BookFlowApp\ViewModels\DomViewModel.cs:1017-1022`

**Issue:** `new Random()` seeded with wall clock can produce duplicate IDs under rapid order submission. Additionally, `DateTime.UtcNow.ToString("HHmmss")` collides within the same second.

**Impact:** Duplicate order IDs may confuse NT8 order matching.

**Fix:**
```csharp
private static readonly Guid _orderIdPrefix = Guid.NewGuid();
private static int _sequence = 0;

private string GenerateOrderId() =>
    $"BF-{_instrumentName}-{Interlocked.Increment(ref _sequence):D6}-{_orderIdPrefix:N}";
```

---

### P2 — Medium: GenerateOrderId re-instantiates Random per call
**File:** `BookFlowApp\ViewModels\DomViewModel.cs:1018`

**Issue:** `new Random()` uses `Environment.TickCount` seed — if two orders fire within the same tick, both get the same random value.

**Fix:** Use `Random.Shared` (.NET 6+) or a static instance (see P2 above).

---

### P2 — Medium: `DefaultPointValue` hardcoded as 50m  
**File:** `BookFlowApp\ViewModels\DomViewModel.cs:65`

**Issue:** The comment says `TODO: pull from instrument metadata` but `DefaultPointValue = 50m` is used for all PnL calculations (lines 131, 199, 725). For instruments with different contract specs (ES, NQ, CL, etc.), PnL will be wrong.

**Impact:** Incorrect real-time PnL display across instruments.

**Fix:** Pass `pointValue` from `IDomEngine` or instrument settings at construction time.

---

### P2 — Medium: Missing unit tests and integration tests  
**Files:** Entire solution

**Issue:** No `*.Test` project, no xUnit/NUnit/ MSTest references. The `SharedRingBuffer` race condition and `DomEngine` conflation logic cannot be verified automatically.

**Impact:** All changes rely on manual QA. Regression risk is high.

**Fix:** Add an xUnit test project targeting `SharedLibrary.Standard`:
- `SharedRingBuffer_ProduceConsume_NoLoss` — N producers, M consumers, verify all messages delivered
- `SharedRingBuffer_WriteWhenFull_ReturnsFalse` — capacity boundary test
- `DomEngine_ConflateMultipleUpdates_ReducesToSingleLadderUpdate` — conflation behavior

---

### P2 — Medium: `domrowdata.cs` exposes 40+ mutable properties  
**File:** `BookFlowApp\Models\DomRowData.cs:1-771`

**Issue:** 40+ backing fields + setters, many of which are never bound to any XAML column (e.g., `_observations`, `_reserve`, `_bidOrdersFullText`, `_askOrdersFullText`). Each unnecessary property adds memory pressure and potential PropertyChanged noise.

**Impact:** 771-line model class, ~30% dead properties.

**Fix:** Audit against `DomGridWindow.xaml` column bindings, remove unused properties. Keep a slim 10-12 property public surface.

---

### P3 — Low: `PriceLevelViewModel` is empty
**File:** `BookFlowApp\ViewModels\DomViewModel.cs:1045-1051`

**Issue:** `PriceLevelViewModel` inherits `INotifyPropertyChanged` but has zero properties and is never instantiated in the codebase.

**Fix:** Remove dead class or implement if intended for future use.

---

### P3 — Low: `DotNet Reactor` conditional import with no fallback
**Files:** `BookFlowApp\BookFlow.App.csproj`, `NT8DataEngine\BookFlow.NT8DataEngine.csproj`

**Issue:** `dotnet_reactor.targets` is imported conditionally:
```xml
<Import Project="$(MSBuildThisFileDirectory)..\Dependencies\dotnet_reactor.targets" Condition="Exists(...)"/>
```
If `dotnet_reactor` fails to run (missing exe, wrong path), the build silently succeeds without obfuscation. This gives a false sense of protection.

**Fix:** Add a `<Target>` verification step to emit a warning when obfuscation is skipped:
```xml
<Target Name="VerifyDotNetReactor" BeforeTargets="CoreCompile" Condition="!Exists('..\Dependencies\dotnet_reactor.targets')">
  <Warning Text="DotNet Reactor targets not found — build will skip obfuscation." />
</Target>
```

---

### P3 — Low: `DomViewModel` Dispose does not unsubscribe handlers
**File:** `BookFlowApp\ViewModels\DomViewModel.cs:1033-1042`

**Issue:** `_domEngine.ConnectionStatusChanged += OnConnectionStatusChanged` (line 95) and `_tradingService.OrderBookChanged` (line 87) are never unsubscribed in `Dispose`. If `_domEngine` is shared across viewmodels, lingering handlers cause phantom callbacks.

**Impact:** Null reference exceptions or stale UI updates after ViewModel disposal.

**Fix:**
```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;

    _uiUpdateTimer?.Stop();
    _domEngine?.ConnectionStatusChanged -= OnConnectionStatusChanged;
    _ladderSubscription?.Dispose();
    _tradingService?.OrderBookChanged -= OnOrderBookChanged;
    _tradingService?.PortfolioChanged -= OnPortfolioChanged;
    _domEngine?.Dispose();
}
```

---

### P3 — Low: `DomRowData.FromPriceLevel` creates string allocations per row
**File:** `BookFlowApp\Models\DomRowData.cs:647-648`

**Issue:** `"${priceLevel.BidCount} orders at ${priceLevel.Price:F2}"` runs every time a new `DomRowData` is created or updated. Under high frequency this allocates strings which are never used by the grid (the actual columns bind to `BidOrdersInfo` and `AskOrdersInfo`).

**Fix:** Remove string interpolation from `FromPriceLevel`. Only format when bound, using IValueConverter.

---

## Delta Analysis: Round 3 (Deep Implementation Review)

### Agreement Assessment with Previous Rounds

**AGREE — Confirmed by full code review:**
- P0 SharedRingBuffer race condition (CAS needed) — confirmed; read/write lack atomic compare-exchange
- P0 Missing Rx Subscription Cleanup in DomViewModel — confirmed; `_domEngine.LadderUpdates.Subscribe(...)` at L94 returns disposable that is never stored
- P0 DomRowData 30+ OnPropertyChanged per tick — confirmed structurally
- P1 BuildFullLadder O(N) allocation — confirmed; `DomRows.Clear()` + per-row add fires ~2000 events
- P1 SharedRingBuffer memory barrier — **partially corrected** (see Disagree section below)
- P1 String interning churn — confirmed in `UpdateOrderAnnotations`
- P2 Random-based order ID — confirmed; `new Random()` per invocation
- P2 DefaultPointValue hardcoded — confirmed `50m` used globally
- P2 Missing unit tests — confirmed; zero test projects
- P2 DomRowData 40+ mutable properties — confirmed; ~30% dead
- P3 PriceLevelViewModel empty, DomViewModel Dispose gaps, DotNet Reactor — all confirmed

**DISAGREE — Corrections from full code review:**

1. **SharedRingBuffer write-side barrier is correct, not backwards (Round 2 P1)**
   - Round 2 claimed: "the barrier should fence the message write **before** publishing the new head/tail" — this is actually what the code does correctly in `TryWrite` (line 37-39: write message, barrier, write head). The barrier ordering is correct for the write path.
   - **However**, `TryRead` (line 49-52) has the barrier **after** the struct read but **before** the tail update, which is correct for tail advancement (a write operation). The real issue is the lack of atomicity (CAS), not barrier ordering.
   - **Verdict:** The barrier-ordering claim was overstated. The CAS fix (already in P0) is sufficient.

2. **SortedDictionary.Keys.Last() claim: O(log N), not O(N) (Round 1)**
   - `SortedDictionary.Keys.Last()` internally navigates the RB tree to the rightmost node — O(log N), not O(N). The enumerator allocation is the real cost, not asymptotic complexity.
   - **Verdict:** Severity was overstated; the allocation is the issue, not the algorithm.

### NEW Findings (Not in Previous Rounds)

The following 20 issues were identified in the Round 3 deep implementation review across `DomEngine.cs`, `DomDataContracts.cs`, and `BookSnapshot`:

---

#### NEW P0-1: `DomEngine` — `_tradingService.OrderBookChanged` never unsubscribed
**File:** `BookFlowApp\Engine\DomEngine.cs:162-163`

**Issue:** Subscribed in `StartAsync` but never unsubscribed in `StopAsync`, `ClearAllData`, or `Dispose`. Keeps `DomEngine` rooted through `TradingService` event chain, preventing GC on instrument rotation.

**Impact:** Memory leak — each instrument switch leaks a `DomEngine` instance.

**Fix:**
```csharp
// In StopAsync or Dispose:
_tradingService?.OrderBookChanged -= OnOrderBookChanged;
```

---

#### NEW P0-2: `DomEngine` — `StopAsync` doesn't stop `_snapshotTimer` or `_statisticsTimer`
**File:** `BookFlowApp\Engine\DomEngine.cs:179-188`

**Issue:** After `StopAsync`, both `System.Threading.Timer` instances continue firing at 100ms/1000ms intervals. `UpdateSnapshot` checks `_disposed` but not `_isConnected`, silently doing nothing while consuming CPU. `PublishLadderUpdate` at L601 still publishes to `_ladderUpdatesSubject` after data feed is stopped.

**Impact:** Wasted CPU cycles, spurious ladder updates after stop, potential access to stale/incomplete book state.

**Fix:** Add `_snapshotTimer?.Change(Timeout.Infinite, Timeout.Infinite);` and `_statisticsTimer?.Change(Timeout.Infinite, Timeout.Infinite);` in `StopAsync`, or null the timer references.

---

#### NEW P0-3: `DomEngine` — `_settings.PropertyChanged` handler never unsubscribed
**File:** `BookFlowApp\Engine\DomEngine.cs:1077-1092`

**Issue:** The lambda subscribed in `UpdateConfigurationFromSettings` captures `this`. `DomSettings` will outlive `DomEngine`, preventing GC.

**Impact:** Same as P0-1 — `DomEngine` instance never released.

**Fix:** Store the handler as a field; unsubscribe in `Dispose`.

---

#### NEW P0-4: `DomEngine` — `Dispose` not idempotent — no double-dispose guard
**File:** `BookFlowApp\Engine\DomEngine.cs:1135-1152`

**Issue:** `_disposed` is `volatile` but assignment at L1140 is not atomic with respect to concurrent calls. Two threads calling `Dispose` can both pass the L1137 check before either sets L1140. `Timer.Dispose` is typically safe to call twice, but `_snapshotLock.Dispose()` after the lock is already disposed will throw `ObjectDisposedException`.

**Impact:** Potential `ObjectDisposedException` crash during shutdown.

**Fix:**
```csharp
private int _disposedInt = 0;
// In Dispose:
if (Interlocked.Exchange(ref _disposedInt, 1) != 0) return;
_disposed = true;
```

---

#### NEW P1-1: `DomEngine` — Double-buffer pattern is not lock-free
**File:** `BookFlowApp\Engine\DomEngine.cs:27-29, 519, 821`

**Issue:** The comment says "double-buffering for thread-safe reads" but `ReaderWriterLockSlim` serializes all readers against writers. A UI thread calling `GetBookSnapshot()` acquires `EnterReadLock()` while the data thread holds `EnterWriteLock()` during `UpdateSnapshot`. This adds write-contention latency: the data thread must wait for all UI read lock holders to release before swapping snapshots.

**Impact:** Lock contention between data thread and UI polling thread. Under sustained throughput, the UI thread's read locks can starve the data thread.

**Fix:** True double-buffering: two snapshot buffers + atomic index.
```csharp
private BookSnapshot[] _buffers = new BookSnapshot[2];
private volatile int _currentIndex = 0;
private volatile int _readIndex => _currentIndex;

// Writer swaps:
_currentIndex = 1 - _currentIndex;

// Reader reads:
var snapshot = _buffers[_readIndex];
```

---

#### NEW P1-2: `DomEngine` — `UpdateLastTrade` allocates new `Dictionary` per trade + O(N) scan
**File:** `BookFlowApp\Engine\DomEngine.cs:431-448`

**Issue:** On every trade message, merges both `_bidBook` and `_askBook` into a new `Dictionary`, then linear-scans all keys to find the nearest price. At 10K trades/sec and a 50-level book: ~500K dict objects/sec + 500K linear scans.

**Impact:** Massive GC pressure. At trade-volume peaks, GC can cause data processing stalls >5ms.

**Fix:** Search `_bidBook` then `_askBook` directly with `SortedDictionary` O(log N) lookup:
```csharp
decimal? nearestKey = null;
if (_bidBook.ContainsKey(price)) nearestKey = price;
else
{
    var bidCandidate = _bidBook.Keys.Where(k => k <= price).LastOrDefault();
    var askCandidate = _askBook.Keys.Where(k => k >= price).FirstOrDefault();
    // pick closer
}
```

---

#### NEW P1-3: `DomEngine` — `DetectDecimalPlaces` allocates strings per message
**File:** `BookFlowApp\Engine\DomEngine.cs:1112-1133`

**Issue:** `price.ToString("G29")`, `IndexOf`, `Substring`, `TrimEnd` all run on every L1/L2 message. At 100K msg/s = 500K+ string allocations/sec per instrument.

**Impact:** GC pressure and CPU waste in the hot path.

**Fix:** Arithmetic computation — repeatedly multiply by 10 until integer, counting iterations. Or cache result per instrument after first detection.

---

#### NEW P1-4: `DomEngine` — `OnError` silently swallows exceptions when `_debugLoggingEnabled = false`
**File:** `BookFlowApp\Engine\DomEngine.cs:1041-1048`

**Issue:** With `_debugLoggingEnabled = false` (the production default), all errors from the Rx pipeline, message processing, and portfolio callbacks are silently discarded. In a trading application, a silent failure in message processing means stale market data on screen, which can cause mis-trading.

**Impact:** Silent market data corruption. Developer has no visibility into failures.

**Fix:** Use `ILogger` dependency (e.g., Serilog) or at minimum `Trace.WriteLine` in the production path:
```csharp
private void OnError(string message, Exception ex)
{
    if (_debugLoggingEnabled)
        System.Diagnostics.Debug.WriteLine($"[DomEngine:{InstrumentName}] {message}: {ex.Message}");
    // Production logging:
    System.Diagnostics.Trace.WriteLine($"[DomEngine ERROR:{InstrumentName}] {message}: {ex.Message}");
}
```

---

#### NEW P1-5: `DomEngine` — `Rx Subject.Buffer` unbounded — no backpressure
**File:** `BookFlowApp\Engine\DomEngine.cs:117-121`

**Issue:** `_ladderUpdatesSubject.Buffer(_conflationInterval)` buffers into a time window. If `PublishLadderUpdate()` fires faster than the window emits (e.g., during market open with rapid order book changes), messages accumulate in the `Subject` buffer with no upper bound. `Subject` has no backpressure — infinite buffer until OOM.

**Impact:** Memory spike risk during extreme market events.

**Fix:** Use `Buffer(int count)` with a maximum, or apply `Sample()` instead of `Buffer()` for latest-state semantic:
```csharp
_ladderUpdatesSubject
    .Sample(TimeSpan.FromMilliseconds(16))
    .ObserveOn(SynchronizationContext.Current)
```

---

#### NEW P1-6: `DomEngine` — `_fixedCenterPrice` race condition
**File:** `BookFlowApp\Engine\DomEngine.cs:658-685, 961-1018`

**Issue:** `_fixedCenterPrice` is read and written from both the snapshot timer thread (`PublishLadderUpdate`) and user-initiated calls (`CenterDom`, `ForceUpdate`). The read-modify-write cycle at L659-667 (`if (!_fixedCenterPrice.HasValue) { ... _fixedCenterPrice = ... }`) is not synchronized. Two concurrent calls can race.

**Impact:** Visual artifact — ladder center jumps erratically during rapid recentering.

**Fix:** Guard under `_syncLock` or use `Interlocked.CompareExchange` on a wrapper struct.

---

#### NEW P2-1: `DomEngine` — `SnapShot` copies both books every 100ms regardless of activity
**File:** `BookFlowApp\Engine\DomEngine.cs:511-516`

**Issue:** `_snapshotTimer` fires at fixed 100ms intervals, and `UpdateSnapshot` always copies both `SortedDictionary` instances under `lock(_syncLock)`. During quiet periods (no market data), this is wasted work.

**Impact:** Unnecessary CPU and allocations.

**Fix:** Only trigger snapshot when data has changed:
```csharp
private void UpdateSnapshot(object? state)
{
    var current = Volatile.Read(ref _lastMarketDataChangeSequence);
    if (current == _lastPublishedSequence) return;
    // ... rest of method
    Volatile.Write(ref _lastPublishedSequence, current);
}
```

---

#### NEW P2-2: `DomEngine` — `_snapshotLock.EnterWriteLock` outside try block
**File:** `BookFlowApp\Engine\DomEngine.cs:504-597`

**Issue:** `EnterWriteLock()` is on L519, but the `try` block starts at L505. If `_snapshotLock` is disposed between L504 (`if (_disposed)`) and L519, `EnterWriteLock` throws and the `finally` at L595 calls `ExitWriteLock()` on a disposed lock, throwing again.

**Impact:** Double exception during disposal race.

**Fix:** Use a boolean flag:
```csharp
bool lockAcquired = false;
try
{
    _snapshotLock.EnterWriteLock();
    lockAcquired = true;
    // ... work
}
finally
{
    if (lockAcquired) _snapshotLock.ExitWriteLock();
}
```

---

#### NEW P2-3: `BookSnapshot` re-iterates to compute aggregates
**File:** `SharedLibrary.Standard\Contracts\DomDataContracts.cs:165-181`

**Issue:** The `BookSnapshot` constructor copies the passed dictionary, then iterates it again to compute `BidDepthLevels`, `AskDepthLevels`, `TotalBidVolume`, `TotalAskVolume`. Combined with the snapshot copies in `UpdateSnapshot` (3 allocations per snapshot), and the `GetVisibleLadder` enumeration, that's 4+ passes per 100ms tick.

**Impact:** CPU waste and GC pressure.

**Fix:** Pass precomputed aggregates as constructor parameters from `UpdateSnapshot`, where the data is already being iterated.

---

#### NEW P2-4: `DomEngine` — `_latencyMeasurements` enqueues `ConcurrentQueue` node per message
**File:** `BookFlowApp\Engine\DomEngine.cs:207-209`

**Issue:** Every single message enqueues to `ConcurrentQueue<long>`. At 100K msg/s = 100K queue node allocations/sec. Each node is a separate heap object (16 bytes payload + 24-byte object header).

**Impact:** GC pressure at high throughput.

**Fix:** Sample at a fixed rate (every 100th message), or use a fixed-size circular buffer with `ArrayPool<long>.Shared`.

---

#### NEW P2-5: `DomEngine` — `UpdateOrderCountsInBook` calls `.ToList()` unnecessarily
**File:** `BookFlowApp\Engine\DomEngine.cs:610`

**Issue:** Allocates a list copy of dictionary keys to avoid modification during enumeration. The method only modifies values (the `PriceLevel` struct), not keys, so enumeration is safe.

**Impact:** Unnecessary allocation every snapshot.

**Fix:** Use `foreach` directly over the dictionary.

---

#### NEW P2-6: `DomEngine` — `LadderUpdate.VisibleLevels` new `List` every publish
**File:** `BookFlowApp\Engine\DomEngine.cs:687-698`

**Issue:** Every conflation window (every 16ms), a new `List<PriceLevel>` is allocated as `visibleLevels`, then boxed into `LadderUpdate`. If subscribers don't keep up, these lists pile up in the Rx buffer.

**Impact:** GC pressure on the publish path.

**Fix:** Object pooling for `LadderUpdate` and its `List<PriceLevel>`, or use a shared array.

---

#### NEW P3-1: `DomEngine` — `CenterMode.OneTime` mutates settings as side-effect
**File:** `BookFlowApp\Engine\DomEngine.cs:678`

**Issue:** `_settings.CenterMode = CenterMode.None;` is a side effect of the publish operation. This triggers `PropertyChanged` on `DomSettings`, which could fire the handler at L1077-1092, recomputing `_visibleLevelsAbove`/`_visibleLevelsBelow` mid-publish.

**Impact:** Unpredictable state change during publish.

**Fix:** Use a local state flag instead of mutating settings.

---

#### NEW P3-2: `DomEngine` — Magic integers for order side
**File:** `BookFlowApp\Engine\DomEngine.cs:634-639`

**Issue:** `order.Side == 1` (Buy) and `order.Side == 2` (Sell) instead of using an `OrderSide` enum.

**Fix:** `order.Side == OrderSide.Buy` / `OrderSide.Sell`.

---

#### NEW P3-3: `DomEngine` — Dead price scale detection code
**File:** `BookFlowApp\Engine\DomEngine.cs:77-83, 246-251`

**Issue:** `_scaleHalfHits`, `_scaleDoubleHits`, `_scaleConfirmThreshold` fields and related logic exist but are never used. L251 says "Do not apply auto scale correction; use raw prices aligned to tick size."

**Impact:** Confusion for future maintainers.

**Fix:** Remove dead code.

---

#### NEW P3-4: `DomEngine` — `_lastMarketDataChangeSequence` incremented but never compared for skip
**File:** `BookFlowApp\Engine\DomEngine.cs:48-49, 709`

**Issue:** `_lastMarketDataChangeSequence` is incremented via `Interlocked.Increment` on data changes (L298, L352, L391, L405). `_lastPublishedSequence` exists but the comparison logic to skip publishing is absent. `PublishLadderUpdate` always runs regardless.

**Impact:** The intended optimization (skipping publish when nothing changed) is unimplemented.

**Fix:** Implement the skip:
```csharp
if (Volatile.Read(ref _lastMarketDataChangeSequence) == _lastPublishedSequence)
    // skip or do lightweight update
```

---

#### NEW P3-5: `DomEngine` — `OnOrderBookChanged` empty catch block
**File:** `BookFlowApp\Engine\DomEngine.cs:491`

**Issue:** `catch { }` swallows all exceptions from `PublishLadderUpdate`, including disposed object exceptions and memory errors. Silent failure in a trading application is dangerous.

**Fix:** Catch and log (useful even when `_debugLoggingEnabled = false`).

---

#### NEW P3-6: `DomEngine` — Magic numbers throughout
**File:** `BookFlowApp\Engine\DomEngine.cs` (throughout)

**Issue:** Hardcoded values with no named constants:

| Line | Value | Meaning |
|------|-------|---------|
| L40 | `0.25m` | Default tick size |
| L41 | `50m` | Default point value |
| L64 | `16` | Conflation interval (ms) |
| L129 | `100` | Snapshot timer interval (ms) |
| L208 | `1000` | Max latency queue size |
| L522 | `50` | Extra buffer levels |
| L568 | `200` | Max combined levels warning |
| L742 | `1000` | Max latencies for stats |
| L759 | `1000000` | Max latency cap (1s in ticks) |
| L1132 | `2` | Min decimal places |
| L1178 | `6` | Max decimal places |

**Fix:** Define named `const` or `readonly` fields.

---

#### NEW P3-7: `DomEngine` — `OnInfo` / `OnCompleted` also silently swallowed
**File:** `BookFlowApp\Engine\DomEngine.cs:1050-1064`

**Issue:** `OnInfo` and `OnCompleted` are guarded by `_debugLoggingEnabled`. With debug off, stream completion events (a critical signal that the data feed disconnected) are silent.

**Impact:** Lost disconnection events. No alert when data stream ends.

**Fix:** Always log `OnCompleted` (data stream end) and `OnError` with at minimum `Trace.WriteLine`.

---

#### NEW P3-8: `DomEngine` — `Empty catch` in `AlignToTick` and `UpdatePricePrecisionFromObservedPrice`
**File:** `BookFlowApp\Engine\DomEngine.cs:1163, 1184`

**Issue:** `catch { return price; }` and `catch { /* ignore */ }` silently absorb `Math.Round` overflows and decimal precision edge cases. A misaligned price would go undetected in production.

**Fix:** Remove empty catches — let the outer `try/catch` in `ProcessMessage` (L217-220) handle them.

---

#### NEW P3-9: `DomEngine` — `order.Side` used without enum
**File:** `BookFlowApp\Engine\DomEngine.cs:634, 638`

**Issue:** `order.Side == 1` and `order.Side == 2` are magic integers. `OrderCommand` already defines `OrderAction` enum; `Side` should similarly be an enum.

**Fix:** `order.Side == OrderSide.Buy` / `OrderSide.Sell`.

---

#### NEW P3-10: `DomEngine` — `ProcessPortfolioUpdate` is a stub
**File:** `BookFlowApp\Engine\DomEngine.cs:494-497`

**Issue:** The method body says "This is now handled by the NT8TradingService" but the subscription at L156-158 still routes every portfolio message through `DomEngine`. This is dead throughput — messages arrive, are routed through `Subscribe`, hit the stub, and return.

**Fix:** Either remove the `_portfolioSubscription` entirely, or if needed in the future, implement the handler.

---

#### NEW P1-7: `DomEngine` — `Empty catch` blocks throughout
**File:** `BookFlowApp\Engine\DomEngine.cs:491, 1163, 1184`

**Issue:** Three empty `catch { }` blocks in the hot path. In a live trading application, silently absorbing exceptions means the UI displays stale or partially-constructed market data without any alert mechanism. Combined with `OnError` being silent (P1-4), the error visibility surface is zero in production.

**Fix:** See individual fixes above. At minimum, route through `OnError` to preserve traceability.

---

#### NEW P2-7: `_settings` field mutations from arbitrary thread (no sync)
**File:** `BookFlowApp\Engine\DomEngine.cs:1077-1092`

**Issue:** The `PropertyChanged` callback on `_settings` fires on whatever thread modifies settings (likely the UI thread). It writes to `_visibleLevelsAbove`, `_visibleLevelsBelow`, `_conflationInterval` without any synchronization. These fields are read inside `lock(_syncLock)` in `UpdateSnapshot` concurrently.

**Impact:** The `lock(_syncLock)` in `UpdateSnapshot` ensures the read is safe under the lock, but the writer (the lambda) has no lock. A torn read of a `int` field is technically safe on .NET (ints are atomic loads on x64), but the semantics are unclear for `_conflationInterval` (a `TimeSpan` struct — 8-byte write, which IS atomic on x64). In practice this works, but it's not guaranteed by the struct fields' declarations.

**Fix:** Mark `_visibleLevelsAbove`, `_visibleLevelsBelow`, `_conflationInterval` as `volatile`, or use a dedicated settings lock.

---

## Summary: Round 3 Delta

| Category | Previous Rounds | Round 3 New | Total Known |
|----------|---------------------------------|-------------|
| P0 (Critical) | 3 | 4 | **7** |
| P1 (High) | 3 | 7 | **10** |
| P2 (Medium) | 4 | 7 | **11** |
| P3 (Low) | 4 | 10 | **14** |
| Architecture | 3 | 2 (God class, Order placement) | **5** |
| **Total** | **17** | **20** | **37** |

**Coverage Note:** `qwen3-coder.md` covered IPC-level issues (SharedRingBuffer, ControlPipe, TCP) well but had minimal visibility into `DomEngine.cs` internals. Round 3 fills the gap with 20 new findings inside the core engine class, covering thread safety, GC pressure, error handling, and disposable resource management.