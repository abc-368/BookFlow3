# BookFlow Enhancement Plan

## 1. Purpose (north star)

These priorities are load-bearing for every design choice below.

1. **Fastest possible data ingestion.** L1/L2 tick path must minimize per-tick CPU, allocations, and serialization. Sub-microsecond hand-off from NT8 to client is achievable and is the bar.
2. **Responsive UI.** Render at a locked frame rate, decoupled from tick rate. Per `ARCHITECTURE.md §5`, pull-based: the engine updates a snapshot at tick speed, the UI polls the snapshot at 60 FPS. No per-tick `OnPropertyChanged` storms.
3. **Consistent portfolio state.** NT8 is the source of truth. Every account/order/position/PnL value displayed must be reconcilable to an NT8 query. Drift is a bug.
4. **Immediate broker-side sync.** Any order/fill/position event in NT8 must reach the WPF client with the lowest possible latency, in-order, never silently dropped, and correlatable to its originating command.

If any decision contradicts these four, it loses.

---

## 2. What we learned from Jigsaw (`C:\Program Files\Jigsaw Trading\`)

Confirmed by inspecting `daytradr64.exe.config`, `JigsawCommunication.dll`, and `JTConnection.dll`:

- **One IPC channel for everything**: WCF `netNamedPipeBinding` at `net.pipe://localhost/daytradr/communicationservice.svc`.
- **Wire format**: **protobuf-net** (`ProtoBuf.ServiceModel.ProtoBehaviorExtension`), 20 MB max message.
- **Bidirectional via WCF duplex**: `DuplexChannelFactory<>` + `IJTCommunicationService` + `IJTCommunicationCallBack`. daytradr can push events into NT8 and vice versa over the same pipe. No second event channel.
- **Subscription is explicit and server-pulled**: daytradr calls `SubscribeL1`/`SubscribeL2`/`UnsubscribeL1`/`UnsubscribeL2`/`SubscribeL1EventData` into NT8 on demand. User picks instruments from the daytradr UI; NT8 reacts. (Our current "indicator drop = forced stream" is the inverse — we keep this trigger because the user wants it.)
- **No shared memory ring**. Every L1/L2 tick crosses WCF protobuf. Fast enough at retail rates; not the fastest physically possible.
- **Server-side structured book**: `OrderFlowMarketDepthMap` maintained inside daytradr from raw NT8 ticks.
- **Industrial logging**: 12 named log4net streams (App/Debug/Trade/PnL/etc.) with async buffered forwarders (Skip-vs-Grow). We have effectively none.
- **Plugin split**: small NinjaScript surface (`JigsawCommunication.dll`, 143 KB) + fat connection lib (`JTConnection.dll`, 1.3 MB) doing WCF + protobuf + duplex.

Validates: thin NT8 plugin + heavy external client, named pipe IPC, protobuf wire, duplex callbacks, pull-style subscriptions.

Contradicts: our regex JSON, our single-client TCP event server, our `DataEngineManager`/`InstrumentDataManager` dead code.

---

## 3. Architectural decision — Path C (hybrid), with preference

> **Preferred path: hybrid.** WCF duplex + protobuf-net for the control plane and event push; **keep the MMF ring** for L1/L2 ticks. Drop the broken pieces.

**Why this beats the full Jigsaw mirror (Path B):**
Priority 1 (fastest possible ingestion) rules out per-tick protobuf serialization plus a pipe boundary. Our existing struct-copy MMF ring is ~10–50× faster per tick than protobuf-over-named-pipe and is already written. Jigsaw uses WCF for ticks because they hadn't paid the MMF cost; we already have.

**Why this beats keeping the current architecture (Path A):**
Priorities 3 and 4 (consistent portfolio, immediate sync) need a real RPC + duplex event channel. Our regex-JSON pipe and single-client TCP server can't deliver either. We're hand-rolling a bad WCF.

**What we get from each half:**
| Layer | Choice | Reason |
|---|---|---|
| L1/L2 tick ingest (NT8 → client) | MMF SPSC ring (coalesced, struct copy) | Priority 1: fastest physically possible on a single host |
| Control / orders / portfolio queries | WCF duplex + protobuf | Priority 3: typed contracts, no parser bugs, multi-client by config |
| Order / fill / position / account events (NT8 → client) | WCF duplex callback | Priority 4: in-order, with `ClientOrderId` correlation, no dropped frames |
| UI rendering | Pull-based: 60 FPS poll of double-buffered snapshot | Priority 2: decouples render rate from tick rate, per ARCHITECTURE.md §5 |
| Portfolio reconciliation | Periodic full snapshot + event deltas + version counter | Priority 3: lets the client detect divergence and re-pull |

