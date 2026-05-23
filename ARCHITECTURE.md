# BookFlow Architecture & Implementation Roadmap

BookFlow is a high-performance trading depth-of-market (DOM) and order flow visualization solution. This document describes the system architecture, analyzes current implementation issues, and outlines the roadmap for future optimizations—including scaling to parallel DOM windows and leveraging hardware acceleration.

---

## 1. High-Level System Architecture

BookFlow decouples heavy market data acquisition and order routing (which must run inside the single-threaded environment of NinjaTrader 8) from high-frequency UI rendering (handled by a standalone WPF client).

```mermaid
graph TD
    subgraph NT8 [NinjaTrader 8 Process - .NET Framework 4.8]
        Indi[BookFlowIndi: L1/L2 Capture] -->|Write binary| RingBuf(SharedRingBuffer: MMF)
        AddOn[BookFlowAddOn: Manager] <-->|Named Pipes| ControlPipe(ControlPipe: IPC)
        AddOn <-->|TCP Loopback 38755| TcpEvents[TCP Order Event Server]
    end

    subgraph Shared [SharedLibrary.Standard - .NET Standard 2.0]
        Contracts[DataContracts.cs: Explicit StructLayout]
        IPC[IPC Logic: RingBuffer & ControlPipe]
    end

    subgraph Client [WPF Standalone App - .NET 9.0 / .NET 10]
        Feed[NT8DirectDataFeed] <--> RingBuf
        Trading[NT8TradingService] <--> ControlPipe
        Trading <--> TcpEvents
        Engine[DomEngine] <-- Snapshot Reads --- Feed
        VM[DomViewModel] <-- Subscribes --- Engine
        UI[DomGridWindow: DevExpress Grid] <-- Data Binding --- VM
    end
```

### Architectural Components

1. **Shared Contracts (`BookFlow.Shared`)**
   * Target Framework: `.NET Standard 2.0` (enabling reference by both .NET Framework 4.8 and .NET 9/10).
   * Key Artifacts: `PriceLevel`, `BookSnapshot`, `LadderUpdate`, `DomSettings`.
   * Choice: Structs use `[StructLayout(LayoutKind.Sequential, Pack = 1)]` to guarantee exact byte offsets, allowing zero-serialization MMF reads across different runtime architectures.
2. **NinjaTrader Integration (`BookFlow.NT8DataEngine`)**
   * Target Framework: `.NET Framework 4.8` (required by NinjaTrader).
   * **`BookFlowIndi`**: Captures raw Level 1 (`OnMarketData`) and Level 2 (`OnMarketDepth`) ticks. It packages them as raw `UnifiedMarketDataMessage` structs and streams them into the shared memory file.
   * **`BookFlowAddOn`**: Singleton manager that handles global trading events (orders, executions, position updates) and exposes control channels.
3. **DOM Engine (`BookFlow.App.Engine`)**
   * Target Framework: `.NET 9.0` (Client-side execution).
   * Maintains in-memory sorted books (`_bidBook` / `_askBook`), aligns prices, resolves crossed markets, and handles order integration.
   * Employs a double-buffered snapshot mechanism (`_readerSnapshot` / `_writerSnapshot`) to decouple data parsing from UI rendering threads.
4. **WPF Client (`BookFlow.App`)**
   * Renders a 15-column depth ladder using DevExpress `GridControl`.
   * Reuses visual row models (`DomRowData`) in-place to minimize GC collections and optimize grid layout calculations.

---

## 2. IPC Channels and Communication Design

The system implements three communication channels to decouple the processes:
* **MMF Ring Buffer (`BookFlow_Data_Global`)**: A high-speed, circular memory-mapped file for transmitting high-frequency Level 1 and Level 2 market data messages.
* **Named Pipe (`BookFlow_Control_Global`)**: A reliable, bi-directional pipe for command-and-control requests (e.g., submitting/canceling orders, querying instrument dictionaries).
* **TCP Port `38755`**: Broadcasts asynchronous order, fill, and position changes as hand-formatted JSON strings.

---

## 3. Comprehensive Code Quality Audit & Findings

A deep code-level audit of the current repository has identified several critical bottlenecks, memory leaks, and concurrency hazards.

### P0 — Critical Issues (Safety and Correctness)

1. **Book Volume Corruption on Price Overlap**
   * **Location**: `DomEngine.cs:431-460` (`UpdateLastTrade`)
   * **Symptom**: During crossed/locked markets or transient overlaps where the same price exists in both the bid and ask books, the engine merges the books into a single dictionary, records the trade, and writes the mutated level back to *both* books. This clobbers the side-specific depth volumes and order counts.
   * **Fix**: Search and mutate `_bidBook` and `_askBook` independently without merging them.
2. **Unmanaged Property & Event Subscriptions (Memory Leaks)**
   * **Location**: `DomEngine.cs` and `DomViewModel.cs`
   * **Symptom**:
     * `DomEngine` subscribes to `DomSettings.PropertyChanged` using an anonymous lambda, keeping the engine rooted in memory.
     * `DomEngine` subscribes to `_tradingService.OrderBookChanged` and never unsubscribes.
     * `DomViewModel` subscribes to `_domEngine.LadderUpdates` and leaks the subscription handle.
   * **Fix**: Implement proper disposals and store event handles as class fields to unsubscribe inside `Dispose()`.
