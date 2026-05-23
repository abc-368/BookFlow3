using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using BookFlow.Shared.Contracts;

namespace BookFlow.Shared.IPC
{
    public class SharedRingBuffer : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly Semaphore _dataAvailableSemaphore;
        private readonly int _capacity;
        private readonly int _messageSize;
        private readonly long _bufferOffset = 16;
        private const int HeadPosition = 0;
        private const int TailPosition = 8;

        public SharedRingBuffer(string mapName, int capacity)
        {
            _capacity = capacity;
            _messageSize = Marshal.SizeOf<UnifiedMarketDataMessage>();
            long totalSize = _bufferOffset + (long)capacity * _messageSize;
            _mmf = MemoryMappedFile.CreateOrOpen(mapName, totalSize);
            _accessor = _mmf.CreateViewAccessor();
            _dataAvailableSemaphore = new Semaphore(0, int.MaxValue, $"{mapName}_Semaphore");
        }

        public bool TryWrite(ref UnifiedMarketDataMessage message)
        {
            long head = _accessor.ReadInt64(HeadPosition);
            long tail = _accessor.ReadInt64(TailPosition);
            long nextHead = (head + 1) % _capacity;
            if (nextHead == tail) return false; // full
            long position = _bufferOffset + head * _messageSize;
            _accessor.Write(position, ref message);
            Thread.MemoryBarrier();
            _accessor.Write(HeadPosition, nextHead);
            return true;
        }

        public bool TryRead(out UnifiedMarketDataMessage message)
        {
            long head = _accessor.ReadInt64(HeadPosition);
            long tail = _accessor.ReadInt64(TailPosition);
            if (head == tail) { message = default; return false; }
            long position = _bufferOffset + tail * _messageSize;
            _accessor.Read(position, out message);
            Thread.MemoryBarrier();
            long nextTail = (tail + 1) % _capacity;
            _accessor.Write(TailPosition, nextTail);
            return true;
        }

        public void SignalDataAvailable() => _dataAvailableSemaphore.Release();
        public void WaitForData() => _dataAvailableSemaphore.WaitOne();

        /// <summary>
        /// Approximate occupancy [0,1] of the ring. Producer-side diagnostic only;
        /// reads head/tail without synchronization, so the value is a snapshot.
        /// </summary>
        public double FillRatio
        {
            get
            {
                long head = _accessor.ReadInt64(HeadPosition);
                long tail = _accessor.ReadInt64(TailPosition);
                long used = head - tail;
                if (used < 0) used += _capacity;
                return _capacity > 0 ? (double)used / _capacity : 0.0;
            }
        }

        public void Dispose()
        {
            _accessor.Dispose();
            _mmf.Dispose();
            _dataAvailableSemaphore.Dispose();
        }
    }
}
