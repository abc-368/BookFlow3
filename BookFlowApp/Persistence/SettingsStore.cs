using System;
using System.IO;
using System.Text.Json;
using BookFlow.App.Diagnostics;
using BookFlow.Shared.Contracts;

namespace BookFlow.App.Persistence
{
    /// <summary>
    /// Persists the per-symbol signal/trade TUNING subset of <see cref="DomSettings"/> (always on —
    /// you never re-tune from scratch). Deliberately does NOT persist <c>AutoTradeArmed</c>: arming
    /// is a per-session decision, never restored automatically. Keyed by root/continuous symbol.
    /// (Column layout / fonts / colors are not persisted yet.)
    /// </summary>
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { WriteIndented = true };

        public static void Save(string root, DomSettings s)
        {
            try
            {
                BookFlowPaths.EnsureDirs();
                File.WriteAllText(BookFlowPaths.SettingsFile(root), JsonSerializer.Serialize(DomSettingsSnapshot.From(s), Opts));
            }
            catch (Exception ex) { BookFlowLog.Error("SettingsStore", "save failed for " + root, ex); }
        }

        /// <summary>Apply persisted tuning onto <paramref name="target"/> if a file exists; returns true if applied.</summary>
        public static bool ApplyIfPresent(string root, DomSettings target)
        {
            try
            {
                var f = BookFlowPaths.SettingsFile(root);
                if (!File.Exists(f)) return false;
                var snap = JsonSerializer.Deserialize<DomSettingsSnapshot>(File.ReadAllText(f), Opts);
                snap?.ApplyTo(target);
                return snap != null;
            }
            catch (Exception ex) { BookFlowLog.Error("SettingsStore", "load failed for " + root, ex); return false; }
        }
    }

    /// <summary>Plain DTO mirror of the tunable settings (robust round-trip, no serializer surprises).</summary>
    public sealed class DomSettingsSnapshot
    {
        public bool EnableMicrostructureSignals { get; set; }
        public bool EnableSignalAudit { get; set; }

        public int SpoofMinChurn { get; set; }
        public double SpoofMaxTradedFraction { get; set; }
        public int IcebergMinRefill { get; set; }
        public int WithdrawalMinSize { get; set; }
        public int WithdrawalRadiusTicks { get; set; }
        public int AggressionMinVolume { get; set; }
        public double AggressionMinImbalance { get; set; }
        public int OfiMinImbalance { get; set; }
        public int OfiRadiusTicks { get; set; }
        public double BookImbalanceMinRatio { get; set; }
        public int BookImbalanceMinSize { get; set; }

        public int ReliabilityMinSamples { get; set; }
        public int ReliabilityGreenPercent { get; set; }
        public int AutoTradeEntryTimeoutSeconds { get; set; }
        public double AutoTradeMaxToxicity { get; set; }

        public bool ShowSpoofingSignals { get; set; }
        public bool ShowIcebergSignals { get; set; }
        public bool ShowWithdrawalSignals { get; set; }
        public bool ShowAggressionSignals { get; set; }
        public bool ShowOfiSignals { get; set; }
        public bool ShowBookImbalanceSignals { get; set; }

        public AutoTradeConfigDto Spoofing { get; set; } = new AutoTradeConfigDto();
        public AutoTradeConfigDto Iceberg { get; set; } = new AutoTradeConfigDto();
        public AutoTradeConfigDto Withdrawal { get; set; } = new AutoTradeConfigDto();
        public AutoTradeConfigDto Aggression { get; set; } = new AutoTradeConfigDto();
        public AutoTradeConfigDto Ofi { get; set; } = new AutoTradeConfigDto();
        public AutoTradeConfigDto BookImbalance { get; set; } = new AutoTradeConfigDto();

        public static DomSettingsSnapshot From(DomSettings s) => new DomSettingsSnapshot
        {
            EnableMicrostructureSignals = s.EnableMicrostructureSignals,
            EnableSignalAudit = s.EnableSignalAudit,
            SpoofMinChurn = s.SpoofMinChurn,
            SpoofMaxTradedFraction = s.SpoofMaxTradedFraction,
            IcebergMinRefill = s.IcebergMinRefill,
            WithdrawalMinSize = s.WithdrawalMinSize,
            WithdrawalRadiusTicks = s.WithdrawalRadiusTicks,
            AggressionMinVolume = s.AggressionMinVolume,
            AggressionMinImbalance = s.AggressionMinImbalance,
            OfiMinImbalance = s.OfiMinImbalance,
            OfiRadiusTicks = s.OfiRadiusTicks,
            BookImbalanceMinRatio = s.BookImbalanceMinRatio,
            BookImbalanceMinSize = s.BookImbalanceMinSize,
            ReliabilityMinSamples = s.ReliabilityMinSamples,
            ReliabilityGreenPercent = s.ReliabilityGreenPercent,
            AutoTradeEntryTimeoutSeconds = s.AutoTradeEntryTimeoutSeconds,
            AutoTradeMaxToxicity = s.AutoTradeMaxToxicity,
            ShowSpoofingSignals = s.ShowSpoofingSignals,
            ShowIcebergSignals = s.ShowIcebergSignals,
            ShowWithdrawalSignals = s.ShowWithdrawalSignals,
            ShowAggressionSignals = s.ShowAggressionSignals,
            ShowOfiSignals = s.ShowOfiSignals,
            ShowBookImbalanceSignals = s.ShowBookImbalanceSignals,
            Spoofing = AutoTradeConfigDto.From(s.SpoofingAutoTrade),
            Iceberg = AutoTradeConfigDto.From(s.IcebergAutoTrade),
            Withdrawal = AutoTradeConfigDto.From(s.WithdrawalAutoTrade),
            Aggression = AutoTradeConfigDto.From(s.AggressionAutoTrade),
            Ofi = AutoTradeConfigDto.From(s.OfiAutoTrade),
            BookImbalance = AutoTradeConfigDto.From(s.BookImbalanceAutoTrade),
        };

        public void ApplyTo(DomSettings s)
        {
            s.EnableMicrostructureSignals = EnableMicrostructureSignals;
            s.EnableSignalAudit = EnableSignalAudit;
            s.SpoofMinChurn = SpoofMinChurn;
            s.SpoofMaxTradedFraction = SpoofMaxTradedFraction;
            s.IcebergMinRefill = IcebergMinRefill;
            s.WithdrawalMinSize = WithdrawalMinSize;
            s.WithdrawalRadiusTicks = WithdrawalRadiusTicks;
            s.AggressionMinVolume = AggressionMinVolume;
            s.AggressionMinImbalance = AggressionMinImbalance;
            s.OfiMinImbalance = OfiMinImbalance;
            s.OfiRadiusTicks = OfiRadiusTicks;
            s.BookImbalanceMinRatio = BookImbalanceMinRatio;
            s.BookImbalanceMinSize = BookImbalanceMinSize;
            s.ReliabilityMinSamples = ReliabilityMinSamples;
            s.ReliabilityGreenPercent = ReliabilityGreenPercent;
            s.AutoTradeEntryTimeoutSeconds = AutoTradeEntryTimeoutSeconds;
            s.AutoTradeMaxToxicity = AutoTradeMaxToxicity;
            s.ShowSpoofingSignals = ShowSpoofingSignals;
            s.ShowIcebergSignals = ShowIcebergSignals;
            s.ShowWithdrawalSignals = ShowWithdrawalSignals;
            s.ShowAggressionSignals = ShowAggressionSignals;
            s.ShowOfiSignals = ShowOfiSignals;
            s.ShowBookImbalanceSignals = ShowBookImbalanceSignals;
            Spoofing.ApplyTo(s.SpoofingAutoTrade);
            Iceberg.ApplyTo(s.IcebergAutoTrade);
            Withdrawal.ApplyTo(s.WithdrawalAutoTrade);
            Aggression.ApplyTo(s.AggressionAutoTrade);
            Ofi.ApplyTo(s.OfiAutoTrade);
            BookImbalance.ApplyTo(s.BookImbalanceAutoTrade);
            // AutoTradeArmed intentionally NOT restored — arming is always a deliberate per-session action.
        }
    }

    public sealed class AutoTradeConfigDto
    {
        public bool Enabled { get; set; }
        public bool UseMarketOrder { get; set; }
        public int OffsetTicks { get; set; }
        public int TargetTicks { get; set; } = 4;
        public int StopTicks { get; set; } = 4;
        public int Size { get; set; } = 1;

        public static AutoTradeConfigDto From(AutoTradeConfig c) => new AutoTradeConfigDto
        {
            Enabled = c.Enabled, UseMarketOrder = c.UseMarketOrder, OffsetTicks = c.OffsetTicks,
            TargetTicks = c.TargetTicks, StopTicks = c.StopTicks, Size = c.Size,
        };

        public void ApplyTo(AutoTradeConfig c)
        {
            c.Enabled = Enabled; c.UseMarketOrder = UseMarketOrder; c.OffsetTicks = OffsetTicks;
            c.TargetTicks = TargetTicks; c.StopTicks = StopTicks; c.Size = Size;
        }
    }
}
