using System;
using System.Collections.Generic;

namespace BookFlow.NT8DataEngine.NinjaTrader;

/// <summary>
/// A static class to manage multiple InstrumentDataManager instances.
/// This allows the NT8 indicator to support multiple instruments simultaneously.
/// </summary>
public static class DataEngineManager
{
    private static readonly Dictionary<string, InstrumentDataManager> _dataManagers = new();
    private static readonly Dictionary<string, int> _referenceCount = new();
    private static readonly object _lock = new object();

    public static void AddInstrument(string instrumentName, double tickSize, double pointValue)
    {
        AddInstrument(instrumentName, tickSize, pointValue, null, null);
    }

    public static void AddInstrument(string instrumentName, double tickSize, double pointValue, string sharedMemoryName, string controlPipeName)
    {
        lock (_lock)
        {
            try
            {
                if (_dataManagers.ContainsKey(instrumentName))
                {
                    // Increment reference count for existing manager
                    _referenceCount[instrumentName]++;
                    System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Reusing existing manager for {instrumentName}, incrementing reference count to {_referenceCount[instrumentName]}");
                }
                else
                {
                    // Create new manager with both shared memory and control pipe names
                    InstrumentDataManager manager;
                    if (string.IsNullOrEmpty(sharedMemoryName) || string.IsNullOrEmpty(controlPipeName))
                    {
                        // Use default constructor if either is null
                        manager = new InstrumentDataManager(instrumentName, tickSize, pointValue);
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Created default manager with auto-generated channels");
                    }
                    else
                    {
                        // Use custom constructor with both parameters
                        manager = new InstrumentDataManager(instrumentName, tickSize, pointValue, sharedMemoryName, controlPipeName);
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Created custom manager with specified channels");
                    }
                    
                    _dataManagers.Add(instrumentName, manager);
                    _referenceCount.Add(instrumentName, 1);
                    System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Created new manager for {instrumentName} with reference count 1");
                    
                    if (!string.IsNullOrEmpty(sharedMemoryName))
                    {
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Using custom SharedMemory: {sharedMemoryName}");
                    }
                    if (!string.IsNullOrEmpty(controlPipeName))
                    {
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Using custom ControlPipe: {controlPipeName}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DataEngineManager] ERROR adding instrument {instrumentName}: {ex.Message}");
                throw;
            }
        }
    }

    public static void AddInstrument(string instrumentName, double tickSize, double pointValue, string sharedMemoryName, bool useExternalControlPipe)
    {
        lock (_lock)
        {
            try
            {
                if (_dataManagers.ContainsKey(instrumentName))
                {
                    // Increment reference count for existing manager
                    _referenceCount[instrumentName]++;
                    System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Reusing existing manager for {instrumentName}, incrementing reference count to {_referenceCount[instrumentName]}");
                }
                else
                {
                    // Create new manager - use external control pipe mode if specified
                    InstrumentDataManager manager;
                    if (useExternalControlPipe)
                    {
                        // Data-only mode: create manager without control pipe (handled externally by AddOn)
                        manager = new InstrumentDataManager(instrumentName, tickSize, pointValue, sharedMemoryName, useExternalControlPipe);
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Created data-only manager (external control pipe mode)");
                    }
                    else if (string.IsNullOrEmpty(sharedMemoryName))
                    {
                        // Default mode: auto-generated names for both channels
                        manager = new InstrumentDataManager(instrumentName, tickSize, pointValue);
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Created default manager with auto-generated channels");
                    }
                    else
                    {
                        // Custom mode: use provided shared memory name and auto-generate control pipe
                        manager = new InstrumentDataManager(instrumentName, tickSize, pointValue, sharedMemoryName, null);
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Created custom manager with specified data channel");
                    }
                    
                    _dataManagers.Add(instrumentName, manager);
                    _referenceCount.Add(instrumentName, 1);
                    System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Created new manager for {instrumentName} with reference count 1");
                    
                    if (!string.IsNullOrEmpty(sharedMemoryName))
                    {
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Using custom SharedMemory: {sharedMemoryName}");
                    }
                    if (useExternalControlPipe)
                    {
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Using external control pipe (AddOn handles control operations)");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DataEngineManager] ERROR adding instrument {instrumentName}: {ex.Message}");
                throw;
            }
        }
    }

    public static InstrumentDataManager GetManager(string instrumentName)
    {
        lock (_lock)
        {
            _dataManagers.TryGetValue(instrumentName, out var manager);
            return manager;
        }
    }

    public static void RemoveInstrument(string instrumentName)
    {
        lock (_lock)
        {
            if (!_dataManagers.ContainsKey(instrumentName))
            {
                System.Diagnostics.Trace.WriteLine($"[DataEngineManager] No manager found for {instrumentName} to remove");
                return;
            }
            
            try
            {
                // Decrement reference count
                _referenceCount[instrumentName]--;
                System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Decrementing reference count for {instrumentName} to {_referenceCount[instrumentName]}");
                
                // Only dispose if reference count reaches 0
                if (_referenceCount[instrumentName] <= 0)
                {
                    if (_dataManagers.TryGetValue(instrumentName, out var manager))
                    {
                        manager.Dispose();
                        _dataManagers.Remove(instrumentName);
                        _referenceCount.Remove(instrumentName);
                        System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Disposed and removed manager for {instrumentName}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DataEngineManager] ERROR removing instrument {instrumentName}: {ex.Message}");
            }
        }
    }
    
    public static void Cleanup()
    {
        lock (_lock)
        {
            System.Diagnostics.Trace.WriteLine($"[DataEngineManager] Cleaning up {_dataManagers.Count} managers");
            foreach (var kvp in _dataManagers)
            {
                try
                {
                    kvp.Value.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[DataEngineManager] ERROR disposing manager for {kvp.Key}: {ex.Message}");
                }
            }
            _dataManagers.Clear();
            _referenceCount.Clear();
        }
    }
    
    public static int GetReferenceCount(string instrumentName)
    {
        lock (_lock)
        {
            return _referenceCount.TryGetValue(instrumentName, out var count) ? count : 0;
        }
    }
}