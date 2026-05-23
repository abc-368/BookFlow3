using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BookFlow.Shared.Contracts
{
    public class ColumnConfig : INotifyPropertyChanged
    {
        private string _key = string.Empty;
        private string _displayName = string.Empty;
        private int _width;
        private int _fontSize = 8;
        private bool _isBold = false;
        private string _fontColor = "#FFFFFFFF"; // white

        public string Key { get => _key; set { if (_key != value) { _key = value; OnPropertyChanged(); } } }
        public string DisplayName { get => _displayName; set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } } }
        public int Width { get => _width; set { if (_width != value) { _width = value; OnPropertyChanged(); } } }
        public int FontSize { get => _fontSize; set { if (_fontSize != value) { _fontSize = value; OnPropertyChanged(); } } }
        public bool IsBold { get => _isBold; set { if (_isBold != value) { _isBold = value; OnPropertyChanged(); } } }
        public string FontColor { get => _fontColor; set { if (_fontColor != value) { _fontColor = value; OnPropertyChanged(); } } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
