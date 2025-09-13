using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BookFlow.Shared.Contracts;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.AddOns;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class BookFlowIndi : Indicator
    {
        private byte _tickerId = 0;
        private bool _enableLogging = true;
        private bool _enableLevel1Logging = true;
        private bool _enableLevel2Logging = true;
        private bool _enableOtherEventLogging = true;
        private string _logPrefix;
        private bool _isRegistered = false;
        private long _sequenceNumber = 0;

        [NinjaScriptProperty]
        [Display(Name = "Enable Logging", Description = "Master switch for all logging to NinjaScript Output", Order = 1, GroupName = "Debug")]
        public bool EnableLogging { get => _enableLogging; set => _enableLogging = value; }

        [NinjaScriptProperty]
        [Display(Name = "Log Level 1 Data", Description = "Log Level 1 market data (Bid, Ask, Last, etc.)", Order = 2, GroupName = "Debug")]
        public bool EnableLevel1Logging { get => _enableLevel1Logging; set => _enableLevel1Logging = value; }

        [NinjaScriptProperty]
        [Display(Name = "Log Level 2 Data", Description = "Log Level 2 market depth (Order Book)", Order = 3, GroupName = "Debug")]
        public bool EnableLevel2Logging { get => _enableLevel2Logging; set => _enableLevel2Logging = value; }

        [NinjaScriptProperty]
        [Display(Name = "Log Other Events", Description = "Log Bar updates and other events", Order = 4, GroupName = "Debug")]
        public bool EnableOtherEventLogging { get => _enableOtherEventLogging; set => _enableOtherEventLogging = value; }

        [Display(Name = "Ticker ID", Description = "Assigned ticker ID from global AddOn", Order = 5, GroupName = "Status")]
        public byte TickerId => _tickerId;

        private void Log(string message) { if (_enableLogging) Print($"{_logPrefix} {DateTime.Now:HH:mm:ss.fff} - {message}"); }
        private void LogLevel1(string message) { if (_enableLogging && _enableLevel1Logging) Print($"{_logPrefix} L1 {DateTime.Now:HH:mm:ss.fff} - {message}"); }
        private void LogLevel2(string message) { if (_enableLogging && _enableLevel2Logging) Print($"{_logPrefix} L2 {DateTime.Now:HH:mm:ss.fff} - {message}"); }
        private void LogOtherEvent(string message) { if (_enableLogging && _enableOtherEventLogging) Print($"{_logPrefix} EVT {DateTime.Now:HH:mm:ss.fff} - {message}"); }

        private string FormatPrice(double price) => (double.IsNaN(price) || double.IsInfinity(price)) ? "N/A" : (Math.Abs(price) > 999999999 ? price.ToString("E2") : price.ToString("0.####"));
        private string FormatVolume(long volume) => volume < 0 ? "N/A" : (volume > 999999999 ? volume.ToString("E0") : volume.ToString("N0"));
        private string FormatTimestamp(long ticks) { try { if (ticks <= 0) return "N/A"; return new DateTime(ticks).ToString("HH:mm:ss.fff"); } catch { return "INV"; } }
        private string FormatSequence(long sequence) => sequence > 999999999 ? sequence.ToString("E0") : sequence.ToString("N0");

        private string GetL1TypeName(MarketDataType mdType) => mdType switch
        {
            MarketDataType.Ask => "ASK",
            MarketDataType.Bid => "BID",
            MarketDataType.Last => "LAST",
            MarketDataType.DailyHigh => "HIGH",
            MarketDataType.DailyLow => "LOW",
            MarketDataType.DailyVolume => "VOL",
            MarketDataType.Opening => "OPEN",
            MarketDataType.LastClose => "CLS",
            MarketDataType.Settlement => "SET",
            MarketDataType.OpenInterest => "OI",
            _ => "UNK"
        };

        private string GetL2OperationName(Operation operation) => operation switch
        {
            Operation.Add => "ADD",
            Operation.Update => "UPD",
            Operation.Remove => "DEL",
            _ => "UNK"
        };
        private string GetL2SideName(MarketDataType mdType) => mdType switch { MarketDataType.Ask => "ASK", MarketDataType.Bid => "BID", _ => "UNK" };

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "BookFlow Indicator for unified global data streaming";
                Name = "BookFlowIndi";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;
                _logPrefix = "[BookFlowIndi]";
            }
            else if (State == State.DataLoaded)
            {
                try { _logPrefix = $"[BookFlowIndi-{Instrument.FullName}]"; RegisterWithAddOn(); }
                catch (Exception ex) { Log($"Setup error: {ex.Message}"); }
            }
        }

        private void RegisterWithAddOn()
        {
            var addOn = BookFlowAddOn.Instance ?? throw new Exception("BookFlow AddOn not available");
            _tickerId = addOn.RegisterTicker(Instrument.FullName, Instrument.MasterInstrument.TickSize, Instrument.MasterInstrument.PointValue);
            _isRegistered = true;
            Log($"Ticker registration: ID {_tickerId} for {Instrument.FullName}");
        }

        private static byte MapL1Type(MarketDataType mdType) => mdType switch
        {
            MarketDataType.Ask => (byte)L1MarketDataType.Ask,
            MarketDataType.Bid => (byte)L1MarketDataType.Bid,
            MarketDataType.Last => (byte)L1MarketDataType.Last,
            MarketDataType.DailyHigh => (byte)L1MarketDataType.DailyHigh,
            MarketDataType.DailyLow => (byte)L1MarketDataType.DailyLow,
            MarketDataType.DailyVolume => (byte)L1MarketDataType.DailyVolume,
            _ => (byte)L1MarketDataType.Last
        };

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            try
            {
                if (!_isRegistered || _tickerId == 0) return;
                var ntReceiveTime = Stopwatch.GetTimestamp();
                var seq = Interlocked.Increment(ref _sequenceNumber);
                var message = new UnifiedMarketDataMessage
                {
                    Category = MessageCategory.L1Data,
                    MarketDataType = MapL1Type(e.MarketDataType),
                    Price = e.Price,
                    Volume = e.Volume,
                    AskPrice = e.Ask,
                    BidPrice = e.Bid,
                    TickerId = _tickerId,
                    OriginalTimestamp = e.Time.Ticks,
                    NtReceiveTime = ntReceiveTime,
                    Sequence = seq
                };
                LogLevel1($"T{_tickerId:D2} {GetL1TypeName(e.MarketDataType),-4} P:{FormatPrice(e.Price),-10} V:{FormatVolume(e.Volume),-8} B:{FormatPrice(e.Bid),-10} A:{FormatPrice(e.Ask),-10} TS:{FormatTimestamp(e.Time.Ticks)} S#{FormatSequence(seq)}");
                BookFlowAddOn.Instance?.WriteToGlobalChannel(_tickerId, message);
            }
            catch (Exception ex) { Log($"Market data error: {ex.Message}"); }
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            try
            {
                if (!_isRegistered || _tickerId == 0) return;
                var ntReceiveTime = Stopwatch.GetTimestamp();
                var seq = Interlocked.Increment(ref _sequenceNumber);
                var message = new UnifiedMarketDataMessage
                {
                    Category = MessageCategory.L2Data,
                    MarketDataType = (byte)(e.MarketDataType == MarketDataType.Ask ? 1 : 2),
                    Operation = (byte)e.Operation,
                    Price = e.Price,
                    Volume = e.Volume,
                    Position = e.Position,
                    TickerId = _tickerId,
                    OriginalTimestamp = e.Time.Ticks,
                    NtReceiveTime = ntReceiveTime,
                    Sequence = seq
                };
                LogLevel2($"T{_tickerId:D2} {GetL2OperationName(e.Operation),-3} {GetL2SideName(e.MarketDataType),-3} @{e.Position:D2} P:{FormatPrice(e.Price),-10} V:{FormatVolume(e.Volume),-8} TS:{FormatTimestamp(e.Time.Ticks)} S#{FormatSequence(seq)}");
                BookFlowAddOn.Instance?.WriteToGlobalChannel(_tickerId, message);
            }
            catch (Exception ex) { Log($"Market depth error: {ex.Message}"); }
        }

        protected override void OnBarUpdate()
        {
            try
            {
                if (!_isRegistered || _tickerId == 0 || !IsFirstTickOfBar) return;
                var ntReceiveTime = Stopwatch.GetTimestamp();
                var seq = Interlocked.Increment(ref _sequenceNumber);
                var message = new UnifiedMarketDataMessage
                {
                    Category = MessageCategory.L1Data,
                    MarketDataType = (byte)L1MarketDataType.Last,
                    Price = Close[0],
                    Volume = (long)Volume[0],
                    AskPrice = GetCurrentAsk(),
                    BidPrice = GetCurrentBid(),
                    TickerId = _tickerId,
                    OriginalTimestamp = Time[0].Ticks,
                    NtReceiveTime = ntReceiveTime,
                    Sequence = seq
                };
                LogOtherEvent($"T{_tickerId:D2} BAR O:{FormatPrice(Open[0]),-10} H:{FormatPrice(High[0]),-10} L:{FormatPrice(Low[0]),-10} C:{FormatPrice(Close[0]),-10} V:{FormatVolume((long)Volume[0]),-8} TS:{FormatTimestamp(Time[0].Ticks)} S#{FormatSequence(seq)}");
                BookFlowAddOn.Instance?.WriteToGlobalChannel(_tickerId, message);
            }
            catch (Exception ex) { Log($"Bar update error: {ex.Message}"); }
        }
    }
}
