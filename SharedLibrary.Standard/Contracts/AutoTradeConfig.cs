using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BookFlow.Shared.Contracts
{
    /// <summary>
    /// Per-signal-type automatic-trade configuration. When the master arm switch is on and a
    /// signal of this type qualifies (reliable / "green"), the client fires a single bracket entry
    /// with these parameters; the NT8 host attaches the OCO target/stop on fill.
    ///
    /// Aggression signals carry no price level, so they always enter at market regardless of
    /// <see cref="UseMarketOrder"/>. For level-anchored types the entry price is
    /// <c>signal price + OffsetTicks * tickSize</c> (the offset is signed and applied literally —
    /// negative = below the level, positive = above — for both buy and sell).
    /// </summary>
    public sealed class AutoTradeConfig : INotifyPropertyChanged
    {
        private bool _enabled;
        private bool _useMarketOrder;
        private int _offsetTicks;
        private int _targetTicks = 4; // default matches the reliability metric's default success target
        private int _stopTicks = 4;   // so out-of-the-box "green" describes the default bracket
        private int _size = 1;

        public bool Enabled { get => _enabled; set { if (_enabled != value) { _enabled = value; OnPropertyChanged(); } } }
        public bool UseMarketOrder { get => _useMarketOrder; set { if (_useMarketOrder != value) { _useMarketOrder = value; OnPropertyChanged(); } } }
        public int OffsetTicks { get => _offsetTicks; set { if (_offsetTicks != value && value >= -1000 && value <= 1000) { _offsetTicks = value; OnPropertyChanged(); } } }
        public int TargetTicks { get => _targetTicks; set { if (_targetTicks != value && value >= 1 && value <= 100000) { _targetTicks = value; OnPropertyChanged(); } } }
        public int StopTicks { get => _stopTicks; set { if (_stopTicks != value && value >= 1 && value <= 100000) { _stopTicks = value; OnPropertyChanged(); } } }
        public int Size { get => _size; set { if (_size != value && value >= 1 && value <= 10000) { _size = value; OnPropertyChanged(); } } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
