using System;
using System.Threading.Tasks;
using BookFlow.Shared.Analytics;
using BookFlow.Shared.Contracts;

namespace BookFlow.App.Engine
{
    /// <summary>Fires a bracket entry. Returns when the entry has been submitted.</summary>
    public delegate Task AutoTradeSubmit(bool isBuy, bool entryIsLimit, double entryLimitPrice,
        int quantity, int targetTicks, int stopTicks, int entryTimeoutSeconds);

    /// <summary>
    /// Client-side automatic trader. When the master arm switch is on and a signal of an enabled
    /// type is "green" (hit-rate ≥ the configurable threshold, past learning), it fires a single
    /// bracket entry — aggression at market, level-anchored types at limit (signal price + signed
    /// offset). The NT8 host attaches the OCO target/stop on fill.
    ///
    /// Concurrency: <b>one auto-position per instrument</b> — no new entry while one is pending or
    /// open. Detection and reliability scoring are unaffected; this only reacts to signals.
    /// </summary>
    public sealed class SignalAutoTrader
    {
        private enum State { Idle, PendingEntry, InPosition }

        private readonly DomSettings _settings;
        private readonly decimal _tickSize;
        private readonly AutoTradeSubmit _submit;
        private readonly Func<double>? _toxicity;
        private readonly Action<string>? _log;
        private readonly object _gate = new object();
        private State _state = State.Idle;
        private DateTime _lastFireUtc = DateTime.MinValue;

        public SignalAutoTrader(DomSettings settings, decimal tickSize, AutoTradeSubmit submit,
            Func<double>? toxicity = null, Action<string>? log = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _tickSize = tickSize > 0 ? tickSize : 0.25m;
            _submit = submit ?? throw new ArgumentNullException(nameof(submit));
            _toxicity = toxicity;
            _log = log;
        }

        /// <summary>Drive the concurrency state machine from position updates (signed quantity).</summary>
        public void OnPositionChanged(int signedQuantity)
        {
            lock (_gate)
            {
                if (signedQuantity != 0) _state = State.InPosition;
                else if (_state == State.InPosition) _state = State.Idle; // closed (OCO hit / flatten)
            }
        }

        public void OnSignal(MicrostructureSignal sig)
        {
            if (sig == null || !Qualifies(sig, out var cfg)) return;

            bool aggression = sig.Type == MicrostructureSignalType.AggressionImbalance;
            bool entryIsLimit = !aggression && !cfg!.UseMarketOrder && sig.Price.HasValue;
            double entryPrice = entryIsLimit ? (double)(sig.Price!.Value + cfg!.OffsetTicks * _tickSize) : 0.0;
            bool isBuy = sig.Bias == MicrostructureBias.Up;

            lock (_gate)
            {
                // Reset a stuck pending entry that should have filled or timed out (server cancels
                // an unfilled limit at EntryTimeoutSeconds); +3 s grace for the round-trip.
                if (_state == State.PendingEntry &&
                    (DateTime.UtcNow - _lastFireUtc).TotalSeconds > _settings.AutoTradeEntryTimeoutSeconds + 3)
                    _state = State.Idle;

                if (_state != State.Idle) return; // one auto-position per instrument
                _state = State.PendingEntry;
                _lastFireUtc = DateTime.UtcNow;
            }

            _ = FireAsync(isBuy, entryIsLimit, entryPrice, cfg!);
        }

        private bool Qualifies(MicrostructureSignal sig, out AutoTradeConfig? cfg)
        {
            cfg = null;
            if (!_settings.AutoTradeArmed) return false;
            if (sig.Bias == MicrostructureBias.Neutral) return false;
            cfg = ConfigFor(sig.Type);
            if (cfg == null || !cfg.Enabled) return false;
            if (cfg.Size <= 0 || cfg.TargetTicks <= 0 || cfg.StopTicks <= 0) return false;
            // Configurable "green" gate: must be past learning and at/above the green hit-rate.
            if (sig.ReliabilityLearning) return false;
            if (sig.ReliabilityRatio * 100.0 < _settings.ReliabilityGreenPercent) return false;
            // Regime gate: stand aside in toxic (one-sided/informed) flow. Default max = 1.0 (off).
            if (_toxicity != null && _toxicity() > _settings.AutoTradeMaxToxicity) return false;
            return true;
        }

        private AutoTradeConfig? ConfigFor(MicrostructureSignalType t) => t switch
        {
            MicrostructureSignalType.Spoofing => _settings.SpoofingAutoTrade,
            MicrostructureSignalType.Iceberg => _settings.IcebergAutoTrade,
            MicrostructureSignalType.LiquidityWithdrawal => _settings.WithdrawalAutoTrade,
            MicrostructureSignalType.AggressionImbalance => _settings.AggressionAutoTrade,
            _ => null,
        };

        private async Task FireAsync(bool isBuy, bool entryIsLimit, double entryPrice, AutoTradeConfig cfg)
        {
            try
            {
                await _submit(isBuy, entryIsLimit, entryPrice, cfg.Size, cfg.TargetTicks, cfg.StopTicks,
                    entryIsLimit ? _settings.AutoTradeEntryTimeoutSeconds : 0);
                _log?.Invoke($"Auto-trade fired: {(isBuy ? "BUY" : "SELL")} {cfg.Size} {(entryIsLimit ? "limit @ " + entryPrice : "market")} (TP {cfg.TargetTicks} / SL {cfg.StopTicks})");
            }
            catch (Exception ex)
            {
                _log?.Invoke("Auto-trade submit failed: " + ex.Message);
                lock (_gate) { if (_state == State.PendingEntry) _state = State.Idle; } // allow retry on next signal
            }
        }
    }
}