---

## 4. Detailed design per layer

### 4.1 Market data ingestion — MMF SPSC ring (kept, hardened)

**Channel:** `BookFlow_Data_Global` (existing name), 80 B/slot, capacity ≥ 1 M slots (~80 MB MMF, paged, fine).

**Producer side (inside AddOn):**
- Every `BookFlowIndi.OnMarketData`/`OnMarketDepth` enqueues to a single `System.Threading.Channels.Channel<UnifiedMarketDataMessage>` (bounded, ~64 K).
- One dedicated background thread inside `BookFlowAddOn` drains the channel and writes to the MMF ring. Ring becomes true SPSC; existing `Thread.MemoryBarrier` ordering is correct.
- If the channel fills (slow consumer), drop oldest + increment a counter exposed via the WCF service.

**Consumer side (WPF client):**
- One reader thread blocks on the existing semaphore, drains the ring into an in-memory queue, demultiplexes by `TickerId` to the right `DomEngine`.
- DomEngine updates its books and a double-buffered snapshot at tick speed (no UI thread touched).

**Why coalesce instead of CAS multi-producer ring:** simpler, easier to audit, eliminates per-slot sequence handshakes. The single drain thread costs us one context switch per batch, not per tick (the channel batches naturally). Throughput easily exceeds 1 M msg/sec on a desktop CPU.

**Slot growth:** if 80 B/slot becomes constraining (e.g., we want per-slot timestamps for end-to-end latency), grow to 96 B and use `Reserved1`/`Reserved2`. Coordinate with the struct contract.

### 4.2 Control plane — WCF duplex + protobuf-net

**Endpoint:** `net.pipe://localhost/bookflow/control` — single named-pipe service hosted in `BookFlowAddOn`.

**Service contract (`IBookFlowService`)** — request/reply, called by client:
- `RegisterClient(ClientInfo)` → `SessionToken` (idempotent reconnect via token)
- `RequestPortfolioState()` → full snapshot (account + working orders + positions), with monotonic `Version`
- `SubmitOrder(OrderCommand)` → `OrderAck { ClientOrderId, NtOrderId, Status }`
- `CancelOrder(string clientOrderId)` / `CancelAllOrders(AccountSelector)` / `CancelAtPrice(...)` / `FlattenPosition(...)`
- `ListAccounts()` / `GetAccountStatus(string accountName)`
- `Subscribe(SubscriptionRequest)` / `Unsubscribe(...)` — for future server-pulled subscription parity with Jigsaw
- `Ping()` → `Pong { ServerTimestamp, MessagesSent, MessagesDropped, RingFillRatio }`

**Callback contract (`IBookFlowCallback`)** — pushed by server, consumed by client:
- `OnOrderUpdate(OrderUpdate)` — carries both `ClientOrderId` and `NtOrderId`
- `OnExecutionUpdate(ExecutionUpdate)`
- `OnPositionUpdate(PositionUpdate)` — **signed** quantity, `MarketPosition` byte
- `OnAccountItemUpdate(AccountItemUpdate)` — BuyingPower, RealizedPnL, UnrealizedPnL, NetLiquidation, Commission
- `OnConnectionStatus(ConnectionStatus)`
- `OnHeartbeat(Heartbeat { ServerTimestamp, Sequence })` — every 1 s
- `OnPortfolioStateChanged(PortfolioStateDelta { Version, Changes })` — emitted whenever the server's view of the portfolio changes; client uses `Version` to detect missed deltas and re-pull full snapshot

**Wire:** protobuf-net contracts on the class-shaped messages (`[ProtoContract]` / `[ProtoMember]`). Struct contracts in the MMF ring are unchanged.

