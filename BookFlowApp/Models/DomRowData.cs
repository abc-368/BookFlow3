using System.ComponentModel;
using System.Runtime.CompilerServices;
using BookFlow.Shared.Contracts; // For shared PriceLevel

namespace BookFlow.App.Models
{
    /// <summary>
    /// Data model for a single DOM row in the DevExpress grid.
    /// Based on BookFlowApp-Sample DomRowData with essential properties for DOM display.
    /// </summary>
    public class DomRowData : INotifyPropertyChanged
    {
        private decimal _price;
        private long _bidVolume;
        private long _askVolume;
        private int _bidCount;
        private int _askCount;
        private bool _isTopOfBook;
        private bool _hasPosition;
        private long _positionQuantity;

        // Additional properties for 15-column DOM
        private string _observations = "";
        private string _bidOrdersInfo = "";
        private string _askOrdersInfo = "";
        private decimal _openPositionPnL = 0m;
        
        // Enhanced order tracking properties
        private string _bidOrdersFullText = "";
        private string _askOrdersFullText = "";
        private int _bidOrderCount = 0;
        private int _askOrderCount = 0;
        private int _myBidOrderCount = 0;
        private int _myAskOrderCount = 0;
        private long _bidSnapshot = 0;
        private long _bidDepth = 0;
        private long _lastTradeAtBid = 0;
        private long _lastTradeAtAsk = 0;
        private long _lastTradeAtBidBurst = 0;
        private long _lastTradeAtAskBurst = 0;
        private long _askDepth = 0;
        private long _askSnapshot = 0;
        private long _askProfile = 0;
        private long _bidProfile = 0;
        private string _reserve = "";

        // Supporting properties for advanced features
        private bool _hasL1Update = false;
        private bool _showBidSum = false;
        private bool _showAskSum = false;
        private long _bidSumValue = 0;
        private long _askSumValue = 0;
        private bool _isTopBid = false;
        private bool _isTopAsk = false;

        // Position markers to overlay on order counts without overriding working orders
        private int _positionBidMarker = 0; // long position marker on bid column
        private int _positionAskMarker = 0; // short position marker on ask column (positive value; UI formats with '-')

        public decimal Price
        {
            get => _price;
            set
            {
                if (_price != value)
                {
                    _price = value;
                    OnPropertyChanged();
                }
            }
        }

