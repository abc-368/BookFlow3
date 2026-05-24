using System;
using System.IO;

namespace BookFlow.App.Persistence
{
    /// <summary>Per-user on-disk locations for BookFlow settings and signal history.</summary>
    public static class BookFlowPaths
    {
        public static string Base =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BookFlow");

        public static string SettingsDir => Path.Combine(Base, "Settings");
        public static string HistoryDir => Path.Combine(Base, "History");

        public static void EnsureDirs()
        {
            Directory.CreateDirectory(SettingsDir);
            Directory.CreateDirectory(HistoryDir);
        }

        public static string Sanitize(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return "_";
            foreach (var c in Path.GetInvalidFileNameChars()) symbol = symbol.Replace(c, '_');
            return symbol;
        }

        public static string SettingsFile(string root) => Path.Combine(SettingsDir, Sanitize(root) + ".json");
        public static string HistoryFile(string root) => Path.Combine(HistoryDir, Sanitize(root) + ".json");
    }
}
