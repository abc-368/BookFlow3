using System;
using System.IO;

namespace BookFlow.App.Diagnostics
{
    /// <summary>
    /// Lightweight, thread-safe, dependency-free file logger for the WPF client.
    /// Writes to %UserProfile%\Documents\BookFlow\Logs\app_yyyy-MM-dd.log.
    ///
    /// Purpose: give the client persistent, always-on visibility — in particular for
    /// errors, which were previously discarded in production (DomEngine only logged to
    /// Debug when a compile-time flag was on). Logging must never throw.
    ///
    /// Intended for low-frequency events (errors, connection state, trades), NOT the
    /// per-tick data path. A full log4net setup (rotation/async buffering, separate
    /// App/Trade/Debug appenders) is the future-hardening path.
    /// </summary>
    public static class BookFlowLog
    {
        private static readonly object Gate = new object();
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BookFlow", "Logs");

        public static void Error(string source, string message, Exception? ex = null)
            => Write("ERROR", source, ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

        public static void Info(string source, string message) => Write("INFO", source, message);

        public static void Trade(string message) => Write("TRADE", "Trade", message);

        private static void Write(string level, string source, string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{source}] {message}{Environment.NewLine}";
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(Path.Combine(Dir, $"app_{DateTime.Now:yyyy-MM-dd}.log"), line);
                }
            }
            catch { /* logging must never throw */ }
        }
    }
}
