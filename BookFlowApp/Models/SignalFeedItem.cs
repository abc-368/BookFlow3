using System;
using BookFlow.Shared.Analytics;

namespace BookFlow.App.Models
{
    /// <summary>A row in the microstructure signal feed (most-recent detections).</summary>
    public sealed class SignalFeedItem
    {
        public DateTime Time { get; init; }
        public MicrostructureSignalType Type { get; init; }
        public MicrostructureSide Side { get; init; }
        public decimal? Price { get; init; }
        public string Label { get; init; } = "";
        public MicrostructureBias Bias { get; init; }
        public bool IsStrong { get; init; }

        // Historical prediction power of this signal type (stamped at emit time; never revised).
        public int ReliabilityBars { get; init; }
        public double ReliabilityRatio { get; init; }
        public int ReliabilitySamples { get; init; }
        public bool ReliabilityLearning { get; init; }

        public string TimeText => Time.ToString("HH:mm:ss");

        // Right-aligned 5-segment strength meter showing how reliable this type has been.
        public string StrengthMeter
        {
            get
            {
                if (Bias == MicrostructureBias.Neutral) return "";
                int bars = ReliabilityBars < 0 ? 0 : ReliabilityBars > 5 ? 5 : ReliabilityBars;
                return new string('▰', bars) + new string('▱', 5 - bars);
            }
        }

        // Meter color: gray while learning, then red→amber→green by hit-rate.
        public string StrengthColorHex
        {
            get
            {
                if (Bias == MicrostructureBias.Neutral || ReliabilityLearning) return "#FF607D8B"; // gray
                if (ReliabilityRatio < 0.45) return "#FFEF5350";  // red
                if (ReliabilityRatio < 0.60) return "#FFFFA726";  // amber
                return "#FF66BB6A";                                // green
            }
        }

        public string StrengthTooltip =>
            Bias == MicrostructureBias.Neutral ? "No directional prediction"
            : ReliabilityLearning ? $"Reliability: learning ({ReliabilitySamples} resolved)"
            : $"Reliability {ReliabilityRatio:P0} over {ReliabilitySamples} resolved {Type} signal(s)";

        // Predicted direction arrow (bold rendered separately via IsStrong).
        public string DirectionArrow => Bias switch
        {
            MicrostructureBias.Up => "▲",   // ▲
            MicrostructureBias.Down => "▼", // ▼
            _ => "•",                        // •
        };

        // Arrow color by predicted direction.
        public string ArrowColorHex => Bias switch
        {
            MicrostructureBias.Up => "#FF66BB6A",   // green
            MicrostructureBias.Down => "#FFEF5350", // red
            _ => "#FFB0BEC5",                        // gray
        };

        // Label color by signal type/category.
        public string ColorHex => Type switch
        {
            MicrostructureSignalType.Spoofing => "#FFFFA726",            // amber
            MicrostructureSignalType.Iceberg => "#FF42A5F5",             // blue
            MicrostructureSignalType.LiquidityWithdrawal => "#FFEF5350", // red
            MicrostructureSignalType.AggressionImbalance =>
                Side == MicrostructureSide.Ask ? "#FF66BB6A" : "#FFEF5350", // buy=green / sell=red
            _ => "#FFB0BEC5",
        };
    }
}
