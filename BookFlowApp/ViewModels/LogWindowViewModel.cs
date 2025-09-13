using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace BookFlow.App.ViewModels
{
    public class LogWindowViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly DispatcherTimer _updateTimer;
        private readonly ConcurrentQueue<LogEntry> _logQueue;
        private readonly StringBuilder _logBuffer;
        private readonly object _bufferLock = new();
        private volatile bool _disposed = false;

        private const int MaxBufferLines = 10000;
        private const int UpdateIntervalMs = 100; // NT8TestClient optimization: 100ms updates
        private const int MaxDisplayLines = 5000;

        private string _logText = "";
        private int _totalLines;
        private int _bufferCount;
        private string _logRate = "0";
        private string _lastUpdateTime = "";
        private long _lastLogCount;
        private DateTime _lastRateCalculation = DateTime.UtcNow;
        private bool _isLoggingEnabled = false;

        public LogWindowViewModel()
        {
            _logQueue = new ConcurrentQueue<LogEntry>();
            _logBuffer = new StringBuilder(MaxBufferLines * 100); // Pre-allocate capacity

            // Set up timer for UI updates (NT8TestClient pattern: batch updates every 100ms)
            _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(UpdateIntervalMs)
            };
            _updateTimer.Tick += OnUpdateTimer;
            // Don't start timer by default - will be started when logging is enabled
        }

        #region Properties

        public string LogText
        {
            get => _logText;
            private set
            {
                if (_logText != value)
                {
                    _logText = value;
                    OnPropertyChanged();
                }
            }
        }

        public int TotalLines
        {
            get => _totalLines;
            private set
            {
                if (_totalLines != value)
                {
                    _totalLines = value;
                    OnPropertyChanged();
                }
            }
        }

        public int BufferCount
        {
            get => _bufferCount;
            private set
            {
                if (_bufferCount != value)
                {
                    _bufferCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LogRate
        {
            get => _logRate;
            private set
            {
                if (_logRate != value)
                {
                    _logRate = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LastUpdateTime
        {
            get => _lastUpdateTime;
            private set
            {
                if (_lastUpdateTime != value)
                {
                    _lastUpdateTime = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsLoggingEnabled
        {
            get => _isLoggingEnabled;
            set
            {
                if (_isLoggingEnabled != value)
                {
                    _isLoggingEnabled = value;
                    OnPropertyChanged();
                    
                    if (value)
                    {
                        // Start logging
                        _updateTimer.Start();
                    }
                    else
                    {
                        // Stop logging and clear data
                        _updateTimer.Stop();
                        // Clear any remaining queued messages aggressively
                        while (_logQueue.TryDequeue(out _)) { }
                        ClearLogs();
                    }
                }
            }
        }

        #endregion

        #region Public Methods

        public void LogMessage(string source, string level, string message)
        {
            if (_disposed || !_isLoggingEnabled) 
            {
                // Debug: Log when messages are being blocked
                System.Diagnostics.Debug.WriteLine($"[LogMessage] BLOCKED - Disposed: {_disposed}, Enabled: {_isLoggingEnabled}, Source: {source}");
                return;
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Source = source,
                Level = level,
                Message = message
            };
            
            // Debug: Log when messages are being queued
            System.Diagnostics.Debug.WriteLine($"[LogMessage] QUEUED - Source: {source}, Level: {level}, Time: {entry.Timestamp:HH:mm:ss.fff}");

            _logQueue.Enqueue(entry);
        }

        public void LogDomUpdate(string instrumentName, string updateType, string details)
        {
            if (!_isLoggingEnabled) return;
            LogMessage($"DOM-{instrumentName}", updateType, details);
        }

        public void LogEngineEvent(string eventName, string details)
        {
            if (!_isLoggingEnabled) return;
            LogMessage("Engine", "EVENT", $"{eventName}: {details}");
        }

        public void LogPerformance(string metric, double value, string unit = "")
        {
            if (!_isLoggingEnabled) return;
            LogMessage("Performance", "PERF", $"{metric}: {value:F2} {unit}");
        }

        public void ClearLogs()
        {
            lock (_bufferLock)
            {
                _logBuffer.Clear();
                
                // Clear the queue
                while (_logQueue.TryDequeue(out _)) { }
                
                TotalLines = 0;
                BufferCount = 0;
                LogText = "";
                LastUpdateTime = DateTime.Now.ToString("HH:mm:ss.fff");
            }
        }

        #endregion

        #region Private Methods

        private void OnUpdateTimer(object? sender, EventArgs e)
        {
            if (_disposed) return;

            try
            {
                ProcessLogQueue();
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                // Prevent timer exceptions from crashing the app
                Debug.WriteLine($"LogWindowViewModel timer error: {ex.Message}");
            }
        }

        private void ProcessLogQueue()
        {
            // Don't process queue if logging is disabled
            if (!_isLoggingEnabled || _logQueue.IsEmpty) return;

            var processedCount = 0;
            var maxProcessPerUpdate = 1000; // Limit processing to prevent UI blocking
            var hasNewLogs = false;

            lock (_bufferLock)
            {
                // Process queued log entries
                while (_logQueue.TryDequeue(out var entry) && processedCount < maxProcessPerUpdate)
                {
                    AppendLogEntry(entry);
                    processedCount++;
                    hasNewLogs = true;
                }

                // Update UI only if we have new logs (NT8TestClient optimization)
                if (hasNewLogs)
                {
                    // Trim buffer if it gets too large
                    TrimBufferIfNeeded();
                    
                    // Update display text
                    LogText = _logBuffer.ToString();
                    BufferCount = _logQueue.Count;
                    LastUpdateTime = DateTime.Now.ToString("HH:mm:ss.fff");
                }
            }
        }

        private void AppendLogEntry(LogEntry entry)
        {
            var timestamp = entry.Timestamp.ToString("HH:mm:ss.fff");
            var levelColor = GetLevelPrefix(entry.Level);
            
            _logBuffer.AppendLine($"[{timestamp}] {levelColor} [{entry.Source}] {entry.Message}");
            TotalLines++;
        }

        private void TrimBufferIfNeeded()
        {
            if (TotalLines <= MaxDisplayLines) return;

            // Trim oldest lines to maintain performance
            var text = _logBuffer.ToString();
            var lines = text.Split('\n');
            
            if (lines.Length > MaxDisplayLines)
            {
                var keepLines = lines.Length - MaxDisplayLines + (MaxDisplayLines / 4); // Keep 25% buffer
                var newText = string.Join("\n", lines[keepLines..]);
                
                _logBuffer.Clear();
                _logBuffer.Append(newText);
                TotalLines = MaxDisplayLines - (MaxDisplayLines / 4);
            }
        }

        private void UpdateStatistics()
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRateCalculation).TotalSeconds;
            
            if (elapsed >= 1.0) // Update rate every second
            {
                var currentCount = TotalLines;
                var newLogs = currentCount - _lastLogCount;
                var rate = elapsed > 0 ? newLogs / elapsed : 0;
                
                LogRate = rate.ToString("F1");
                _lastLogCount = currentCount;
                _lastRateCalculation = now;
            }
        }

        private static string GetLevelPrefix(string level)
        {
            return level.ToUpper() switch
            {
                "ERROR" => "ERR",
                "WARN" => "WRN", 
                "WARNING" => "WRN",
                "INFO" => "INF",
                "DEBUG" => "DBG",
                "PERF" => "PRF",
                "EVENT" => "EVT",
                _ => level.Length > 3 ? level[..3].ToUpper() : level.ToUpper()
            };
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _updateTimer?.Stop();
            if (_updateTimer != null)
                _updateTimer.Tick -= OnUpdateTimer;

            GC.SuppressFinalize(this);
        }

        #endregion

        #region Nested Types

        private struct LogEntry
        {
            public DateTime Timestamp;
            public string Source;
            public string Level;
            public string Message;
        }

        #endregion
    }
}