3. **Lock Contention in Double-Buffering**
   * **Location**: `DomEngine.cs:519, 821`
   * **Symptom**: The snapshot reader/writer uses a `ReaderWriterLockSlim`. During rapid market updates, the UI thread (reading the snapshot) and the background data thread (writing the snapshot) block each other, causing visual stutters.
   * **Fix**: Implement true lock-free double-buffering by swapping snapshots atomically with a `volatile` pointer index.
4. **Race Condition in SharedRingBuffer**
   * **Location**: `SharedRingBuffer.cs:30-54`
   * **Symptom**: `TryWrite` and `TryRead` fetch and modify head and tail offsets without atomic check-and-swap actions. Under rapid multi-threaded production or consumption, this can corrupt the ring pointers or drop data.
   * **Fix**: Use `Interlocked.CompareExchange` for ring pointer offsets.

### P1 — High-Priority Issues (Performance & GC Pressure)

5. **SortedDictionary Key Traversals inside the Hot Path**
   * **Location**: `DomEngine.cs` (e.g., L341, L380, L465, L466)
   * **Symptom**: Calling `_bidBook.Keys.Last()` or `_askBook.Keys.First()` to compute best bid/ask bounds iterates the internal tree structure and allocates enumerators on every single L2 update.
   * **Fix**: Cache and maintain `_bestBid` and `_bestAsk` incrementally as prices are added or removed.
6. **Massive Allocation Churn on Ticks**
   * **Location**: `DomEngine.cs` / `DomRowData.cs`
   * **Symptom**:
     * `UpdateLastTrade` instantiates a new `Dictionary` on every trade message.
     * `DetectDecimalPlaces` performs string conversions (`price.ToString("G29")`, `IndexOf`) on every L1/L2 message.
     * `_latencyMeasurements` allocates a queue node for every incoming message.
   * **Fix**: Recalculate decimal places arithmetically or cache the instrument configuration once, and sample latencies instead of tracking every tick.
7. **Conflation Mismatch**
   * **Location**: `DomEngine.cs:117-121`
   * **Symptom**: The UI conflation stream applies `Buffer(16ms).Last()`, but the upstream publisher is already throttled to 100ms by the `_snapshotTimer`. This incurs Rx overhead without achieving true conflation.

---

## 4. Modern .NET 10 & Hardware Optimization Roadmap

### GPU Rendering vs. CUDA Compute
* **CUDA (Compute Unified Device Architecture)**: CUDA is **unsuited** for the DOM engine. Market data is small, and transferring ticks across the PCIe bus takes 5–20 microseconds (introducing severe latency). Furthermore, GPUs are inefficient at sequential, branching tree traversals like those needed to update order books.
* **GPU-Accelerated Graphics**: Rendering *is* the primary bottleneck. Standard WPF calculates layout and bindings on the CPU.
  * **Roadmap Recommendation**: Migrate the DOM rendering layout to a custom-drawn canvas using **SkiaSharp** (backed by OpenGL/Vulkan/Direct3D) or **Direct2D (via D3DImage)**.
  * **Result**: Renders the DOM in under 1ms, reduces UI thread CPU usage to <2%, and allows fluid redraws on 144Hz+ monitors.

### .NET 10 Runtime Improvements
* Upgrading the client to .NET 10 yields performance gains from Dynamic PGO, loop vectorization, and reduced allocations in WPF layout and event routing.
* Target framework rules remain unchanged: NinjaTrader components must continue to target `.NET Framework 4.8`, utilizing `.NET Standard 2.0` as the communication contract bridge.

---

## 5. Scaling to Parallel Multi-DOM Windows

To support multiple parallel DOM windows without UI thread freeze, we must move away from the current single-threaded design toward a **Single-Engine Multi-Consumer (SEMC)** model with **Multi-Threaded STA Apartments**.

```mermaid
graph TD
    Data[NT8 Direct Feed] -->|Single Stream| Mgr[DomEngineManager: Singleton]
    Mgr -->|Instance per symbol| SharedEngine[Shared DomEngine: ES]
    
    subgraph Thread1 [STA UI Thread 1]
        VM1[DomViewModel 1] <-- Pulls 60fps --- SharedEngine
        Win1[DOM Window 1: WPF/Skia]
    end

    subgraph Thread2 [STA UI Thread 2]
        VM2[DomViewModel 2] <-- Pulls 60fps --- SharedEngine
        Win2[DOM Window 2: WPF/Skia]
    end
```

### Core Parallel Scaling Design

1. **Singleton Engine Manager (Engine Reuse)**
   * Avoid creating a dedicated `DomEngine` per window. Use a central manager to maintain a single engine per symbol. If the user opens multiple windows for the same symbol (e.g., on different monitors), they all bind to the same background processor, reducing calculation overhead to $O(1)$.
2. **Multi-Threaded WPF STA Threads**
   * Launch each DOM window on a dedicated OS thread configured as a Single-Threaded Apartment (STA).
   * This distributes layout, drawing, and converter execution across multiple CPU cores, preventing one lagging window from freezing the rest of the application.
3. **Pull-Based Decoupled UI Loop**
   * Decouple the UI rendering frame rate from the incoming market data tick rate. The background engine updates the double-buffered snapshot at tick speed, and the UI threads poll and render the latest state at a locked 30 or 60 FPS.
4. **IPC Consolidation**
   * Replace the TCP loopback server with a **Duplex Named Pipe** or a dedicated **MMF Event Ring Buffer** using binary serializers like **Protobuf** or **MessagePack**. This removes networking socket overhead, eliminates firewall blocks, and decreases serialization lag.