**Binding:**
- `NetNamedPipeBinding` with `maxReceivedMessageSize=20 MB` (Jigsaw's setting).
- `SecurityMode.None` for localhost (same as Jigsaw).
- `InstanceContextMode.Single` (one service instance fans out to all callbacks).
- `ConcurrencyMode.Multiple` with per-call locking inside handlers.
- `ReliableSession` off (named-pipe is in-order on localhost; reliable session adds latency).

**Multi-client by construction.** Each WPF window opens its own callback channel; the service holds a list, broadcasts to all live callbacks, removes dead ones on `CommunicationException`.

### 4.3 Portfolio state — NT8 is the source of truth

**Server-side state machine in `BookFlowAddOn`:**
- Maintain an authoritative `PortfolioSnapshot { Version, Account, Orders[], Positions[] }`.
- Increment `Version` on every order/exec/position/account event from NT8.
- Push `OnPortfolioStateChanged(delta)` to all callbacks immediately with the new `Version`.
- On `RequestPortfolioState()`, return the full snapshot with current `Version`.

**Client-side reconciliation:**
- On connect: call `RequestPortfolioState()`. Cache `Version`.
- On `OnPortfolioStateChanged(delta)`: if `delta.Version == cached + 1`, apply. Else, re-call `RequestPortfolioState()` to resync (rare, only on dropped frames or restart).
- On heartbeat: if no `OnHeartbeat` in 5 s, mark connection stale and resync.

**Why not just rely on events:** events can be missed across reconnects or service restarts. The snapshot+version pattern guarantees the client converges to NT8's truth.

**Account routing:** every `OrderCommand` carries `AccountName`. Server rejects ambiguous commands (e.g., omitted account when multiple are eligible). Eliminates the silent "pick first non-backtest" footgun in `GetPreferredAccount`.

### 4.4 Event delivery — immediate, in-order, correlatable

- Every `OrderUpdate` event carries both `ClientOrderId` (set at submit) and `NtOrderId` (assigned by NT8). Bidirectional map maintained in the AddOn.
- Position updates carry signed quantity and `MarketPosition`. No more "always positive on short" bug.
- Event delivery is **synchronous within the service** (callbacks invoked from the NT8 account-event thread), but **fire-and-forget per callback** (one slow client doesn't block others).
- A bounded per-callback queue absorbs burst (configurable, ~4 K events). Overflow → callback marked stale → client forced to resync.
- Subscribe to `Cbi.Account.AllAccountsAccountStatusUpdate` to catch accounts added at runtime; subscribe their `OrderUpdate`/`ExecutionUpdate`/`PositionUpdate` immediately. Today we only hook accounts present at AddOn init.

### 4.5 UI rendering — pull-based at 60 FPS

Per `ARCHITECTURE.md §5`:
- Tick stream pushes into `DomEngine` via the MMF reader thread.
- `DomEngine` maintains a double-buffered `BookSnapshot`. Writer thread builds the back buffer; on completion, atomically swap (`Interlocked.Exchange` on the buffer index — no `ReaderWriterLockSlim`).
- WPF thread polls the snapshot at locked 60 FPS via `CompositionTarget.Rendering` or a 16 ms `DispatcherTimer`.
- `DomRowData` no longer fires per-property `OnPropertyChanged` per tick. Batch all updates per row per frame, then fire `PropertyChanged(null)` once to flush all bindings (or, better, render via a `SkiaSharp`/`D3DImage` canvas — that's the roadmap goal in ARCHITECTURE.md but out of scope here).

Order/fill/position events are pushed directly into the relevant view models via the WCF callback — they're low-frequency (max ~10/sec) and don't need conflation.

---

## 5. Classes to drop entirely

- `SharedLibrary.Standard/IPC/ControlPipe.cs` — regex JSON, no-op `Dispose`, fragile framing. Replaced by WCF.
- `NT8DataEngine/NinjaTrader/DataEngineManager.cs` — unused.
- `NT8DataEngine/NinjaTrader/InstrumentDataManager.cs` — unused; truncates `UnifiedEventMessage` payloads when called.
- `BookFlowAddOn._eventQueue`, `_eventSignal`, `_eventWriterThread`, `_eventServerThread`, `_eventListener`, `_eventClient`, `EventWriterLoop`, `RunEventTcpServer`, `CloseEventClient`, `RestartEventChannel`, `BroadcastEventMessage`, `BroadcastOrderUpdateEvent`, `BroadcastExecutionUpdateEvent`, `BroadcastPositionUpdateEvent` — replaced by WCF callback dispatch.
- `BookFlowApp/Services/NT8DirectDataFeed`'s TCP event client (keep only the MMF reader half).

## 6. New components

- `SharedLibrary.Standard/Contracts/Protobuf/` — protobuf-annotated message types (parallel to existing classes; existing types stay for in-process use).
- `SharedLibrary.Standard/Service/IBookFlowService.cs` — service contract.
- `SharedLibrary.Standard/Service/IBookFlowCallback.cs` — callback contract.
- `NT8DataEngine/Service/BookFlowServiceHost.cs` — owns the `ServiceHost`, manages live callbacks, dispatches events.
- `NT8DataEngine/Service/PortfolioStateTracker.cs` — versioned authoritative portfolio state.
- `BookFlowApp/Services/BookFlowServiceClient.cs` — duplex WCF client, reconnect logic, subscription cache.

## 7. Increment plan (each increment ships independently, no half-finished states)

### Increment 1 — Lifecycle & cleanup *(no wire change)*
- Override `BookFlowAddOn.OnStateChange`; initialize on `State.Active`, dispose on `State.Terminated`.
- `BookFlowAddOn` becomes `IDisposable`. Dispose unsubscribes account events, stops background threads via `CancellationTokenSource`, closes pipes/sockets/MMFs.
- `BookFlowIndi.OnStateChange == State.Terminated` → call `BookFlowAddOn.UnregisterTicker(_tickerId)`; ticker IDs recycled via free list.
- `ControlPipeServer.Dispose()` actually cancels the listener loop (interim — full removal in Increment 2).
- Default `BookFlowIndi.EnableLogging = false`. Rate-limited optional debug.

**Verifiable:** NT8 recompile (F5) shows clean teardown, no port-in-use errors, no leaked handles. WPF reconnect after NT8 restart works.

### Increment 2 — WCF service + duplex callback (replaces ControlPipe + TCP events)
- Add protobuf-net + WCF references (NT8 net48 native; .NET 9 client via `System.ServiceModel.NetNamedPipe`).
- Implement `IBookFlowService` + `IBookFlowCallback`.
- `BookFlowAddOn` hosts the service in `State.Active`, closes in `State.Terminated`.
- Wire `OrderUpdate`/`ExecutionUpdate`/`PositionUpdate`/`AccountItemUpdate` through callback dispatch.
- WPF client (`BookFlowServiceClient`) connects, registers, listens for callbacks.
- Delete: `ControlPipe.cs`, TCP event server, `DataEngineManager`, `InstrumentDataManager`.

**Verifiable:** two WPF windows connect simultaneously and both receive every order/fill event. Order submit response includes `NtOrderId`. Cancel-by-clientId works.

### Increment 3 — SPSC coalesced ring + reader hardening
- Channel-based producer-coalescing in `BookFlowAddOn.WriteToGlobalChannel`.
- One drain thread → MMF ring. SPSC, no CAS.
- Drop counters exposed via `Ping()`.
- WPF reader: refactor to demux by `TickerId` and route to per-instrument engines.

**Verifiable:** multi-indicator (ES + NQ + CL) sustained 100 K msg/sec without drops; sequence continuity check passes.

### Increment 4 — Portfolio state versioning + reconciliation
- Server-side `PortfolioStateTracker` with monotonic `Version`.
- `OnPortfolioStateChanged(delta)` callback with version.
- Client detects gap → re-pulls full snapshot.
- Heartbeat watchdog (5 s) triggers resync.
- `AccountName` required on `OrderCommand`; default selectable in UI.

**Verifiable:** kill+restart NT8 mid-session, client resyncs and matches NT8's Account Performance window exactly.

### Increment 5 — Render path & observability
- DomEngine: replace `ReaderWriterLockSlim` double buffer with atomic-swap double buffer.
- DomViewModel: 60 FPS poll instead of per-tick property fires.
- `DomRowData`: prune dead properties, batch `PropertyChanged(null)` per frame.
- log4net (matched Jigsaw pattern): separate `App`, `Trade`, `Debug` loggers with async buffered forwarders.

**Verifiable:** sustained 60 FPS with three windows open during market open; trade log CSV is human-auditable.

---

## 8. Out of scope (separately scheduled)

- SkiaSharp/D3D rendering (ARCHITECTURE.md §4).
- Multi-threaded STA windows (ARCHITECTURE.md §5).
- Embedded persistence (Jigsaw-style VelocityDb).
- Risk pre-checks (Jigsaw-style `alphariskcheck`).
- Multi-broker direct connection (Jigsaw ships TT/Rithmic/CQG natively; we route via NT8 only).

---

## 9. Open questions for the user

1. **WCF on .NET 9 WPF client**: OK with depending on `System.ServiceModel.NetNamedPipe` NuGet? It's a Microsoft-shipped compat shim, mature, but does add an assembly to deploy.
2. **Protobuf-net vs Google.Protobuf**: protobuf-net is what Jigsaw uses, simpler attribute model on existing C# types. Google.Protobuf needs `.proto` files but is the cross-language standard. Either is fine; preference?
3. **Where to enforce `AccountName`**: default to a configured "primary" account and only require explicit selection when multiple are eligible? Or always require explicit?
4. **Logging directory**: Jigsaw uses `%UserProfile%\Documents\Jigsaw Trading\RTPAppLogs\`. We mirror under `%UserProfile%\Documents\BookFlow\Logs\`?

---

## 10. Antigravity Agent Assessment & Notes

We have carefully inspected the proposed enhancements in this document and **fully agree** with the proposed **Path C (Hybrid)** architecture. Decoupling high-frequency data (MMF ring buffer) from transactional control and event routing (Duplex Named Pipe) is the optimal design choice for low-latency trading software on a single host.

Here is our assessment and direct feedback on the open questions:

### 10.1 Feedback on Open Questions

1. **WCF on .NET 9 WPF Client (`System.ServiceModel.NetNamedPipe` NuGet)**:
   * **Agree**: This is fully supported and is the correct approach. Although .NET Core/9 does not support hosting WCF *servers*, the client package is mature, fully supported by Microsoft, and works seamlessly with .NET Framework 4.8 hosts.
   * *Recommendation*: Use `System.ServiceModel.NetNamedPipe` version `6.x` or later on the client side, and ensure the client handle performs automatic channel recreation on fault state.

2. **Protobuf-net vs Google.Protobuf**:
   * **Strong Preference for Protobuf-net**: We strongly agree with using `protobuf-net`. 
   * *Reasoning*: NinjaTrader compiles custom scripts (indicators/addons) dynamically from source code. Managing Google.Protobuf `.proto` code-generation and distributing generated classes adds friction to compile-on-the-fly environments. Protobuf-net allows us to annotate the existing shared C# contracts directly, requiring only the distribution of `protobuf-net.dll` without any external generation tools.

3. **Where to enforce `AccountName`**:
   * **Hybrid Approach**: 
     * If only one trading account is active (or a designated "Primary" account is set in `DomSettings`), fallback to that account automatically.
     * If multiple accounts exist and no account is specified in the client request, return a validation rejection. This avoids user friction for single-account setups while preventing erroneous fills in multi-account environments.

4. **Logging Directory**:
   * **Agree**: `%UserProfile%\Documents\BookFlow\Logs\` is clean, standard, and easy for users to find.

### 10.2 Technical Implementation Recommendations

* **Coalesced Ring Buffer (Section 4.1)**:
  * Using a `System.Threading.Channels.Channel<UnifiedMarketDataMessage>` inside the AddOn to coalesce ticks onto a single MMF writer thread is an excellent design. It eliminates the need for locks or CAS loops on the memory-mapped file itself, restoring a pure Single-Producer Single-Consumer (SPSC) topology on the MMF.
  * *Implementation Detail*: Configure the Channel using `BoundedChannelOptions` with `BoundedChannelFullMode.DropOldest` to protect against slow consumer memory exhaustion under extreme volatility.

* **WCF Fault Handling & Reconnects**:
  * Duplex Named Pipes can enter a faulted state if connection is lost. The client's `NT8DirectDataFeed` must implement an active heartbeat watchdog that actively monitors connection status and recreates the `DuplexChannelFactory` when a timeout is detected.

---

## 11. Post-Increment work — order-flow analytics, DOM UI, and signal reliability

Everything below was built after Increments 1–5 landed. It sits on top of the hybrid IPC core (MMF ring + WCF duplex + versioned portfolio) and does **not** touch the hot tick path: all analysis runs on an off-path 4 Hz timer, and the UI stays pull-based.

### 11.1 Snapshot-on-connect handshake (Increment 2b)
- On `StartAsync`, the engine buffers live ticks, requests an authoritative L2 snapshot (`RequestDomSnapshotAsync`), seeds both books, then drains the buffer discarding any tick whose global sequence (`Reserved1`) is already in the snapshot, and flips to live — no lost/duplicated ticks across the seed window.
- `ServerBookRegistry` / `BookFlowServiceImpl.RequestDomSnapshot` maintain the per-ticker server book; the global sequence is stamped in `RingWriterLoop`.
- Files: `DomEngine.SeedFromSnapshot`, `NT8DataEngine/Service/ServerBookRegistry.cs`, `BookFlowServiceImpl.cs`.

### 11.2 Stable ticker IDs — instrument-picker fix
- **Bug:** ticker IDs were recycled via a free list, so dropping an indicator and adding another re-pointed an open window (an ES window showed NQ prices).
- **Fix:** `RegisterTicker` assigns monotonic, non-recycled IDs with dedup; `UnregisterTicker` is a no-op for the mapping. IDs are stable for the session, so an open DOM is never re-pointed.
- Defensive guard: `SeedFromSnapshot` logs loudly if `snapshot.InstrumentName` ≠ the engine's expected instrument (catches any stale mapping).
- Files: `NT8DataEngine/NinjaTrader/BookFlowAddOn.cs`.

### 11.3 Order-flow microstructure analytics (the "Q5" engine)
- `L2AnalyticsWindow` — a 512-slot tick-indexed ring (`priceTicks % 512`) accruing per-level add / cancel / trade volume (split by aggressor side) in the vicinity of the market. Fed O(1) per event from the data thread under `_syncLock` (`OnDepth`, `OnTrade`); `GetVicinity(center, radiusTicks)` returns a high→low slice.
- `MicrostructureDetector` — **stateless**; takes two vicinity snapshots an interval apart and works on per-level deltas (diff-of-cumulative-counters, with reset/aliasing guards). Detects:
  - **Spoofing/layering** — heavy churn (added+canceled) with little traded and cancels dominating adds.
  - **Iceberg/absorption** — traded far more than the displayed size dropped (hidden replenishment).
  - **Liquidity withdrawal** — one-sided cancel-not-traded summed within `WithdrawalRadiusTicks` of the touch.
  - **Aggression imbalance** — directional skew of aggressive (ask-lifting vs bid-hitting) volume.
- Honest scope is documented in-code: NT8 L2 is aggregated depth (no per-order IDs / queue position), so these are heuristics, not classifications.
- Driven from `DomEngine.OnMicrostructureTick` (4 Hz, after a warm-up), published on the non-conflated `MicrostructureSignals` observable.
- Files: `SharedLibrary.Standard/Analytics/L2AnalyticsWindow.cs`, `MicrostructureDetector.cs`, `DomEngine.OnMicrostructureTick`.

### 11.4 Per-instrument thresholds wired to `DomSettings`
- All detector thresholds live on `DomSettings` (per-instrument; volume scales differ across ES/NQ/CL): `EnableMicrostructureSignals`, `SpoofMinChurn`, `SpoofMaxTradedFraction`, `IcebergMinRefill`, `WithdrawalMinSize`, `WithdrawalRadiusTicks`, `AggressionMinVolume`, `AggressionMinImbalance` — each validated, with `ResetToDefaults`.
- `OnMicrostructureTick` syncs them onto the detector each tick, so edits apply live without restart.

### 11.5 DOM-UI enhancements (Q1–Q5)
- **Q1 last-hit accent** — Price cell shows a colored left border on the last bid/ask execution level.
- **Q2 zone tint** — bid/ask zones tinted on the Price cell via `IsBidZone`/`IsAskZone` DataTriggers.
- **Q3 crossed-market suppression at the display layer** — `DisplayBidDepth`/`DisplayAskDepth`/`DisplayBidSnapshot`/`DisplayAskSnapshot` gate stray depth in the spread. (Engine-level crossed pruning was deliberately **not** added — best-price resolution strands the level, and which leg is stale is ambiguous from depth alone.)
- **Q5 observations glyph** — transient `S`/`I` glyph on the matching ladder row, cleared by the 60 fps tick.
- **DOM "Center" fix** — `ScrollToTopOfBook` targeted the bid/ask mid but parked it 5 rows below center (a hard-coded `-5`); removed so top-of-book is dead-center.
- Files: `DomGridWindow.xaml(.cs)`, `Models/DomRowData.cs`, `DomViewModel`.

### 11.6 Signal feed — docked panel + readable labels + direction
- **Docked panel** beside the ladder (toggled by the `Σ` button), stretching to the ladder height — replaced the old popup; append-only feed capped at 60 entries.
- **Plain-English labels** carrying magnitude + price: e.g. `Spoof bid +120/-115 @ 5000.00`, `Iceberg ask 50 hidden @ 5000.25`, `Bid pulled 76 (≤5t of 5000.00)`, `Buy aggression 60 vs 5`. (Withdrawal size is a vicinity aggregate over the detection window — clarified in the label so it isn't mistaken for a single-level depth.)
- **Direction arrows** — ▲ green (up) / ▼ red (down) / • gray, **bold when the signal is strong** (magnitude ≥ 2× threshold, or imbalance ≥ 0.85). Mapping documented in code (e.g. spoof bid → ▼ since fake pressure reverses when pulled).
- Files: `Models/SignalFeedItem.cs`, `DomViewModel.OnSignal`, `DomGridWindow.xaml`.

### 11.7 Signal reliability meter — prediction power, scored by price level (not time)
- `SignalReliabilityTracker` (in `SharedLibrary.Standard/Analytics`) forward-evaluates each emitted signal by **price-level barriers**: target = anchor ± `PredictionEvalTicks` in the predicted direction, stop = the same against. Reaching target before stop = WIN, stop first = LOSS (a first-passage / triple-barrier test).
- Resolved outcomes accumulate a **per-type hit-rate** (Beta(2,2)-smoothed). A new feed line is stamped with that type's hit-rate **as known at print time** — past lines are never revised, so there is no look-ahead bias and the UI stays append-only.
- **Visual:** a right-aligned 5-segment meter `▰▰▰▱▱`, gray while "learning" (< `ReliabilityMinSamples` resolved), then red < 45% → amber → green > 60%; tooltip shows the rate and sample count.
- **Performance:** `RegisterSignal` / `OnPriceSample` / `GetReliability` are O(pending)/O(1), run inside the existing 4 Hz tick. Pending list capped at 64 (drop-oldest); reset on `ClearAllData`. The L1/L2 path, book updates, and 60 fps render loop are untouched.
- New tunables on `DomSettings`: `PredictionEvalTicks` (default 4), `ReliabilityMinSamples` (default 5).
- Files: `SignalReliabilityTracker.cs`, `MicrostructureDetector.cs` (signal carries `ReliabilityBars/Ratio/Samples/Learning`), `DomEngine.OnMicrostructureTick`, `SignalFeedItem.cs`, `DomGridWindow.xaml`.

### 11.8 Professional DOM settings dialog
- `GridSettingsWindow` rebuilt with a cohesive dark theme: a **Columns** tab as a proper table (named columns + width / font size / bold / font color), and an **Order-Flow Signals** tab grouping the thresholds by detector (Spoofing / Iceberg / Withdrawal / Aggression) with an enable toggle.

### 11.9 Controller — instrument refresh + detachable log
- **Refresh instruments** (`RefreshInstrumentsAsync`) re-pulls the instrument→ticker map without disconnecting or closing open DOMs, preserving the selection by name (safe because ticker IDs are stable).
- **System Log moved to a side window** — the controller now sizes to content (no more truncated "System Log"); a **System Log** button opens a detachable `LogViewerWindow` sharing the controller's view model (Auto-Scroll, Copy, Select All, Clear).
- Files: `ControllerViewModel.cs`, `ControllerWindow.xaml(.cs)`, `LogViewerWindow.xaml(.cs)`.

### 11.10 Obfuscation removed
- .NET Reactor obfuscation was removed from the build (per `*.dotnet_reactor.targets` being disabled) to keep builds and debugging transparent.

### 11.11 Test coverage
- xUnit project (`BookFlow.Tests`, net9.0-windows) — **47 tests**, all green. Covers `SharedRingBuffer`, WCF contract mapping, `DomRowData`, `DomEngine` (incl. snapshot seed and the locked-market trade-attribution P0 fix), `L2AnalyticsWindow`, `MicrostructureDetector` (incl. bias), and `SignalReliabilityTracker`.