        public long BidVolume
        {
            get => _bidVolume;
            set
            {
                if (_bidVolume != value)
                {
                    _bidVolume = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasBids));
                }
            }
        }

        public long AskVolume
        {
            get => _askVolume;
            set
            {
                if (_askVolume != value)
                {
                    _askVolume = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAsks));
                }
            }
        }

        public int BidCount
        {
            get => _bidCount;
            set
            {
                if (_bidCount != value)
                {
                    _bidCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public int AskCount
        {
            get => _askCount;
            set
            {
                if (_askCount != value)
                {
                    _askCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsTopOfBook
        {
            get => _isTopOfBook;
            set
            {
                if (_isTopOfBook != value)
                {
                    _isTopOfBook = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasPosition
        {
            get => _hasPosition;
            set
            {
                if (_hasPosition != value)
                {
                    _hasPosition = value;
                    OnPropertyChanged();
                }
            }
        }

        public long PositionQuantity
        {
            get => _positionQuantity;
            set
            {
                if (_positionQuantity != value)
                {
                    _positionQuantity = value;
                    OnPropertyChanged();
                }
            }
        }

        #region Advanced 15-Column DOM Properties

        public string Observations
        {
            get => _observations;
            set
            {
                if (_observations != value)
                {
                    _observations = value;
                    OnPropertyChanged();
                }
            }
        }

        public string BidOrdersInfo
        {
            get => _bidOrdersInfo;
            set
            {
                if (_bidOrdersInfo != value)
                {
                    _bidOrdersInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AskOrdersInfo
        {
            get => _askOrdersInfo;
            set
            {
                if (_askOrdersInfo != value)
                {
                    _askOrdersInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public decimal OpenPositionPnL
        {
            get => _openPositionPnL;
            set
            {
                if (_openPositionPnL != value)
                {
                    _openPositionPnL = value;
                    OnPropertyChanged();
                }
            }
        }

        public long VolumeProfile
        {
            get => _askProfile + _bidProfile; // Sum of Ask and Bid profiles
            set
            {
                // For backward compatibility, if someone sets VolumeProfile directly,
                // we distribute it proportionally between ask and bid profiles
                var totalCurrent = _askProfile + _bidProfile;
                if (totalCurrent > 0 && value != totalCurrent)
                {
                    var askRatio = (double)_askProfile / totalCurrent;
                    var bidRatio = (double)_bidProfile / totalCurrent;
                    
                    _askProfile = (long)(value * askRatio);
                    _bidProfile = (long)(value * bidRatio);
                }
                else if (totalCurrent == 0 && value > 0)
                {
                    // If no existing data, split equally
                    _askProfile = value / 2;
                    _bidProfile = value - _askProfile;
                }
                
                OnPropertyChanged();
                OnPropertyChanged(nameof(AskProfile));
                OnPropertyChanged(nameof(BidProfile));
            }
        }

        public long BidSnapshot
        {
            get => _bidSnapshot;
            set
            {
                if (_bidSnapshot != value)
                {
                    _bidSnapshot = value;
                    OnPropertyChanged();
                }
            }
        }

        public long BidDepth
        {
            get => _bidDepth;
            set
            {
                if (_bidDepth != value)
                {
                    _bidDepth = value;
                    OnPropertyChanged();
                }
            }
        }

        public long LastTradeAtBid
        {
            get => _lastTradeAtBid;
            set
            {
                if (_lastTradeAtBid != value)
                {
                    _lastTradeAtBid = value;
                    OnPropertyChanged();
                }
            }
        }

        public long LastTradeAtAsk
        {
            get => _lastTradeAtAsk;
            set
            {
                if (_lastTradeAtAsk != value)
                {
                    _lastTradeAtAsk = value;
                    OnPropertyChanged();
                }
            }
        }

        public long LastTradeAtBidBurst
        {
            get => _lastTradeAtBidBurst;
            set
            {
                if (_lastTradeAtBidBurst != value)
                {
                    _lastTradeAtBidBurst = value;
                    OnPropertyChanged();
                }
            }
        }

        public long LastTradeAtAskBurst
        {
            get => _lastTradeAtAskBurst;
            set
            {
                if (_lastTradeAtAskBurst != value)
                {
                    _lastTradeAtAskBurst = value;
                    OnPropertyChanged();
                }
            }
        }

        public long AskDepth
        {
            get => _askDepth;
            set
            {
                if (_askDepth != value)
                {
                    _askDepth = value;
                    OnPropertyChanged();
                }
            }
        }

        public long AskSnapshot
        {
            get => _askSnapshot;
            set
            {
                if (_askSnapshot != value)
                {
                    _askSnapshot = value;
                    OnPropertyChanged();
                }
            }
        }

        public long AskProfile
        {
            get => _askProfile;
            set
            {
                if (_askProfile != value)
                {
                    _askProfile = value;
                    OnPropertyChanged();
                }
            }
        }

        public long BidProfile
        {
            get => _bidProfile;
            set
            {
                if (_bidProfile != value)
                {
                    _bidProfile = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Reserve
        {
            get => _reserve;
            set
            {
                if (_reserve != value)
                {
                    _reserve = value;
                    OnPropertyChanged();
                }
            }
        }

        // Supporting properties for order tracking
        public string BidOrdersFullText
        {
            get => _bidOrdersFullText;
            set
            {
                if (_bidOrdersFullText != value)
                {
                    _bidOrdersFullText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AskOrdersFullText
        {
            get => _askOrdersFullText;
            set
            {
                if (_askOrdersFullText != value)
                {
                    _askOrdersFullText = value;
                    OnPropertyChanged();
                }
            }
        }

        public int BidOrderCount
        {
            get => _bidOrderCount;
            set
            {
                if (_bidOrderCount != value)
                {
                    _bidOrderCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public int AskOrderCount
        {
            get => _askOrderCount;
            set
            {
                if (_askOrderCount != value)
                {
                    _askOrderCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public int MyBidOrderCount
        {
            get => _myBidOrderCount;
            set
            {
                if (_myBidOrderCount != value)
                {
                    _myBidOrderCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayBidOrderCount));
                }
            }
        }

        public int MyAskOrderCount
        {
            get => _myAskOrderCount;
            set
            {
                if (_myAskOrderCount != value)
                {
                    _myAskOrderCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayAskOrderCount));
                }
            }
        }

        public int PositionBidMarker
        {
            get => _positionBidMarker;
            set
            {
                if (_positionBidMarker != value)
                {
                    _positionBidMarker = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayBidOrderCount));
                }
            }
        }

        public int PositionAskMarker
        {
            get => _positionAskMarker;
            set
            {
                if (_positionAskMarker != value)
                {
                    _positionAskMarker = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayAskOrderCount));
                }
            }
        }

        // Display counts that combine working orders and position markers
        public int DisplayBidOrderCount => _myBidOrderCount;
        public int DisplayAskOrderCount => _myAskOrderCount;

        public bool HasL1Update
        {
            get => _hasL1Update;
            set
            {
                if (_hasL1Update != value)
                {
                    _hasL1Update = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowBidSum
        {
            get => _showBidSum;
            set
            {
                if (_showBidSum != value)
                {
                    _showBidSum = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowAskSum
        {
            get => _showAskSum;
            set
            {
                if (_showAskSum != value)
                {
                    _showAskSum = value;
                    OnPropertyChanged();
                }
            }
        }

        public long BidSumValue
        {
            get => _bidSumValue;
            set
            {
                if (_bidSumValue != value)
                {
                    _bidSumValue = value;
                    OnPropertyChanged();
                }
            }
        }

        public long AskSumValue
        {
            get => _askSumValue;
            set
            {
                if (_askSumValue != value)
                {
                    _askSumValue = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsTopBid
        {
            get => _isTopBid;
            set
            {
                if (_isTopBid != value)
                {
                    _isTopBid = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsTopAsk
        {
            get => _isTopAsk;
            set
            {
                if (_isTopAsk != value)
                {
                    _isTopAsk = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        // Computed properties for display
        public bool HasBids => BidVolume > 0;
        public bool HasAsks => AskVolume > 0;
        public string PriceText => Price.ToString("F2");
        public string BidVolumeText => BidVolume > 0 ? BidVolume.ToString() : "";
        public string AskVolumeText => AskVolume > 0 ? AskVolume.ToString() : "";
        public string BidCountText => BidCount > 0 ? BidCount.ToString() : "";
        public string AskCountText => AskCount > 0 ? AskCount.ToString() : "";

        // Constructor
        public DomRowData()
        {
        }

        public DomRowData(decimal price)
        {
            _price = price;
        }

        // Factory method from PriceLevel
        public static DomRowData FromPriceLevel(BookFlow.Shared.Contracts.PriceLevel priceLevel)
        {
            return new DomRowData
            {
                // Basic properties
                Price = priceLevel.Price,
                BidVolume = priceLevel.BidVolume,
                AskVolume = priceLevel.AskVolume,
                BidCount = priceLevel.BidCount,
                AskCount = priceLevel.AskCount,
                IsTopOfBook = priceLevel.IsTopOfBook,
                
                // Enhanced properties from PriceLevel
                VolumeProfile = priceLevel.VolumeProfileVolume,
                BidDepth = priceLevel.BidVolume,  // Current bid volume = depth
                AskDepth = priceLevel.AskVolume,  // Current ask volume = depth
                LastTradeAtBid = priceLevel.BidSideTradedVolume,
                LastTradeAtAsk = priceLevel.AskSideTradedVolume,
                LastTradeAtBidBurst = priceLevel.BidSideTradedVolume, // Market sells hitting bids
                LastTradeAtAskBurst = priceLevel.AskSideTradedVolume, // Market buys hitting asks
                OpenPositionPnL = priceLevel.UnrealizedPnL,
                BidProfile = priceLevel.BidSideTradedVolume,
                AskProfile = priceLevel.AskSideTradedVolume,
                
                // Order tracking
                BidOrderCount = priceLevel.BidCount,
                AskOrderCount = priceLevel.AskCount,
                MyBidOrderCount = priceLevel.MyBidOrderCount,
                MyAskOrderCount = priceLevel.MyAskOrderCount,
                
                // State flags
                IsTopBid = false, // Will be set correctly by DomViewModel based on best bid price
                IsTopAsk = false, // Will be set correctly by DomViewModel based on best ask price
                HasL1Update = priceLevel.HasRecentActivity,
                
                // Initialize placeholders for future enhancement
                Observations = "",
                BidOrdersInfo = "", // Only show when user has working orders at this price
                AskOrdersInfo = "", // Only show when user has working orders at this price
                BidOrdersFullText = priceLevel.BidCount > 0 ? $"{priceLevel.BidCount} orders at {priceLevel.Price:F2}" : "",
                AskOrdersFullText = priceLevel.AskCount > 0 ? $"{priceLevel.AskCount} orders at {priceLevel.Price:F2}" : "",
                Reserve = ""
            };
        }

        // Method to update from PriceLevel
        public void UpdateFromPriceLevel(BookFlow.Shared.Contracts.PriceLevel priceLevel)
        {
            // Basic properties
            Price = priceLevel.Price;
            BidVolume = priceLevel.BidVolume;
            AskVolume = priceLevel.AskVolume;
            BidCount = priceLevel.BidCount;
            AskCount = priceLevel.AskCount;
            IsTopOfBook = priceLevel.IsTopOfBook;
            
            // Enhanced properties from PriceLevel
            // IMPORTANT: AskProfile and BidProfile are cumulative and NEVER reset
            // We need to add any NEW traded volume from PriceLevel to our cumulative profiles
            var newBidTradedVolume = priceLevel.BidSideTradedVolume;
            var newAskTradedVolume = priceLevel.AskSideTradedVolume;
            
            // Only add incremental volume - if PriceLevel has more volume than we've recorded
            if (newBidTradedVolume > _bidProfile)
            {
                var increment = newBidTradedVolume - _bidProfile;
                _bidProfile += increment;
                OnPropertyChanged(nameof(BidProfile));
                OnPropertyChanged(nameof(VolumeProfile)); // VolumeProfile is computed
            }
            
            if (newAskTradedVolume > _askProfile)
            {
                var increment = newAskTradedVolume - _askProfile;
                _askProfile += increment;
                OnPropertyChanged(nameof(AskProfile));
                OnPropertyChanged(nameof(VolumeProfile)); // VolumeProfile is computed
            }
            
            BidDepth = priceLevel.BidVolume;  // Current bid volume = depth
            AskDepth = priceLevel.AskVolume;  // Current ask volume = depth
            LastTradeAtBid = priceLevel.BidSideTradedVolume;
            LastTradeAtAsk = priceLevel.AskSideTradedVolume;
            LastTradeAtBidBurst = priceLevel.BidSideTradedVolume; // Market sells hitting bids
            LastTradeAtAskBurst = priceLevel.AskSideTradedVolume; // Market buys hitting asks
            OpenPositionPnL = priceLevel.UnrealizedPnL;
            
            // Order tracking
            BidOrderCount = priceLevel.BidCount;
            AskOrderCount = priceLevel.AskCount;
            MyBidOrderCount = priceLevel.MyBidOrderCount;
            MyAskOrderCount = priceLevel.MyAskOrderCount;
            
            // State flags
            IsTopBid = false; // Will be set correctly by DomViewModel based on best bid price
            IsTopAsk = false; // Will be set correctly by DomViewModel based on best ask price
            HasL1Update = priceLevel.HasRecentActivity;
            
            // Update order info if we have orders
            BidOrdersInfo = ""; // Only show when user has working orders at this price
            AskOrdersInfo = ""; // Only show when user has working orders at this price
            BidOrdersFullText = priceLevel.BidCount > 0 ? $"{priceLevel.BidCount} orders at {priceLevel.Price:F2}" : "";
            AskOrdersFullText = priceLevel.AskCount > 0 ? $"{priceLevel.AskCount} orders at {priceLevel.Price:F2}" : "";
        }
        
        /// <summary>
        /// Adds trade volume to the appropriate profile column (cumulative and never reset).
        /// This is the proper way to update profile data - NOT through UpdateFromPriceLevel.
        /// </summary>
        /// <param name="volume">Volume traded</param>
        /// <param name="isAskSide">True if trade hit ask (buyer initiated), false if hit bid (seller initiated)</param>
        public void AddTradeVolume(long volume, bool isAskSide)
        {
            if (volume <= 0) return;
            
            if (isAskSide)
            {
                // Trade hit the ask (buyer initiated) - add to AskProfile
                AskProfile += volume;
            }
            else
            {
                // Trade hit the bid (seller initiated) - add to BidProfile  
                BidProfile += volume;
            }
            
            // VolumeProfile is automatically updated since it's computed as AskProfile + BidProfile
            OnPropertyChanged(nameof(VolumeProfile));
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}