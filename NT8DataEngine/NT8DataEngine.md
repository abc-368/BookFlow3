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

*   **Global Channel Management**: It initializes and manages the primary communication channels:
    *   A `ControlPipeServer` for handling commands and requests from external clients.
    *   A `SharedRingBuffer` for broadcasting high-volume market data globally.
    *   A `NamedPipeServerStream` for sending real-time account and order events.
*   **Ticker Management**: It assigns a unique `TickerId` to each instrument that a `BookFlowIndi` instance is attached to. This allows data from multiple instruments to be multiplexed over the global data channel.
*   **Account and Order Management**: It subscribes to NinjaTrader's core account events (`OnOrderUpdate`, `OnExecutionUpdate`, `OnPositionUpdate`) and broadcasts these events to connected clients.
*   **Command Handling**: It processes incoming commands from the control pipe, such as submitting, canceling, or modifying orders, and querying account status.

#### `BookFlowIndi.cs`

This is the NinjaTrader indicator that users attach to a chart. Its primary responsibilities are:

*   **Data Capture**: It subscribes to NinjaTrader's `OnMarketData` (Level 1) and `OnMarketDepth` (Level 2) events for the specific instrument it is attached to.
*   **Registration**: Upon initialization, it registers the instrument with the `BookFlowAddOn` to obtain a unique `TickerId`.
*   **Data Formatting**: It converts the raw NinjaTrader market data into the `UnifiedMarketDataMessage` format defined in `SharedLibrary.Standard`.
*   **Publishing**: It writes the formatted data messages into the global `SharedRingBuffer` managed by the `BookFlowAddOn`.

#### `DataEngineManager.cs`

This static class manages instances of `InstrumentDataManager`. It ensures that only one `InstrumentDataManager` is created per instrument, even if the `BookFlowIndi` is applied to multiple charts for the same instrument. It uses a reference counting mechanism to properly dispose of managers when they are no longer needed.

#### `InstrumentDataManager.cs`

This class is responsible for managing the communication channels for a *single* instrument. While the current implementation has moved towards a global, multiplexed channel model in `BookFlowAddOn`, this class retains the logic for a per-instrument channel setup. It encapsulates a `SharedRingBuffer` and a `ControlPipeServer` for a specific instrument, handling data serialization and command processing at the instrument level.

### 2.2. `SharedLibrary.Standard` Project

This project provides the common language for the `NT8DataEngine` and any external client application.

#### `Contracts/DataContracts.cs`

This file is the most critical part of the shared library. It defines all the data structures and enumerations that are passed between the NT8 engine and the client. The use of `StructLayout` with explicit field offsets is crucial for ensuring that the data structures have the exact same memory layout in both the .NET Framework (NT8) and the client's .NET environment (e.g., .NET 8), which is essential for high-speed IPC via shared memory.

#### `IPC/SharedRingBuffer.cs`

This class implements a high-performance, lock-free ring buffer using a memory-mapped file (`MemoryMappedFile`). It is designed for one-way, high-throughput communication of market data from the producer (NT8) to the consumer (client). A `Semaphore` is used to signal data availability, allowing the consumer to wait efficiently without busy-spinning.

#### `IPC/ControlPipe.cs`

This class provides a bi-directional command and control channel using `NamedPipeServerStream` and `NamedPipeClientStream`. It is used for lower-frequency, message-based communication, such as:
*   Sending trading commands from the client to NT8.
*   Requesting account or position snapshots.
*   Receiving status updates and responses from NT8.
The implementation includes custom, lightweight JSON serialization/deserialization to minimize overhead.

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

### `ControlMessage`

This is a class-based message used for the `ControlPipe`. It contains properties for the request type, instrument name, and nested command objects like `OrderCommand`. Since it's transmitted over a named pipe with serialization, it does not require the strict memory layout of the shared memory structs.

## 4. Communication Channels

The system employs two distinct IPC mechanisms, each suited for a different purpose.

### `SharedRingBuffer` (for Market Data)

*   **Mechanism**: A circular buffer implemented in a memory-mapped file.
*   **Purpose**: High-speed, one-way streaming of `UnifiedMarketDataMessage` structs from NT8 to the client.
*   **Characteristics**:
    *   **Low Latency**: Writing a struct to shared memory is extremely fast, involving a simple memory copy.
    *   **Lock-Free**: The design uses atomic operations on head and tail pointers, avoiding the need for locks in the hot path.
    *   **Efficient Signaling**: A `Semaphore` is used to wake up the consumer thread only when new data is available.
    *   **Global Channel**: A single, large ring buffer (`BookFlow_Data_Global`) is used for all instruments, with the `TickerId` field used to differentiate them.

### `ControlPipe` (for Commands and Events)

*   **Mechanism**: A `NamedPipe` that provides a message-based, bi-directional communication channel.
*   **Purpose**:
    *   Sending commands from the client to NT8 (e.g., submit order).
    *   Receiving responses and status messages from NT8.
    *   Broadcasting lower-frequency events like account updates.
*   **Characteristics**:
    *   **Reliable**: Named pipes provide guaranteed message delivery.
    *   **Bi-directional**: Allows for request/response patterns.
    *   **Flexible**: The use of a simple JSON-like text protocol allows for more complex and variable-sized messages compared to the fixed-size structs in the ring buffer.
    *   **Global Channels**: The system uses a global control pipe (`BookFlow_Control_Global`) and a global event pipe (`BookFlow_Event_Global`).

---

# Implementation Plan: Streaming & IPC Enhancements

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
