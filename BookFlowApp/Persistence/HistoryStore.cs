using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BookFlow.App.Diagnostics;
using BookFlow.Shared.Analytics;

namespace BookFlow.App.Persistence
{
    /// <summary>
    /// Persists recorded <see cref="SignalOutcome"/>s per root/continuous symbol (so history
    /// survives contract rolls). Keep-by-default: <see cref="Wipe"/>/<see cref="WipeAll"/> are the
    /// only ways history is cleared.
    /// </summary>
    public static class HistoryStore
    {
        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true };

        public static void Save(string root, IReadOnlyList<SignalOutcome> outcomes)
        {
            try
            {
                BookFlowPaths.EnsureDirs();
                File.WriteAllText(BookFlowPaths.HistoryFile(root), JsonSerializer.Serialize(outcomes, Opts));
            }
            catch (Exception ex) { BookFlowLog.Error("HistoryStore", "save failed for " + root, ex); }
        }

        public static List<SignalOutcome> Load(string root)
        {
            try
            {
                var f = BookFlowPaths.HistoryFile(root);
                if (!File.Exists(f)) return new List<SignalOutcome>();
                return JsonSerializer.Deserialize<List<SignalOutcome>>(File.ReadAllText(f), Opts) ?? new List<SignalOutcome>();
            }
            catch (Exception ex) { BookFlowLog.Error("HistoryStore", "load failed for " + root, ex); return new List<SignalOutcome>(); }
        }

        public static void Wipe(string root)
        {
            try { var f = BookFlowPaths.HistoryFile(root); if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) { BookFlowLog.Error("HistoryStore", "wipe failed for " + root, ex); }
        }

        public static int WipeAll()
        {
            try
            {
                if (!Directory.Exists(BookFlowPaths.HistoryDir)) return 0;
                var files = Directory.GetFiles(BookFlowPaths.HistoryDir, "*.json");
                foreach (var f in files) File.Delete(f);
                return files.Length;
            }
            catch (Exception ex) { BookFlowLog.Error("HistoryStore", "wipe-all failed", ex); return 0; }
        }
    }
}
