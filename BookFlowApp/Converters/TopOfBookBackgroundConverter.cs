using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BookFlow.App.Converters
{
    /// <summary>
    /// Converter to provide ORANGE background highlighting for top of book (best bid/ask) rows.
    /// 
    /// ORANGE COLOR PURPOSE:
    /// - Instantly identifies the BEST BID and BEST ASK prices in the DOM
    /// - These are the prices where market orders will execute immediately
    /// - Critical for traders to spot executable prices at a glance
    /// - Only appears when a price level contains the current best bid or ask
    /// 
    /// COLOR SCHEME:
    /// - Orange (#FF8C00): Main cell background for top of book
    /// - Darker Orange (#CC7000): Volume profile bars within top of book cells
    /// - Blue: Regular bid side colors
    /// - Red: Regular ask side colors
    /// </summary>
    public class TopOfBookBackgroundConverter : IMultiValueConverter
    {
        private static readonly SolidColorBrush TopOfBookBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 140, 0)); // Dark orange - good contrast with white text
        private static readonly SolidColorBrush TopOfBookProfileBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 112, 0)); // Darker orange for volume profile
        private static readonly SolidColorBrush BidBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 152, 219)); // Blue for bid
        private static readonly SolidColorBrush BidProfileBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 118, 184)); // Darker blue for bid profile
        private static readonly SolidColorBrush AskBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)); // Red for ask
        private static readonly SolidColorBrush AskProfileBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(197, 54, 44)); // Darker red for ask profile

        private static readonly long HighlightWindowTicks = TimeSpan.FromMilliseconds(450).Ticks;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                bool isTopBid = values.Length > 0 && values[0] is bool b0 && b0;
                bool isTopAsk = values.Length > 1 && values[1] is bool b1 && b1;

                long depth = 0;
                if (values.Length > 2)
                {
                    if (values[2] is long l) depth = l;
                    else if (values[2] is int i) depth = i;
                    else if (values[2] is double d) depth = (long)d;
                }

                long lastBidHitTicks = values.Length > 3 && values[3] is long lb ? lb : 0L;
                long lastAskHitTicks = values.Length > 4 && values[4] is long la ? la : 0L;
                // values[5] = UiPulseTicks; not used directly, just to force reevaluation

                string? param = parameter as string;
                bool isProfile = param?.Contains("profile") == true;
                bool wantBid = param?.Contains("bid") == true;
                bool wantAsk = param?.Contains("ask") == true;

                var now = DateTime.UtcNow.Ticks;
                bool recentBidHit = isTopBid && lastBidHitTicks > 0 && (now - lastBidHitTicks) <= HighlightWindowTicks;
                bool recentAskHit = isTopAsk && lastAskHitTicks > 0 && (now - lastAskHitTicks) <= HighlightWindowTicks;

                // Only color orange the cell on the side that was actually hit and only if there is depth
                bool highlightThisCell = depth > 0 && ((wantBid && recentBidHit) || (wantAsk && recentAskHit));
                if (highlightThisCell)
                {
                    return isProfile ? TopOfBookProfileBrush : TopOfBookBrush;
                }

                // No recent hit for this side -> default colors by side/element
                if (isProfile)
                {
                    if (wantBid) return BidProfileBrush;
                    if (wantAsk) return AskProfileBrush;
                }
                else
                {
                    if (wantBid) return BidBrush;
                    if (wantAsk) return AskBrush;
                }

                // Fallback
                return BidBrush;
            }
            catch
            {
                return BidBrush;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}