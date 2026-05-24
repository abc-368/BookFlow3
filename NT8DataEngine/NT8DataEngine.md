# NT8DataEngine Architecture

This document outlines the architecture of the `NT8DataEngine` and `SharedLibrary.Standard` projects, which together form a system for capturing, processing, and transmitting NinjaTrader 8 market data to external applications.

## 1. Overview

The system is designed to run within the NinjaTrader 8 platform and expose real-time market data and trading capabilities to external processes. It consists of two main projects:

*   **`NT8DataEngine`**: A .NET Framework 4.8 class library that integrates directly with NinjaTrader 8. It contains the NinjaScript indicators and add-ons responsible for capturing data and managing communication.
*   **`SharedLibrary.Standard`**: A .NET Standard 2.0 library containing the data contracts (structs and enums) and inter-process communication (IPC) logic. This shared library ensures that both the NT8 engine and external client applications use the same data structures and communication protocols.

The primary goal is to provide a high-performance, low-latency bridge between NinjaTrader 8 and other applications, enabling advanced analytics, custom user interfaces, and automated trading strategies.

## 2. Architecture

The architecture is composed of several key components that work together to stream data and commands.

### 2.1. `NT8DataEngine` Project

This project is the core of the data engine, living inside the NT8 process.

#### `BookFlowAddOn.cs`

This class acts as the central hub for the entire system within NinjaTrader. It is implemented as a singleton and performs several critical functions:

> **Status:** This section was updated to current reality. The original design (a regex-JSON `ControlPipeServer` plus a separate `NamedPipeServerStream`/TCP event channel) has been **replaced by a single WCF duplex `netNamedPipe` service** hosted in the AddOn. The `SharedRingBuffer` for market data is unchanged.

*   **Global Channel Management**: It initializes and manages the primary communication channels:
    *   A **WCF duplex service** (`IBookFlowService` request/reply + `IBookFlowCallback` event push, `DataContractSerializer` wire format) for all transactional traffic — orders, snapshot/portfolio requests, account listing, and pushed order/exec/position/account events.
    *   A `SharedRingBuffer` for broadcasting high-volume market data globally (fed by a single drain thread that coalesces from a `ConcurrentQueue`).
