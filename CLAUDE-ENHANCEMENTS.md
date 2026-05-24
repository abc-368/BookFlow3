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

> **Implemented as:** producer coalescing uses a `ConcurrentQueue<UnifiedMarketDataMessage>` + `AutoResetEvent` + a single drain thread (not `System.Threading.Channels`, which isn't in-box on .NET Framework 4.8). The global sequence is stamped into `Reserved1` in the drain loop. Same SPSC topology and DropOldest behavior, zero extra NuGet on the NT8 side.

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

> **Implemented as:** WCF duplex over `netNamedPipe` as planned, but serialized with the built-in **`DataContractSerializer`** (`[DataContract]`/`[DataMember]`), **not** protobuf-net. Reason: avoids shipping `protobuf-net.dll` into NT8's dynamic-compile environment (assembly-load friction), and both host (net48) and client (net9) interop warning-free with zero codegen. Endpoint/binding details below otherwise hold.

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

> **Implemented as:** the protobuf folder was **not** created (we use `DataContract` on the existing contract classes — see §4.2). Portfolio state was **inlined into `BookFlowAddOn`** (`_portfolioVersion`, `BuildPortfolioSnapshot()`, …) rather than a separate `PortfolioStateTracker.cs`, to keep the NT8-compiled surface minimal and give the snapshot builder direct access to CBI objects. The service/host/client components below shipped as listed.

- ~~`SharedLibrary.Standard/Contracts/Protobuf/`~~ — *not created; `DataContractSerializer` used on existing contract types.*
- `SharedLibrary.Standard/Service/IBookFlowService.cs` — service contract.
- `SharedLibrary.Standard/Service/IBookFlowCallback.cs` — callback contract.
- `NT8DataEngine/Service/BookFlowServiceHost.cs` — owns the `ServiceHost`, manages live callbacks, dispatches events.
- ~~`NT8DataEngine/Service/PortfolioStateTracker.cs`~~ — *versioned portfolio state inlined into `BookFlowAddOn` instead.*
- `BookFlowApp/Services/BookFlowServiceClient.cs` — duplex WCF client, reconnect logic, subscription cache.

## 7. Increment plan (each increment ships independently, no half-finished states)

### Increment 1 — Lifecycle & cleanup *(no wire change)*
- Override `BookFlowAddOn.OnStateChange`; initialize on `State.Active`, dispose on `State.Terminated`.
- `BookFlowAddOn` becomes `IDisposable`. Dispose unsubscribes account events, stops background threads via `CancellationTokenSource`, closes pipes/sockets/MMFs.
- `BookFlowIndi.OnStateChange == State.Terminated` → call `BookFlowAddOn.UnregisterTicker(_tickerId)`. *(Superseded: see §11.2 — IDs are now session-monotonic and never recycled; `UnregisterTicker` is a no-op. The original free-list recycling caused the ES-shows-NQ bug.)*
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
2. **Protobuf-net vs Google.Protobuf**: protobuf-net is what Jigsaw uses, simpler attribute model on existing C# types. Google.Protobuf needs `.proto` files but is the cross-language standard. Either is fine; preference? *(Resolved: neither — shipped on WCF's built-in `DataContractSerializer` to avoid third-party assembly-load friction in NT8. See §4.2.)*
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
- **Docked panel** beside the ladder (toggled by the `Σ` button), replacing the old popup. (Superseded by §11.12: it's now a full-height right rail with a virtualizing, scrollable feed capped at 200.)
- **Plain-English labels** carrying magnitude + price: e.g. `Spoof bid +120/-115 @ 5000.00`, `Iceberg ask 50 hidden @ 5000.25`, `Bid pulled 76 (≤5t of 5000.00)`, `Buy aggression 60 vs 5`. (Withdrawal size is a vicinity aggregate over the detection window — clarified in the label so it isn't mistaken for a single-level depth.)
- **Direction arrows** — ▲ green (up) / ▼ red (down) / • gray, **bold when the signal is strong** (magnitude ≥ 2× threshold, or imbalance ≥ 0.85). Mapping documented in code (e.g. spoof bid → ▼ since fake pressure reverses when pulled).
- Files: `Models/SignalFeedItem.cs`, `DomViewModel.OnSignal`, `DomGridWindow.xaml`.

### 11.7 Signal reliability meter — prediction power, scored by price level (not time)
- `SignalReliabilityTracker` (in `SharedLibrary.Standard/Analytics`) forward-evaluates each emitted signal by **price-level barriers**: WIN if price reaches +Target in the predicted direction before −Stop against (asymmetric allowed — a first-passage / triple-barrier test). **The barrier is per-type** (see §11.17) and equals that type's auto-trade bracket, so the meter measures exactly the move the type is traded for. **Note:** this is a hit-rate at those barriers with zero costs, not a P&L predictor; real P&L/costs are tracked by NT8.
- Resolved outcomes accumulate a **per-type hit-rate** (Beta(2,2)-smoothed). A new feed line is stamped with that type's hit-rate **as known at print time** — past lines are never revised, so there is no look-ahead bias and the UI stays append-only.
- **Visual:** a right-aligned 5-segment meter `▰▰▰▱▱`, gray while "learning" (< `ReliabilityMinSamples` resolved), then red < 45% → amber → green > 60%; tooltip shows the rate and sample count.
- **Performance:** `RegisterSignal` / `OnPriceSample` / `GetReliability` are O(pending)/O(1), run inside the existing 4 Hz tick. Pending list capped at 64 (drop-oldest); reset on `ClearAllData`. The L1/L2 path, book updates, and 60 fps render loop are untouched.
- Success definition is **per-type** (§11.17). Only `ReliabilityMinSamples` (default 5) remains global, in the Signals tab.
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
- xUnit project (`BookFlow.Tests`, net9.0-windows) — **63 tests**, all green. Covers `SharedRingBuffer`, WCF contract mapping, `DomRowData`, `DomEngine` (incl. snapshot seed, the locked-market trade-attribution P0 fix, and bracket-request construction), `L2AnalyticsWindow`, `MicrostructureDetector` (incl. bias), `SignalReliabilityTracker`, and `SignalAutoTrader` (arming/green-gate/concurrency).

### 11.12 Signals rail — full-height + virtualized scrolling
- The order-flow panel is now a **full-height right rail** spanning the whole DOM window (outer 2-column grid; panel at `Grid.Column="1" Grid.RowSpan="4"`), not just the ladder row — so it matches the window height and resizes with it. When the `Σ` toggle is off, the column collapses to 0 width (window shrinks back to ladder width).
- The feed list became a **virtualizing `ListBox`** (`VirtualizingStackPanel` + `Recycling`, selection chrome stripped). Only on-screen rows are realized → O(visible) render cost regardless of count, and it scrolls once entries exceed the visible area.
- `MaxFeed` raised 60 → **200** for useful scroll-back; the cap bounds memory/visual tree. Justification: the feed is event-driven and sparse (signals fire a few/sec at most, never on the hot tick path or 60 fps loop), so scrolling a virtualized capped list has negligible cost.
- Files: `DomGridWindow.xaml`, `DomViewModel` (`MaxFeed`).

### 11.13 Per-type feed muting (manual + auto-hide-when-red)
- Per-instrument `DomSettings` flags: `ShowSpoofingSignals` / `ShowIcebergSignals` / `ShowWithdrawalSignals` / `ShowAggressionSignals` (default on) + `AutoHideUnreliableSignals` (default off).
- Filtering is at the **display layer** (`DomViewModel.ShouldPrintSignal`): detection and reliability scoring keep running for muted types, so their meters stay accurate and re-enabling shows the current rate instantly. Manual toggle off → not rendered (feed + ladder glyph). `AutoHideUnreliableSignals` additionally mutes a type whose hit-rate is in the meter's **red band (<45%)** with enough resolved samples (`!ReliabilityLearning`) — **overrides** an enabled toggle while red, recovers automatically.
- UI: per-detector "Show in feed" checkboxes + a master "Auto-hide types whose reliability stays red" in the Signals tab.
- Files: `DomSettings.cs`, `DomViewModel.ShouldPrintSignal`, `GridSettingsWindow.xaml`.

### 11.14 Auto-trade on reliable signals — server-side OCO brackets (Increment 1: foundation)
Goal: when a signal *type* proves reliable (green meter), act on it — enter in the forecast direction with a configurable size, attach a profit target **and** a protective stop (both in ticks). The chosen design **leverages NT8's server-side OCO** rather than hand-rolling exit management client-side.

**Decision (entry vs exit timing):** the exit pair is placed **after the entry fills**, by the AddOn, not in parallel — this avoids the naked-order risk of a pre-placed exit when a limit entry never fills, anchors the target/stop to the *actual* average fill, and sizes the exit to the *actual* filled quantity.

**Increment 1 shipped — the bracket mechanism (server-side + full client plumbing):**
- New contract `BracketOrderRequest` (side, market/limit + entry price, qty, `TargetTicks`, `StopTicks`) + `IBookFlowService.SubmitBracketOrder`.
- `BookFlowAddOn.WcfSubmitBracketOrder` creates the entry as `OrderEntry.Automated` and registers a `PendingBracket` (keyed by entry OrderId). On `OnOrderUpdate` → `OrderState.Filled`, `TryPlaceBrackets` submits a **profit-target limit + protective-stop StopMarket with the same `oco` tag** → NT8/broker manages one-cancels-other server-side. Priced off `AverageFillPrice`, sized to `Filled`, rounded to the tick grid; idempotent per entry; registry cleared on shutdown.
- Plumbing: `BookFlowServiceImpl`/`BookFlowServiceHost` handler delegate, `BookFlowServiceClient.SubmitBracketOrderAsync`, `IDataFeed.SubmitBracketOrderAsync` (+ test stub), `NT8DirectDataFeed`, and `DomEngine.SubmitBracketOrderAsync(isBuy, entryIsLimit, entryLimitPrice, qty, targetTicks, stopTicks)` — the single call the auto-trader will use.
- **Tested:** client-side request construction (direction mapping, ticks, instrument) is unit-tested via a stub feed (`DomEngineBracketTests`). **Not yet validated live:** the server-side fill→OCO path needs a running NT8 + Sim account.
- **Limitations (v1):** brackets are placed only on a *full* fill (a partially-filled-then-cancelled limit entry would leave an unbracketed partial).

**Increment 2 shipped — the client trigger layer:**
- `SignalAutoTrader` (`BookFlowApp/Engine`) — on a signal it checks: master armed → type enabled → bias non-neutral → **green** (`ReliabilityRatio*100 ≥ ReliabilityGreenPercent` and not learning) → concurrency. If clear, it fires one bracket via `DomEngine.SubmitBracketOrderAsync`. Aggression → market; level types → limit at `signal price + OffsetTicks*tick` (signed, literal). Runs independently of feed muting.
- **Concurrency = one auto-position per instrument** via a small state machine (Idle → PendingEntry → InPosition → Idle), driven by `OnPositionChanged` (signed qty from the VM). A stuck PendingEntry self-resets after `AutoTradeEntryTimeoutSeconds + 3s`.
- **Entry timeout** added to the server path: `BracketOrderRequest.EntryTimeoutSeconds`; the AddOn cancels an unfilled *limit* entry past its deadline on the 1 s heartbeat (`ExpireStaleBracketEntries`).
- **Configurable green gate**: `DomSettings.ReliabilityGreenPercent` (default 60, range 50–95) drives both the auto-trade gate **and** the meter's green band (`SignalFeedItem.GreenThreshold`), so "green" means the same thing everywhere.
- **Settings**: per-type `AutoTradeConfig` (`Enabled`, `UseMarketOrder`, `OffsetTicks` signed, `TargetTicks`, `StopTicks`, `Size`) for the 4 detectors, plus master `AutoTradeArmed` (off by default), `ReliabilityGreenPercent`, `AutoTradeEntryTimeoutSeconds`. UI: a new **Auto-Trade** tab in `GridSettingsWindow` + an **ARM** toggle (red kill-switch) on the DOM toolbar bound to `AutoTradeArmed`.
- Tested: `SignalAutoTraderTests` (8) cover armed/enabled/green/learning/neutral gating, aggression-forces-market, limit-offset pricing, and the one-position-per-instrument guard.
- **Still recommend validating on a Sim101 account first** — the live order path (entry + fill→OCO + timeout cancel) needs a running NT8 to exercise.
- Files: `Service/ServiceContracts.cs`, `Service/IBookFlowService.cs`, `NinjaTrader/BookFlowAddOn.cs`, `Service/BookFlowServiceImpl.cs`, `Service/BookFlowServiceHost.cs`, `Services/BookFlowServiceClient.cs`, `Interfaces/IDataFeed.cs`, `Services/NT8DirectDataFeed.cs`, `Engine/DomEngine.cs`.

### 11.15 New, literature-grounded signals: OFI + book imbalance
Added the two best-evidenced short-horizon microstructure predictors as first-class signal types (`MicrostructureSignalType.OrderFlowImbalance = 5`, `BookImbalance = 6`), reusing the existing add/cancel/trade accumulators in `L2AnalyticsWindow`:
- **Order-flow imbalance (OFI)** — Cont–Kukanov–Stoikov, generalized over a near-touch vicinity (`OfiRadiusTicks`, default 3): `OFI = (bidAdds − bidCancels − sellsHittingBid) − (askAdds − askCancels − buysLiftingAsk)` over the interval. Positive → buy pressure (UP). Threshold `OfiMinImbalance` (default 150). The single best-supported predictor in the literature.
- **Book imbalance** — `(Qbid − Qask)/(Qbid + Qask)` at the touch (read from the current snapshot, no diff needed). Threshold `BookImbalanceMinRatio` (0.6) + `BookImbalanceMinSize` (50). A documented one-tick-ahead skew.
- Fully **observable/measurable** now: they flow through the feed (OFI green/red by bias, book imbalance purple), per-type **Show-in-feed** toggles (`ShowOfiSignals`/`ShowBookImbalanceSignals`), thresholds in the Signals tab, and the **reliability meter** (so you can see their real hit-rate at your configured success barrier).
- **Auto-trade wiring (added after initial observe-only release):** `OfiAutoTrade` / `BookImbalanceAutoTrade` configs + `SignalAutoTrader.ConfigFor` cases + Auto-Trade tab rows. Both carry no price level → **market entry** (like aggression). Default disabled — still **watch their reliability meters before enabling**.
- Follow-ups: **VPIN-style toxicity filter** now shipped (see §11.16). Still deferred: **micro-price** for a better entry/fair-value anchor (no consumer until OFI/book are auto-traded).
- Honest caveat: these are very short-horizon, small, cost/latency-sensitive edges, and on NT8's aggregated L2 (no per-order queue position) they lose some sharpness — better-founded than the original four, not magic.
- Tested: `MicrostructureDetectorTests` add OFI (bid-adds/ask-cancels → UP) and book-imbalance (bid-heavy → UP) cases.
- Files: `Analytics/MicrostructureDetector.cs`, `Contracts/DomSettings.cs`, `Engine/DomEngine.cs`, `Models/SignalFeedItem.cs`, `ViewModels/DomViewModel.cs`, `Views/GridSettingsWindow.xaml`.

### 11.16 Auto-trade flow-toxicity gate (VPIN-style)
A regime filter that stands the auto-trader aside in toxic (one-sided / informed) flow, where fills are worst:
- `DomEngine` maintains a **decayed aggressive buy/sell volume** (updated in `UpdateLastTrade`, which already classifies hit-bid vs lift-ask) and exposes `FlowToxicity = |buy − sell| / (buy + sell)` ∈ [0,1] (1 = fully one-sided). Per-trade decay ≈ 0.97. Reset on `ClearAllData`.
- `SignalAutoTrader` takes an optional toxicity provider and **skips entry when `FlowToxicity > AutoTradeMaxToxicity`** (`DomSettings.AutoTradeMaxToxicity`, default **1.0 = off**, range 0–1). Default-off means no behavior change until you tune it down.
- UI: "Max flow toxicity (0–1; 1 = off)" on the Auto-Trade tab. Tested: `SignalAutoTraderTests` (toxic > max → no fire; calm ≤ max → fires).
- A pragmatic VPIN stand-in (no volume-bucket machinery); tune live. The full micro-price work remains deferred.
- Files: `Engine/DomEngine.cs`, `Interfaces/IDomEngine.cs`, `Contracts/DomSettings.cs`, `Engine/SignalAutoTrader.cs`, `ViewModels/DomViewModel.cs`, `Views/GridSettingsWindow.xaml`.

### 11.17 OFI/book auto-trade + per-type target/stop (single source of truth)
Sim101 round-trip of the full auto-trade loop confirmed (entry → fill → OCO target/stop). Two changes followed:
- **OFI + book imbalance are now auto-tradable** (`OfiAutoTrade`/`BookImbalanceAutoTrade` configs, `SignalAutoTrader.ConfigFor` cases, Auto-Trade tab rows). Both carry no price level → **market entry** (like aggression). Disabled by default.
- **Per-type target/stop replaces the global success barrier.** The removed global `PredictionTargetTicks/StopTicks` are superseded by each type's `AutoTradeConfig.TargetTicks/StopTicks`, which now drive **both** the auto-trade bracket **and** the reliability meter's success barrier for that type (`DomEngine.BarrierFor` sets the tracker barrier per signal before `RegisterSignal`). This guarantees "green" measures the exact move the type is traded for — preventing the meter-vs-trade divergence that earlier showed "60% but losing".
- **Best-practice default brackets** (ticks), differentiated by signal character (reasoned starting points, tune live): Spoof 5/4, Iceberg 8/4, Withdrawal 6/4, Aggression 6/4, OFI 4/4, Book 3/3.
- Operating at **1 lot**, so the partial-fill bracket limitation is moot (no partials).
- Files: `Contracts/DomSettings.cs`, `Contracts/AutoTradeConfig.cs`, `Engine/DomEngine.cs` (`BarrierFor`), `Engine/SignalAutoTrader.cs`, `Views/GridSettingsWindow.xaml`.

---

## 12. Per-signal feedback loop (committed roadmap, phased)

Goal: close the loop between **prediction** (the meter) and **execution** (real fills) per signal type, so we can (1) explain mismatches, (2) calibrate/suggest parameters, (3) optionally auto-tune — addressing the "green meter, negative P&L" dissonance. **The objective is to maximize *realized* expectancy and to make "green" honest — not to chase an over-optimistic meter.** Disabled by default.

### Why prediction ≠ realized (even ignoring fees)
- **Mid vs fill / spread** — meter scores the *mid* crossing the barrier from the anchor; the trade enters at bid/ask, the target is a *limit* (needs a touch), the stop is a *market* (slips). Structural drag.
- **Anchor/time mismatch** — meter starts at signal time/anchor; the trade starts at fill time/price (seconds later for a limit).
- **4 Hz sampling** — a fast wick can trip a real StopMarket the meter misses → meter optimistic for tight stops.
- **Touch vs cross** — meter "win" = mid touched target once; the trade needs the target limit to actually fill.

### Key enabling insight
Record each signal's **forward price path**; then re-score *any* candidate target/stop **offline** against the same recorded population via first-passage — no live trial-and-error, no per-setting sample fragmentation. (Works for trade params target/stop/offset; for *detection* thresholds only *raising* them can be tested offline.)

### §12.1 — Increment 1 SHIPPED: the analytical engine (default off)
- `SignalOutcomeRecorder` (`SharedLibrary.Standard/Analytics`) — on each fired signal, captures the direction-adjusted forward mid path over a fixed horizon (80 samples ≈ 20 s), independent of the live meter's early-resolving barrier. Bounded (≤64 in-flight, ≤5000 retained).
- `BracketReScorer` (`SharedLibrary.Standard/Analytics`) — pure: `Evaluate(outcomes, T, S)` → win-rate + gross expectancy (ticks, fees aside) by first-passage; `Suggest(outcomes, minSamples)` → the bracket maximizing expectancy over a grid, gated by sample size.
- `DomSettings.EnableSignalAudit` (default **false**) + Signals-tab checkbox. When on, `DomEngine` feeds the recorder and ~once/minute logs, per type, the realized win-rate/expectancy at the **current** bracket vs the **suggested** bracket (`BookFlowLog`, category `Audit:<instrument>`).
- Validation workflow (you run one signal type at a time): compare the logged meter/path win-rate against **NT8's Trades tab** realized win-rate for the same window.
- Tests: `BracketReScorerTests` (first-passage, expectancy, suggestion + min-sample gating), `SignalOutcomeRecorderTests` (path capture, direction inversion, neutral ignored).

### §12.2 — Increment 2 NEXT: calibration + suggestions surfaced
- **Calibrate the meter to execution**: evaluate the barrier from the expected/actual fill (not the anchor), target as a touch, stop as slipped — so "green" predicts the trade. (Needs fill correlation, below.)
- **Fill correlation**: tie each auto-traded signal to its real entry/exit fills via `ClientOrderId`/OCO legs for automated predicted-vs-realized attribution (slippage, meter-won-but-trade-lost counts).
- **Suggestions UI** (Feedback tab): per type show predicted vs realized, the attribution, and the suggested bracket with Wilson confidence + sample size.
- **Persistence (prerequisite)**: settings + accumulated outcome/reliability history to disk per instrument — otherwise every restart wipes tuning and re-enters "learning". (See Tier-1 pending items.)

### §12.3 — Increment 3 LAST, GUARDED: auto-tune (high-risk)
Online adaptive control on a noisy, non-stationary process — easy to do badly (overfit last-N, oscillate). If built: significance gating (min N, CI), slow/bounded adaptation, **shadow mode** (track would-have-been before applying), per-regime separation, kill-switch, **default off**, and prefer **one-click-apply over fully autonomous**. Deferred until §12.1–12.2 prove the suggestions are consistently good and stable.

### Honest caveats
Short-horizon edges are small, cost/latency-sensitive, and decay; offline re-scoring needs enough samples to avoid curve-fitting (hence the min-sample gate and CI), and non-stationarity means a tune good this morning may be wrong this afternoon. The loop informs judgment; it isn't a money printer.

---

## 13. Persistence & session/contract context (shipped — §12.2 prerequisite)

The feedback loop and tuning are pointless if they reset every launch, so persistence landed next.

### Storage
- `%UserProfile%\Documents\BookFlow\Settings\<root>.json` and `…\History\<root>.json` (`BookFlow.App.Persistence.BookFlowPaths`).
- **Keyed by root / continuous symbol** (`TradingContext.Root`: "ES 06-26" → "ES") so settings *and* history **survive quarterly contract rolls**.

### Settings (always persisted)
- `SettingsStore` + an explicit `DomSettingsSnapshot` DTO (System.Text.Json kept in the **App** layer, not Shared — no NT8 dependency). Persists the signal/trade tuning subset: detector thresholds (incl. OFI/book), per-type `AutoTradeConfig` (enable/market/offset/target/stop/size), green %, toxicity cap, entry timeout, min-samples, audit flag, show-in-feed flags.
- **`AutoTradeArmed` is deliberately NOT persisted** — arming is always a fresh per-session decision. (Column/font/color layout not persisted yet.)
- Loaded onto the `DomSettings` in `ControllerViewModel.LaunchSelectedInstrumentDom` before the engine starts.

### History (keep by default, explicit wipe)
- `HistoryStore` saves/loads the recorded `SignalOutcome` list (forward paths + the **session** tag) per root. On launch the engine `ImportOutcomes(...)` reloads them and **rebuilds each type's meter** at its current barrier (so "green" is immediately available), and the recorder resumes for further re-scoring.
- **Wipe** = delete the file(s), effective next session: controller buttons **"Wipe Sym Hist"** (selected root) and **"Wipe All Hist"** (confirmed). No keep-toggle — keep is implicit.
- **Crash safety**: a 5-minute timer in `ControllerViewModel` persists every open DOM's settings + history; also saved on window close and on controller dispose. `SignalOutcomeRecorder.Snapshot()` is taken under a lock since the 4 Hz thread mutates the list.

### Session & continuous/traded display
- `TradingContext.Classify` / `CurrentSession` — US/Eastern basis, contiguous Asia / London / US buckets (London 03–08, US 08–17, Asia otherwise). Each recorded outcome is **tagged with its session** (`SignalOutcome.Session`) for future per-session segmentation (the chosen "display + tag now, segment later" path).
- DOM header now shows the **traded contract** (e.g. `ES 06-26`), the **continuous** (`ES cont`), and a colored **session chip** (refreshed ~every 4 s; TZ conversion is throttled off the 60 fps tick).
- New `IDomEngine` members: `ContinuousName`, `CurrentSession`, `ExportOutcomes()`, `ImportOutcomes()`.

### Tests
- `TradingContextTests` (session-by-hour, root-suffix stripping), `DomSettingsSnapshotTests` (tuning round-trips; `AutoTradeArmed` never restored). *(Pending a clean run — the app was open and locking the output DLLs.)*

### Pending next
Per-session **segmentation** of reliability/suggestions (data is already tagged); persisting column/font layout; the §12.2 calibration + suggestions UI; then guarded §12.3 auto-tune.