*   **Ticker Management**: It assigns a **stable, session-monotonic** `TickerId` to each instrument a `BookFlowIndi` registers (deduped by instrument name; never recycled, so an open client window can't be re-pointed). This multiplexes multiple instruments over the global data channel.
*   **Account and Order Management**: It subscribes to NinjaTrader's core account events (`OnOrderUpdate`, `OnExecutionUpdate`, `OnPositionUpdate`) and fans them out to all connected client callbacks. It also maintains a versioned authoritative portfolio snapshot for reconciliation.
*   **Command Handling**: It processes incoming WCF service calls — submitting, canceling, or modifying orders, querying account status, and serving DOM/portfolio snapshots.

#### `BookFlowIndi.cs`

This is the NinjaTrader indicator that users attach to a chart. Its primary responsibilities are:

*   **Data Capture**: It subscribes to NinjaTrader's `OnMarketData` (Level 1) and `OnMarketDepth` (Level 2) events for the specific instrument it is attached to.
*   **Registration**: Upon initialization, it registers the instrument with the `BookFlowAddOn` to obtain a unique `TickerId`.
*   **Data Formatting**: It converts the raw NinjaTrader market data into the `UnifiedMarketDataMessage` format defined in `SharedLibrary.Standard`.
*   **Publishing**: It writes the formatted data messages into the global `SharedRingBuffer` managed by the `BookFlowAddOn`.

#### `DataEngineManager.cs` / `InstrumentDataManager.cs` — *removed*

These per-instrument manager classes were **deleted**. The system uses a single global, multiplexed MMF channel (keyed by `TickerId`) managed directly by `BookFlowAddOn`; the per-instrument channel model and its reference-counting were dead code (and `InstrumentDataManager` truncated event payloads).

### 2.2. `SharedLibrary.Standard` Project

This project provides the common language for the `NT8DataEngine` and any external client application.

#### `Contracts/DataContracts.cs`

This file is the most critical part of the shared library. It defines all the data structures and enumerations that are passed between the NT8 engine and the client. The use of `StructLayout` with explicit field offsets is crucial for ensuring that the data structures have the exact same memory layout in both the .NET Framework (NT8) and the client's .NET environment (e.g., .NET 8), which is essential for high-speed IPC via shared memory.

#### `IPC/SharedRingBuffer.cs`

This class implements a high-performance, lock-free ring buffer using a memory-mapped file (`MemoryMappedFile`). It is designed for one-way, high-throughput communication of market data from the producer (NT8) to the consumer (client). A `Semaphore` is used to signal data availability, allowing the consumer to wait efficiently without busy-spinning.

#### `Service/IBookFlowService.cs` + `IBookFlowCallback.cs` (replaced `IPC/ControlPipe.cs`)

The regex-JSON `ControlPipe.cs` was **removed**. Command/control and event delivery now run over a **WCF duplex service** (`netNamedPipe`):
*   `IBookFlowService` (request/reply, called by the client): register, submit/cancel orders, `RequestDomSnapshot`, `RequestPortfolioState`, list accounts.
*   `IBookFlowCallback` (pushed by the server): order / execution / position / account updates, connection status, portfolio-state deltas with a monotonic `Version`.

Hosted by `Service/BookFlowServiceHost.cs` in the AddOn; consumed by `BookFlowApp/Services/BookFlowServiceClient.cs`. Serialized with `DataContractSerializer` (typed contracts, no hand-rolled JSON parsing).

## 3. Data Structures

The core data structures are defined in `DataContracts.cs` and are designed for performance and compatibility.

### `UnifiedMarketDataMessage`

This 80-byte struct is the workhorse for all high-frequency market data. It uses `FieldOffset` to create a union-like structure, where the same memory locations can be interpreted differently depending on the message category (L1 vs. L2).

*   **`Category`**: An enum (`MessageCategory`) that specifies whether the message is Level 1 data, Level 2 data, or an event.
*   **`MarketDataType`**: Specifies the type of data (e.g., Bid, Ask, Last for L1; Add, Update, Remove for L2).
*   **`TickerId`**: The unique ID for the instrument, allowing the client to demultiplex the data stream.
*   **`Price`, `Volume`**: The core market data values.
*   **`AskPrice`, `BidPrice` / `Position`**: An overlapping field used for L1 bid/ask prices or the L2 book position.
*   **Timestamps**: Includes the original data timestamp, the time it was received by the NT8 process, and the time it was enqueued for IPC, allowing for detailed latency analysis.
*   **`Sequence`**: A monotonically increasing sequence number for detecting message loss.

### `UnifiedEventMessage`

This 128-byte struct is used for broadcasting asynchronous events from NT8, such as order updates, position changes, and connection status. It also uses an overlapping layout to accommodate different event payloads within a single structure.

*   **`EventType`**: Specifies the type of event (e.g., `PositionUpdate`, `OrderUpdate`).
*   **`StringData0` - `StringData63`**: A 64-byte fixed-size buffer for string payloads like order IDs or instrument names.
*   **Overlaid Fields**: The latter part of the struct contains numerous overlapping fields (`AveragePrice`, `Quantity`, `LimitPrice`, `OrderState`, etc.) that are populated based on the `EventType`.

### `ControlMessage` / `UnifiedEventMessage` — superseded by WCF contracts

The hand-rolled `ControlMessage` (for the old JSON `ControlPipe`) and the 128-byte `UnifiedEventMessage` event struct are superseded by the typed WCF `[DataContract]` request/reply and callback messages. `OrderCommand` and the other transactional payloads are now exchanged as data contracts over the duplex service; only the high-frequency `UnifiedMarketDataMessage` still uses the strict `StructLayout` for zero-serialization MMF transport.

## 4. Communication Channels

The system employs two distinct IPC mechanisms, each suited for a different purpose.

### `SharedRingBuffer` (for Market Data)

*   **Mechanism**: A circular buffer implemented in a memory-mapped file.
*   **Purpose**: High-speed, one-way streaming of `UnifiedMarketDataMessage` structs from NT8 to the client.
*   **Characteristics**:
    *   **Low Latency**: Writing a struct to shared memory is extremely fast, involving a simple memory copy.
    *   **SPSC, lock-free**: A single drain thread in the AddOn writes the ring (coalescing from a `ConcurrentQueue`) and a single client thread reads it. Memory barriers fence the struct write before the head pointer is published; no locks or CAS are needed in a single-producer/single-consumer ring.
    *   **Efficient Signaling**: A `Semaphore` is used to wake up the consumer thread only when new data is available.
    *   **Global Channel**: A single, large ring buffer (`BookFlow_Data_Global`) is used for all instruments, with the `TickerId` field used to differentiate them.

### WCF duplex service (for Commands and Events)

*   **Mechanism**: A WCF duplex service over `netNamedPipeBinding`, hosted in `BookFlowAddOn`. `IBookFlowService` is the request/reply contract; `IBookFlowCallback` is the server→client push contract.
*   **Purpose**:
    *   Sending commands from the client to NT8 (submit/cancel orders, flatten, request DOM/portfolio snapshots).
    *   Receiving typed responses (including `NtOrderId` correlation) from NT8.
    *   Pushing order / execution / position / account events and versioned portfolio deltas to every connected client.
*   **Characteristics**:
    *   **Typed & reliable**: `DataContractSerializer` contracts — no hand-rolled JSON parsing; in-order on localhost named pipes.
    *   **Duplex**: One channel for both request/response and server-pushed events (no separate event socket).
    *   **Multi-client**: each WPF window opens its own callback channel; the host broadcasts to all live callbacks and drops faulted ones.

> The earlier global control pipe (`BookFlow_Control_Global`) and global event pipe (`BookFlow_Event_Global`) no longer exist.

---

# Implementation Plan: Streaming & IPC Enhancements

> **Status: IMPLEMENTED (historical plan).** Everything below shipped across Increments 1–3: SPSC ring (single drain thread + memory barriers), full AddOn lifecycle/cleanup on `State.Terminated`, and migration of the order/event channel off TCP loopback onto the WCF duplex `netNamedPipe` service. One divergence from the sketch: the multi-producer `lock` on `TryWrite` was avoided entirely by coalescing all indicator writes onto a single drain thread (true SPSC), so the ring needs no write lock. Retained below for design rationale.

This plan outlines critical improvements to the NinjaTrader 8 data streaming and IPC layer. It resolves concurrency bugs, eliminates resource/thread leaks, and optimizes communication channels.

## 1. Goal Description
The current implementation of the BookFlow data streaming engine contains critical thread-safety bugs on the shared memory ring buffer, memory leaks in NinjaTrader on assembly reload due to missing lifecycle management, and performance bottlenecks in the event reporting system due to loopback TCP and JSON serialization.

This change aims to:
1. Guarantee thread-safe multi-producer streaming of Level 1 and Level 2 market data.
2. Clean up all threads, event subscriptions, and unmanaged resources during AddOn shutdown to prevent memory leaks and handle reload cycles gracefully.
3. Migrate the order and position update channel from a TCP loopback socket using text-based JSON to a duplex Named Pipe or binary Event buffer.

---

## 2. Critical User/Agent Review Required

> [!WARNING]
> **Thread-Safety on Memory-Mapped Accessors**
> .NET Standard `MemoryMappedViewAccessor` does not support direct atomic operations (like `Interlocked`) on its fields. 
> To guarantee thread-safe writes from multiple indicators:
> * **Option A (Recommended)**: Use a lightweight `lock` on a shared object (e.g., in `BookFlowAddOn.WriteToGlobalChannel`) before calling `TryWrite`. Since writing a binary struct to a memory view is an in-memory copy of 80 bytes (sub-microsecond execution time), lock contention is mathematically negligible.
> * **Option B**: Use `unsafe` blocks to acquire pointers to unmanaged memory and execute `Interlocked.CompareExchange` on the raw memory address.
> We recommend **Option A** for simplicity, readability, and platform independence, with minimal performance overhead.

---

## 3. Proposed Changes

### Shared Library Component (`SharedLibrary.Standard`)

#### [MODIFY] [SharedRingBuffer.cs](file:///c:/Users/master/source/repos/BookFlow5/SharedLibrary.Standard/IPC/SharedRingBuffer.cs)
* Add a `lock` mechanism or atomic memory pointer adjustments on `TryWrite` to prevent concurrent write pointers from clobbering each other.
* Correct the memory barrier sequence on `TryRead` to ensure data structure bytes are fully read *before* the tail pointer is advanced and exposed to the writer.

---

### NinjaTrader 8 Data Engine Component (`BookFlow.NT8DataEngine`)

#### [MODIFY] [BookFlowAddOn.cs](file:///c:/Users/master/source/repos/BookFlow5/NT8DataEngine/NinjaTrader/BookFlowAddOn.cs)
* Implement `OnStateChange` to detect indicator/AddOn shutdown (`State.Terminated`).
* Add a thorough `Dispose` method to:
  * Unsubscribe from all `Account.OrderUpdate`, `Account.ExecutionUpdate`, and `Account.PositionUpdate` events.
  * Stop background threads (`_eventWriterThread`, `_eventServerThread`) cleanly using volatile flags and signals.
  * Dispose the Named Pipe control server and Shared Memory buffers.
* Eliminate the loopback TCP socket server and replace it with a dedicated Event Named Pipe (`BookFlow_Event_Global`) or push events directly via the duplex Control Pipe.

#### [MODIFY] [BookFlowIndi.cs](file:///c:/Users/master/source/repos/BookFlow5/NT8DataEngine/NinjaTrader/BookFlowIndi.cs)
* Improve telemetry/logging outputs. Ensure exception messages are tracked.

---

### Client Component (`BookFlow.App`)

#### [MODIFY] [NT8DirectDataFeed.cs](file:///c:/Users/master/source/repos/BookFlow5/BookFlowApp/Services/NT8DirectDataFeed.cs)
* Modify connection logic to bind to the new event Named Pipe instead of the TCP loopback port `38755`.
* Ensure clean client shutdown is performed on disconnect.

---

## 4. Detailed Code Sketches & Rationale

### A. SharedRingBuffer Concurrency Guard
Since multiple indicator instances write to the singleton AddOn channel concurrently, we must guard the write-side pointer increment.

```csharp
// Inside SharedRingBuffer.cs
private readonly object _writeLock = new object();

public bool TryWrite(ref UnifiedMarketDataMessage message)
{
    lock (_writeLock)
    {
        long head = _accessor.ReadInt64(HeadPosition);
        long tail = _accessor.ReadInt64(TailPosition);
        long nextHead = (head + 1) % _capacity;
        
        if (nextHead == tail) return false; // Buffer full
        
        long position = _bufferOffset + head * _messageSize;
        _accessor.Write(position, ref message);
        
        Thread.MemoryBarrier(); // Fence the message write
        _accessor.Write(HeadPosition, nextHead); // Publish new head
        return true;
    }
}
```

For the read path:
```csharp
public bool TryRead(out UnifiedMarketDataMessage message)
{
    long head = _accessor.ReadInt64(HeadPosition);
    long tail = _accessor.ReadInt64(TailPosition);
    
    if (head == tail)
    {
        message = default;
        return false;
    }
    
    long position = _bufferOffset + tail * _messageSize;
    _accessor.Read(position, out message);
    
    long nextTail = (tail + 1) % _capacity;
    
    Thread.MemoryBarrier(); // Guarantee message bytes are copied out BEFORE advancing tail
    _accessor.Write(TailPosition, nextTail);
    return true;
}
```

### B. AddOn Lifecycle and Cleanup
Add lifecycle handlers in `BookFlowAddOn.cs` to prevent resource leaks during NT8 recompile/reload.

```csharp
// Inside BookFlowAddOn.cs

protected override void OnStateChange()
{
    if (State == State.SetDefaults)
    {
        Description = "BookFlow AddOn for order management and events";
        Name = "BookFlowAddOn";
    }
    else if (State == State.Terminated)
    {
        Dispose();
    }
}

public void Dispose()
{
    LogMsg("Disposing BookFlow AddOn...");
    
    // 1. Unsubscribe from NT8 events to avoid leaking callbacks
    UnsubscribeFromAccountEvents();
    
    // 2. Shut down thread loops
    _eventServerRunning = false;
    _eventSignal.Set(); // Wake writer loop to exit
    
    // 3. Close pipes and sockets
    try { _controlPipeServer?.Dispose(); } catch {}
    try { _globalDataChannel?.Dispose(); } catch {}
    try { CloseEventClient(); } catch {}
    try { _eventListener?.Stop(); } catch {}
    
    // 4. Null instance
    _instance = null;
}

private void UnsubscribeFromAccountEvents()
{
    foreach (var account in Account.All)
    {
        if (account.Name == "Backtest") continue;
        account.OrderUpdate -= OnOrderUpdate;
        account.ExecutionUpdate -= OnExecutionUpdate;
        account.PositionUpdate -= OnPositionUpdate;
    }
}
```

---

## 5. Verification Plan

### Automated Verification
* Write an integration unit test in a temporary project/script that spins up 5 concurrent threads executing `TryWrite` on a `SharedRingBuffer`.
* Verify that:
  1. No pointer corruption occurs.
  2. The read side consumes exactly the same number of messages written without any sequence gaps.
  3. Writing to a full buffer returns `false` safely.

### Manual Verification in NinjaTrader 8
1. Compile the modified `NT8DataEngine` and copy assemblies to `Documents/NinjaTrader 8/bin/Custom`.
2. Open NinjaTrader 8, apply `BookFlowIndi` to multiple charts (e.g. ES and NQ) to trigger concurrent writes.
3. Open the WPF Client App and connect. Monitor incoming L1/L2 data for both symbols.
4. Verify sequence continuity in the client log to ensure zero dropped messages.
5. Compile scripts inside NinjaTrader (F5) multiple times and verify that the output window shows clean teardown of channels and no socket binding errors (`Address already in use`).
