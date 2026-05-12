#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// ============================================================================
// EVOLUTION 1.0 — REGIME FILTERS | ES FUTURES | NINJATRADER 8
// ============================================================================
// Base madre preservada: EVOLUTION 1.0 (core Trend + Range intactos).
// Nuevos módulos:
//   P1 · ATR Percentile Rank        — bloquea días de volatilidad extrema
//   P2 · HTF 60m EMA                — solo Range si precio > EMA-20 en 60m
//   P3 · Opening Range Bias         — solo Range Longs si sesión es alcista
//   P4 · Circuit Breaker            — pausa tras N pérdidas consecutivas
//   P6 · Bollinger Band Width       — filtra compresión/expansión extrema
//   P7 · Range Quality Score        — score ponderado, mínimo para entrar
//   P8 · Synthetic VIX              — ratio ATR corto/largo vs umbral
//   P9 · Bug fix activeEntrySignal  — timeout/exit usa la señal correcta
// Objetivo: reducir sangrado 2018-2022 sin destruir edge 2023-2025.
// ============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class EVOLUTION_1_0 : Strategy
    {
        // ─── CORE PARAMS ─────────────────────────────────────────────────────
        private double rr               = 3.0;
        private double bufferPts        = 0.30;
        private double pullbackTol      = 0.50;
        private double bodyMult         = 0.60;
        private double rangeMult        = 1.20;
        private int    maxHoldBars      = 10;
        private int    maxBreakLookback = 10;
        private int    maxTradesPerDay  = 10;

        private int startTime = 93000;
        private int endTime   = 150000;

        private bool useMonday    = false;
        private bool useTuesday   = true;
        private bool useWednesday = true;
        private bool useThursday  = true;
        private bool useFriday    = false;

        private double initialCapital = 100000;
        private double riskPerTrade   = 0.0025;
        private double pointValue     = 50;
        private double maxRiskPoints  = 20;

        private bool useTrendMode  = true;
        private bool useRangeMode  = true;
        private bool useRangeLongs = true;

        private double rangeRR             = 1.80;
        private double rangeBufferPts      = 0.25;
        private int    rangeLookback       = 30;
        private double touchTolerancePts   = 0.35;
        private double wickMinRatio        = 0.20;
        private double rangeExtremeZone    = 0.35;
        private double intradayCompression = 0.80;
        private int    rangeMaxHoldBars    = 8;

        private double rangeMinAtrMult     = 0.90;
        private double rangeAdxMax         = 20.0;  // tightened: was 23
        private int    rangeCooldownBars   = 2;
        private double rangeMidDeadZone    = 0.12;
        private double rangeStrengthMin    = 1.80;  // tightened: was 1.50
        private int    rangeMinTouches     = 2;

        // ─── RANGE TIME WINDOW & TREND-DAY EXCLUSION ─────────────────────────
        private int  rangeWindowStart       = 103000; // 10:30 AM — best mean-reversion window
        private int  rangeWindowEnd         = 140000; // 2:00 PM
        private bool useTrendDayExclusion   = true;   // skip range on breakout days
        private bool   useSweepRecovery    = true;
        private double sweepRecoveryBuffer = 0.10;
        private double sweepMinRangeAtr    = 0.35;
        private bool   useHtfEmaFilter     = true;
        private int    htfEmaPeriod        = 50;
        private double displacementAtrMult = 1.50;
        private int    preCompressionBars  = 8;
        private double preCompressionMax   = 0.75;

        // ─── INDICATORS ──────────────────────────────────────────────────────
        private SMA       sma50;
        private ATR       atr14;
        private ADX       adx14;
        private EMA       ema50_5m;
        private EMA       ema20_60m;   // P2: HTF 60m
        private Bollinger bb20;        // P6: BB Width

        // ─── DAY TRACKING ────────────────────────────────────────────────────
        private double   prevDayHigh;
        private double   prevDayLow;
        private double   currDayHigh;
        private double   currDayLow;
        private DateTime lastDate;
        private bool     prevDayValid;

        private Queue<double> prevDayRanges;
        private double prevDayRangeSum;
        private double avgPrevDayRange;

        private int    tradesToday;
        private int    entryBarNumber;
        private int    lastExitBar;
        private Order  entryOrder;
        private int    pendingOrderBar;

        // ─── P9: ACTIVE ENTRY SIGNAL (bug fix) ───────────────────────────────
        private string activeEntrySignal = "";

        // ─── DAILY LOSS LIMIT ─────────────────────────────────────────────────
        private double cumProfitInicioDia  = 0.0;
        private bool   tradingBloqueadoHoy = false;

        // ─── P2: HTF 60m EMA filter ───────────────────────────────────────────
        private bool useHtf60mFilter = true;
        private int  htf60mEmaPeriod = 20;

        // ─── P1: ATR Percentile Rank ──────────────────────────────────────────
        private Queue<double> dailyAtrHistory = new Queue<double>();
        private double lastDayAtr     = 0.0;
        private bool   atrRegimeOk    = true;
        private int    atrHistoryDays   = 60;
        private double atrPercentileMax = 75.0;

        // ─── P3: Opening Range Bias ───────────────────────────────────────────
        private double orHigh          = double.MinValue;
        private double orLow           = double.MaxValue;
        private bool   orComplete      = false;
        private bool   orBiasLong      = false;
        private bool   useOrBiasFilter = true;
        private int    orEndTimeInt    = 100000; // 10:00 AM

        // ─── P4: Circuit Breaker ──────────────────────────────────────────────
        private int  consecutiveLosses     = 0;
        private bool circuitBreakerActive  = false;
        private int  circuitBreakerDayCount = 0;
        private int  maxConsecutiveLosses  = 3;
        private int  circuitBreakerDays    = 3;

        // ─── P6: BB Width filter ──────────────────────────────────────────────
        // bbwMin/bbwMax expressed as multiples of ATR (e.g., 1.5 = 1.5x ATR wide)
        private bool   useBbwFilter = true;
        private double bbwMin       = 1.5;
        private double bbwMax       = 6.0;

        // ─── P7: Range Quality Score ──────────────────────────────────────────
        private int rangeQualityMin = 60;  // tightened: was 50

        // ─── P8: Synthetic VIX (ATR ratio) ───────────────────────────────────
        private bool   useSyntheticVix = true;
        private double syntheticVixMax = 2.0;

        private const string TrendEntrySignal     = "EVO_TREND_LONG";
        private const string RangeLongSignal      = "EVO_RANGE_LONG";
        private const string RangeSweepLongSignal = "EVO_SWEEP_RECOVERY_LONG";

        // ─── STATE ────────────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                         = "EVOLUTION 1.0";
                Description                  = "EVOLUTION 1.0 clean locked baseline. Daily Loss Limit activo.";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 100;
                TraceOrders                  = false;
                DefaultQuantity              = 1;
                IsInstantiatedOnEachOptimizationIteration = false;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);   // index 1: 5m
                AddDataSeries(BarsPeriodType.Minute, 60);  // index 2: 60m (P2)
            }
            else if (State == State.DataLoaded)
            {
                sma50     = SMA(Close, 50);
                atr14     = ATR(14);
                adx14     = ADX(14);
                ema50_5m  = EMA(Closes[1], htfEmaPeriod);
                ema20_60m = EMA(Closes[2], htf60mEmaPeriod);
                bb20      = Bollinger(Close, 2.0, 20);

                prevDayHigh = 0;
                prevDayLow  = 0;
                currDayHigh = double.MinValue;
                currDayLow  = double.MaxValue;
                lastDate    = DateTime.MinValue;
                prevDayValid = false;

                prevDayRanges   = new Queue<double>();
                prevDayRangeSum = 0;
                avgPrevDayRange = 0;

                tradesToday     = 0;
                entryBarNumber  = -1;
                lastExitBar     = -100000;
                entryOrder      = null;
                pendingOrderBar = -1;

                cumProfitInicioDia  = 0.0;
                tradingBloqueadoHoy = false;
                activeEntrySignal   = "";

                dailyAtrHistory = new Queue<double>();
                lastDayAtr      = 0.0;
                atrRegimeOk     = true;

                orHigh     = double.MinValue;
                orLow      = double.MaxValue;
                orComplete = false;
                orBiasLong = false;

                consecutiveLosses      = 0;
                circuitBreakerActive   = false;
                circuitBreakerDayCount = 0;
            }
        }

        // ─── BAR UPDATE ───────────────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < BarsRequiredToTrade || CurrentBar < Math.Max(rangeLookback + 5, 100))
                return;

            if (CurrentBars.Length > 1 && CurrentBars[1] < htfEmaPeriod + 5)
                return;

            if (CurrentBars.Length > 2 && CurrentBars[2] < htf60mEmaPeriod + 5)
                return;

            UpdateDailyState();
            UpdateOpeningRange();
            CancelExpiredEntryOrder();

            // ── DAILY LOSS LOCK ───────────────────────────────────────────────
            double pnlDia = ObtenerPnLDia(Close[0]);
            if (!tradingBloqueadoHoy && pnlDia <= -DailyLossUSD)
            {
                tradingBloqueadoHoy = true;
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    string exitSig = string.IsNullOrEmpty(activeEntrySignal)
                        ? TrendEntrySignal : activeEntrySignal;
                    ExitLong("ExitDailyLoss", exitSig);
                }
                return;
            }
            if (tradingBloqueadoHoy) return;

            // ── P9 BUG FIX: correct hold limit and exit signal per mode ───────
            if (Position.MarketPosition == MarketPosition.Long)
            {
                string exitSig  = string.IsNullOrEmpty(activeEntrySignal)
                    ? TrendEntrySignal : activeEntrySignal;
                int    holdLimit = (exitSig == TrendEntrySignal) ? maxHoldBars : rangeMaxHoldBars;

                if (entryBarNumber >= 0 && CurrentBar - entryBarNumber >= holdLimit)
                    ExitLong("Timeout", exitSig);

                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (entryOrder != null)
                return;

            if (ToTime(Time[0]) < startTime || ToTime(Time[0]) > endTime)
                return;

            if (!IsTradingDayAllowed(Time[0].DayOfWeek))
                return;

            if (tradesToday >= maxTradesPerDay)
                return;

            // ── P4: CIRCUIT BREAKER ───────────────────────────────────────────
            if (circuitBreakerActive) return;

            bool trendTriggered = false;

            if (useTrendMode)
                trendTriggered = TryTrendMode();

            if (!trendTriggered && useRangeMode)
                TryRangeMode();
        }

        // ─── TRADING DAY FILTER ───────────────────────────────────────────────

        private bool IsTradingDayAllowed(DayOfWeek day)
        {
            if (day == DayOfWeek.Monday)    return useMonday;
            if (day == DayOfWeek.Tuesday)   return useTuesday;
            if (day == DayOfWeek.Wednesday) return useWednesday;
            if (day == DayOfWeek.Thursday)  return useThursday;
            if (day == DayOfWeek.Friday)    return useFriday;
            return false;
        }

        // ─── TREND MODE ───────────────────────────────────────────────────────

        private bool TryTrendMode()
        {
            if (!prevDayValid || avgPrevDayRange <= 0)
                return false;

            double prevDayRange = prevDayHigh - prevDayLow;
            if (prevDayRange <= avgPrevDayRange)
                return false;

            double avgRangeVal = AverageRange(20, 0);
            if (avgRangeVal <= 0)
                return false;

            if (!RecentBreakOccurred())
                return false;

            if (!(Low[0] <= (prevDayHigh + pullbackTol)))
                return false;

            double body      = Math.Abs(Close[0] - Open[0]);
            double candleRng = High[0] - Low[0];

            bool confirmed =
                Close[0] > Open[0]
                && body      > avgRangeVal * bodyMult
                && candleRng > avgRangeVal * rangeMult
                && Close[0]  > sma50[0];

            if (!confirmed)
                return false;

            double trigger   = Instrument.MasterInstrument.RoundToTickSize(High[0] + 2 * TickSize);
            double stopPrice = Instrument.MasterInstrument.RoundToTickSize(
                Math.Min(Low[0], prevDayHigh) - bufferPts);
            double risk      = trigger - stopPrice;

            if (risk <= 0 || risk > maxRiskPoints)
                return false;

            double equity       = initialCapital + SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            double riskUsd      = equity * riskPerTrade;
            double contractRisk = risk * pointValue;
            int    qty          = (int)Math.Floor(riskUsd / contractRisk);

            if (qty < 1)
                return false;

            double targetPrice = Instrument.MasterInstrument.RoundToTickSize(trigger + rr * risk);

            SetStopLoss(TrendEntrySignal,    CalculationMode.Price, stopPrice,   false);
            SetProfitTarget(TrendEntrySignal, CalculationMode.Price, targetPrice);

            activeEntrySignal = TrendEntrySignal;
            entryOrder        = EnterLongStopMarket(0, true, qty, trigger, TrendEntrySignal);
            pendingOrderBar   = CurrentBar;

            return true;
        }

        // ─── RANGE MODE ───────────────────────────────────────────────────────

        private void TryRangeMode()
        {
            if (CurrentBar - lastExitBar < rangeCooldownBars)
                return;

            // ── RANGE TIME WINDOW: best mean-reversion session ────────────────
            int rt = ToTime(Time[0]);
            if (rt < rangeWindowStart || rt > rangeWindowEnd) return;

            // ── TREND DAY EXCLUSION: don't fade range on breakout days ─────────
            if (useTrendDayExclusion && IsTrendDay()) return;

            double rangeHigh = MAX(High, rangeLookback)[1];
            double rangeLow  = MIN(Low,  rangeLookback)[1];
            double rangeSize = rangeHigh - rangeLow;

            if (rangeSize <= 0)  return;
            if (atr14[0]   <= 0) return;

            // ── STRUCTURAL GATES (hard) ───────────────────────────────────────
            if (adx14[0] > rangeAdxMax)                        return;
            if (rangeSize < atr14[0] * rangeMinAtrMult)        return;
            if (rangeSize / atr14[0] < rangeStrengthMin)       return;

            int touchesLow = 0, touchesHigh = 0;
            for (int i = 1; i <= rangeLookback; i++)
            {
                if (Math.Abs(Low[i]  - rangeLow)  <= touchTolerancePts) touchesLow++;
                if (Math.Abs(High[i] - rangeHigh) <= touchTolerancePts) touchesHigh++;
            }
            if (touchesLow < rangeMinTouches && touchesHigh < rangeMinTouches) return;

            double avgRangeVal = AverageRange(20, 0);
            if (avgRangeVal <= 0) return;

            if ((High[0] - Low[0]) / avgRangeVal > intradayCompression) return;

            double mid = (rangeHigh + rangeLow) / 2.0;
            if (Math.Abs(Close[0] - mid) < rangeSize * rangeMidDeadZone) return;

            // ── REGIME FILTERS (P1, P2, P3, P6, P7, P8) ─────────────────────
            if (!Is60mTrendBullish())                                    return; // P2
            if (!atrRegimeOk)                                            return; // P1
            if (useOrBiasFilter && orComplete && !orBiasLong)           return; // P3
            if (!IsBbwInRange())                                         return; // P6
            if (useSyntheticVix && CalcSyntheticVix() > syntheticVixMax)               return; // P8
            if (CalcRangeQualityScore(rangeSize, touchesLow, touchesHigh) < rangeQualityMin) return; // P7

            // ── CANDLE SETUP ──────────────────────────────────────────────────
            double lowerWick     = Math.Min(Open[0], Close[0]) - Low[0];
            double candleRange   = High[0] - Low[0];
            if (candleRange <= 0) return;

            double lowerWickRatio = lowerWick / candleRange;
            double lowerExtreme   = rangeLow + rangeSize * rangeExtremeZone;
            bool   longZone       = Low[0] <= lowerExtreme + touchTolerancePts && Close[0] > rangeLow;
            bool   htfLongOk      = !useHtfEmaFilter || Close[0] > ema50_5m[0];

            if (useRangeLongs && !htfLongOk) return;

            bool preCompressionOk = IsPreCompressionValid(preCompressionBars, preCompressionMax);
            bool displacementOk   = Close[0] > High[1] && (High[0] - Low[0]) >= atr14[0] * displacementAtrMult;

            // SETUP A — CORE: Range Rejection. Base limpia. No tocar.
            if (useRangeLongs
                && longZone
                && lowerWickRatio >= wickMinRatio
                && (Close[0] > Open[0] || Close[0] > Close[1]))
            {
                double stopPrice  = Instrument.MasterInstrument.RoundToTickSize(
                    Math.Min(Low[0], rangeLow) - rangeBufferPts);
                double risk = Close[0] - stopPrice;

                if (risk <= 0 || risk > maxRiskPoints) return;

                double targetPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + risk * rangeRR);

                SetStopLoss(RangeLongSignal,    CalculationMode.Price, stopPrice,   false);
                SetProfitTarget(RangeLongSignal, CalculationMode.Price, targetPrice);

                activeEntrySignal = RangeLongSignal;
                EnterLong(1, RangeLongSignal);
                entryBarNumber = CurrentBar;
                tradesToday++;
                return;
            }

            // SETUP B — SECONDARY: Liquidity Sweep Recovery.
            double sweepBody  = Math.Abs(Close[0] - Open[0]);
            double sweepRange = High[0] - Low[0];
            bool   strongClose     = sweepRange > 0 && sweepBody > sweepRange * 0.50;
            bool   reclaimStrength = Close[0] > Open[0] && Close[0] > Close[1];

            bool sweepRecoveryLong =
                useSweepRecovery
                && useRangeLongs
                && Low[0]    < rangeLow
                && Close[0]  > rangeLow + sweepRecoveryBuffer
                && Close[0]  > Close[1]
                && (High[0] - Low[0]) >= atr14[0] * sweepMinRangeAtr
                && Close[0]  > Low[0] + (High[0] - Low[0]) * 0.50
                && strongClose
                && reclaimStrength
                && displacementOk
                && preCompressionOk;

            if (sweepRecoveryLong)
            {
                double stopPrice  = Instrument.MasterInstrument.RoundToTickSize(Low[0] - rangeBufferPts);
                double risk       = Close[0] - stopPrice;

                if (risk <= 0 || risk > maxRiskPoints) return;

                double targetPrice = Instrument.MasterInstrument.RoundToTickSize(Close[0] + risk * rangeRR);

                SetStopLoss(RangeSweepLongSignal,    CalculationMode.Price, stopPrice,   false);
                SetProfitTarget(RangeSweepLongSignal, CalculationMode.Price, targetPrice);

                activeEntrySignal = RangeSweepLongSignal;
                EnterLong(1, RangeSweepLongSignal);
                entryBarNumber = CurrentBar;
                tradesToday++;
            }
        }

        // ─── REGIME FILTER METHODS ────────────────────────────────────────────

        // P2: True if price is above 20-EMA on 60-minute chart (or filter disabled)
        private bool Is60mTrendBullish()
        {
            if (!useHtf60mFilter) return true;
            if (CurrentBars.Length < 3 || CurrentBars[2] < htf60mEmaPeriod + 5) return true;
            return Closes[2][0] > ema20_60m[0];
        }

        // P1: Update atrRegimeOk — block if today's ATR exceeds atrPercentileMax percentile
        private void UpdateAtrRegime()
        {
            if (dailyAtrHistory.Count < 20)
            {
                atrRegimeOk = true;
                return;
            }

            double currentAtr = atr14[0];
            double[] sorted   = dailyAtrHistory.ToArray();
            Array.Sort(sorted);

            int count = sorted.Length;
            int rank  = 0;
            for (int i = 0; i < count; i++)
                if (sorted[i] <= currentAtr) rank++;

            double percentile = (double)rank / count * 100.0;
            atrRegimeOk = percentile <= atrPercentileMax;
        }

        // P3: Track opening range 9:30–orEndTimeInt, set bias at close of that window
        private void UpdateOpeningRange()
        {
            if (orComplete) return;

            int t            = ToTime(Time[0]);
            int sessionStart = 93000;

            if (t >= sessionStart && t < orEndTimeInt)
            {
                if (High[0] > orHigh || orHigh == double.MinValue) orHigh = High[0];
                if (Low[0]  < orLow  || orLow  == double.MaxValue) orLow  = Low[0];
            }
            else if (t >= orEndTimeInt && orHigh > double.MinValue)
            {
                orComplete = true;
                orBiasLong = Close[0] > (orHigh + orLow) / 2.0;
            }
        }

        // P6: BB Width as multiple of ATR — [bbwMin, bbwMax]
        private bool IsBbwInRange()
        {
            if (!useBbwFilter) return true;
            if (bb20 == null || atr14 == null || CurrentBar < 25) return true;
            if (atr14[0] <= 0) return true;

            double bbWidth    = bb20.Upper[0] - bb20.Lower[0];
            double bbwAtrRatio = bbWidth / atr14[0];
            return bbwAtrRatio >= bbwMin && bbwAtrRatio <= bbwMax;
        }

        // P8: Synthetic VIX — ratio ATR(5) / ATR(20). >syntheticVixMax = spike
        private double CalcSyntheticVix()
        {
            if (CurrentBar < 25) return 1.0;
            double shortAtr = AverageRange(5,  0);
            double longAtr  = AverageRange(20, 0);
            if (longAtr <= 0) return 1.0;
            return shortAtr / longAtr;
        }

        // P7: Range Quality Score 0–100. Weighted sum including touch quality.
        private int CalcRangeQualityScore(double rangeSize, int touchesLow, int touchesHigh)
        {
            int score = 0;

            // Range definition vs ATR — how well-defined is the channel? (0–30 pts)
            double rsRatio = atr14[0] > 0 ? rangeSize / atr14[0] : 0;
            if      (rsRatio >= 2.5) score += 30;
            else if (rsRatio >= 2.0) score += 20;
            else if (rsRatio >= 1.5) score += 10;

            // Touch quality — confirmed bounces = stronger range (0–30 pts)
            int maxTouches = Math.Max(touchesLow, touchesHigh);
            if      (maxTouches >= 4) score += 30;
            else if (maxTouches >= 3) score += 20;
            else if (maxTouches >= 2) score += 10;

            // ADX quality — lower = more confirmed range (0–25 pts)
            if      (adx14[0] < 12) score += 25;
            else if (adx14[0] < 18) score += 15;
            else if (adx14[0] < 23) score += 8;

            // Pre-compression ok — market calming before entry (0–15 pts)
            if (IsPreCompressionValid(preCompressionBars, preCompressionMax)) score += 15;

            return score; // max 100
        }

        // True if today is a trend/breakout day — range mode should stand aside
        private bool IsTrendDay()
        {
            if (!prevDayValid || avgPrevDayRange <= 0) return false;
            // Big prior-day range AND recent breakout above prevDayHigh = trending day
            return (prevDayHigh - prevDayLow) > avgPrevDayRange * 1.20
                && RecentBreakOccurred();
        }

        // ─── HELPER METHODS ───────────────────────────────────────────────────

        private bool IsPreCompressionValid(int period, double maxCompression)
        {
            if (period < 2) return true;
            if (CurrentBar < period + 25) return false;

            double avgRangeVal = AverageRange(20, period + 1);
            if (avgRangeVal <= 0) return false;

            double sum = 0.0;
            for (int i = 1; i <= period; i++)
                sum += High[i] - Low[i];

            return (sum / period) <= avgRangeVal * maxCompression;
        }

        private double AverageRange(int period, int barsAgoStart)
        {
            if (CurrentBar < barsAgoStart + period) return 0;
            double sum = 0;
            for (int i = barsAgoStart; i < barsAgoStart + period; i++)
                sum += High[i] - Low[i];
            return sum / period;
        }

        private bool RecentBreakOccurred()
        {
            for (int barsAgo = 1; barsAgo <= maxBreakLookback; barsAgo++)
            {
                if (CurrentBar < barsAgo + 20) break;
                double avgR = AverageRange(20, barsAgo);
                if (avgR <= 0) continue;
                if (Close[barsAgo] > (prevDayHigh + avgR * 0.50)) return true;
            }
            return false;
        }

        private void UpdateDailyState()
        {
            DateTime barDate = Time[0].Date;

            if (lastDate == DateTime.MinValue)
            {
                lastDate    = barDate;
                currDayHigh = High[0];
                currDayLow  = Low[0];
                tradesToday = 0;
                return;
            }

            if (barDate != lastDate)
            {
                if (currDayHigh > double.MinValue && currDayLow < double.MaxValue)
                {
                    prevDayHigh  = currDayHigh;
                    prevDayLow   = currDayLow;
                    prevDayValid = true;

                    double dr = currDayHigh - currDayLow;
                    if (dr > 0)
                    {
                        prevDayRanges.Enqueue(dr);
                        prevDayRangeSum += dr;

                        if (prevDayRanges.Count > 20)
                            prevDayRangeSum -= prevDayRanges.Dequeue();

                        avgPrevDayRange = prevDayRanges.Count >= 20
                            ? prevDayRangeSum / prevDayRanges.Count
                            : 0;
                    }

                    // P1: persist yesterday's ATR into history
                    if (lastDayAtr > 0)
                    {
                        dailyAtrHistory.Enqueue(lastDayAtr);
                        if (dailyAtrHistory.Count > atrHistoryDays)
                            dailyAtrHistory.Dequeue();
                        UpdateAtrRegime();
                    }
                }

                currDayHigh = High[0];
                currDayLow  = Low[0];
                lastDate    = barDate;

                tradesToday         = 0;
                entryBarNumber      = -1;
                tradingBloqueadoHoy = false;
                try { cumProfitInicioDia = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit; } catch { }

                // P3: reset opening range for new session
                orHigh     = double.MinValue;
                orLow      = double.MaxValue;
                orComplete = false;
                orBiasLong = false;

                // P4: circuit breaker day countdown
                if (circuitBreakerActive && --circuitBreakerDayCount <= 0)
                    circuitBreakerActive = false;

                if (entryOrder != null)
                {
                    CancelOrder(entryOrder);
                    entryOrder      = null;
                    pendingOrderBar = -1;
                }
            }
            else
            {
                if (High[0] > currDayHigh) currDayHigh = High[0];
                if (Low[0]  < currDayLow)  currDayLow  = Low[0];
                lastDayAtr = atr14[0]; // updated every bar; captured at day boundary
            }
        }

        private void CancelExpiredEntryOrder()
        {
            if (entryOrder == null) return;
            if (Position.MarketPosition != MarketPosition.Flat) return;
            if (CurrentBar > pendingOrderBar + 1)
            {
                CancelOrder(entryOrder);
                entryOrder      = null;
                pendingOrderBar = -1;
            }
        }

        // ─── ORDER / EXECUTION UPDATES ────────────────────────────────────────

        protected override void OnOrderUpdate(
            Order order,
            double limitPrice,
            double stopPrice,
            int quantity,
            int filled,
            double averageFillPrice,
            OrderState orderState,
            DateTime time,
            ErrorCode error,
            string nativeError)
        {
            if (order == null) return;

            if (order.Name == TrendEntrySignal)
            {
                if (orderState == OrderState.Filled)
                {
                    entryOrder      = null;
                    pendingOrderBar = -1;
                    entryBarNumber  = CurrentBar;
                    tradesToday++;
                }
                else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    entryOrder        = null;
                    pendingOrderBar   = -1;
                    activeEntrySignal = ""; // clear if trend order never filled
                }
            }
        }

        protected override void OnExecutionUpdate(
            Execution execution,
            string executionId,
            double price,
            int quantity,
            MarketPosition marketPosition,
            string orderId,
            DateTime time)
        {
            if (execution == null || execution.Order == null) return;
            if (execution.Order.OrderState != OrderState.Filled) return;

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                entryBarNumber    = -1;
                lastExitBar       = CurrentBar;
                activeEntrySignal = "";

                // P4: track consecutive losses for circuit breaker
                try
                {
                    int cnt = SystemPerformance.AllTrades.Count;
                    if (cnt > 0)
                    {
                        double lastProfit = SystemPerformance.AllTrades[cnt - 1].ProfitCurrency;
                        if (lastProfit < 0)
                        {
                            consecutiveLosses++;
                            if (consecutiveLosses >= maxConsecutiveLosses)
                            {
                                circuitBreakerActive   = true;
                                circuitBreakerDayCount = circuitBreakerDays;
                            }
                        }
                        else
                        {
                            consecutiveLosses = 0;
                        }
                    }
                }
                catch { }
            }
            else
            {
                if (execution.Order.Name == RangeLongSignal)
                    entryBarNumber = CurrentBar;
            }
        }

        // ─── NINJASCRIPT PROPERTIES ───────────────────────────────────────────

        // 00. Time / Day Filter
        [NinjaScriptProperty]
        [Display(Name = "Start time HHmmss", Order = 1, GroupName = "00. Time / Day Filter")]
        public int StartTime
        { get { return startTime; } set { startTime = Math.Max(0, Math.Min(235959, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "End time HHmmss", Order = 2, GroupName = "00. Time / Day Filter")]
        public int EndTime
        { get { return endTime; } set { endTime = Math.Max(0, Math.Min(235959, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Use Monday", Order = 3, GroupName = "00. Time / Day Filter")]
        public bool UseMonday
        { get { return useMonday; } set { useMonday = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Use Tuesday", Order = 4, GroupName = "00. Time / Day Filter")]
        public bool UseTuesday
        { get { return useTuesday; } set { useTuesday = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Use Wednesday", Order = 5, GroupName = "00. Time / Day Filter")]
        public bool UseWednesday
        { get { return useWednesday; } set { useWednesday = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Use Thursday", Order = 6, GroupName = "00. Time / Day Filter")]
        public bool UseThursday
        { get { return useThursday; } set { useThursday = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Use Friday", Order = 7, GroupName = "00. Time / Day Filter")]
        public bool UseFriday
        { get { return useFriday; } set { useFriday = value; } }

        // 01. Trend Mode
        [NinjaScriptProperty]
        [Display(Name = "Use trend mode", Order = 1, GroupName = "01. Trend Mode")]
        public bool UseTrendMode
        { get { return useTrendMode; } set { useTrendMode = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Trend RR", Order = 2, GroupName = "01. Trend Mode")]
        public double RR
        { get { return rr; } set { rr = Math.Max(1.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Trend buffer stop (pts)", Order = 3, GroupName = "01. Trend Mode")]
        public double BufferPts
        { get { return bufferPts; } set { bufferPts = Math.Max(0.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Trend pullback tolerance (pts)", Order = 4, GroupName = "01. Trend Mode")]
        public double PullbackTol
        { get { return pullbackTol; } set { pullbackTol = Math.Max(0.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Trend body mult", Order = 5, GroupName = "01. Trend Mode")]
        public double BodyMult
        { get { return bodyMult; } set { bodyMult = Math.Max(0.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Trend range mult", Order = 6, GroupName = "01. Trend Mode")]
        public double RangeMult
        { get { return rangeMult; } set { rangeMult = Math.Max(0.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Trend max hold bars", Order = 7, GroupName = "01. Trend Mode")]
        public int MaxHoldBars
        { get { return maxHoldBars; } set { maxHoldBars = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Trend breakout lookback", Order = 8, GroupName = "01. Trend Mode")]
        public int MaxBreakLookback
        { get { return maxBreakLookback; } set { maxBreakLookback = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Max trades/day", Order = 9, GroupName = "01. Trend Mode")]
        public int MaxTradesPerDay
        { get { return maxTradesPerDay; } set { maxTradesPerDay = Math.Max(1, value); } }

        // 02. Range Core
        [NinjaScriptProperty]
        [Display(Name = "Use range mode", Order = 1, GroupName = "02. Range Core")]
        public bool UseRangeMode
        { get { return useRangeMode; } set { useRangeMode = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Use range longs", Order = 2, GroupName = "02. Range Core")]
        public bool UseRangeLongs
        { get { return useRangeLongs; } set { useRangeLongs = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Range RR", Order = 4, GroupName = "02. Range Core")]
        public double RangeRR
        { get { return rangeRR; } set { rangeRR = Math.Max(1.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range buffer stop (pts)", Order = 5, GroupName = "02. Range Core")]
        public double RangeBufferPts
        { get { return rangeBufferPts; } set { rangeBufferPts = Math.Max(0.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range lookback", Order = 6, GroupName = "02. Range Core")]
        public int RangeLookback
        { get { return rangeLookback; } set { rangeLookback = Math.Max(10, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Touch tolerance (pts)", Order = 7, GroupName = "02. Range Core")]
        public double TouchTolerancePts
        { get { return touchTolerancePts; } set { touchTolerancePts = Math.Max(0.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Wick min ratio", Order = 8, GroupName = "02. Range Core")]
        public double WickMinRatio
        { get { return wickMinRatio; } set { wickMinRatio = Math.Max(0.0, Math.Min(1.0, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Range extreme zone", Order = 9, GroupName = "02. Range Core")]
        public double RangeExtremeZone
        { get { return rangeExtremeZone; } set { rangeExtremeZone = Math.Max(0.05, Math.Min(0.50, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Intraday compression", Order = 10, GroupName = "02. Range Core")]
        public double IntradayCompression
        { get { return intradayCompression; } set { intradayCompression = Math.Max(0.10, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range max hold bars", Order = 11, GroupName = "02. Range Core")]
        public int RangeMaxHoldBars
        { get { return rangeMaxHoldBars; } set { rangeMaxHoldBars = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range min ATR mult", Order = 12, GroupName = "02. Range Core")]
        public double RangeMinAtrMult
        { get { return rangeMinAtrMult; } set { rangeMinAtrMult = Math.Max(0.1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range ADX max", Order = 13, GroupName = "02. Range Core")]
        public double RangeAdxMax
        { get { return rangeAdxMax; } set { rangeAdxMax = Math.Max(1.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range cooldown bars", Order = 14, GroupName = "02. Range Core")]
        public int RangeCooldownBars
        { get { return rangeCooldownBars; } set { rangeCooldownBars = Math.Max(0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range mid dead zone", Order = 15, GroupName = "02. Range Core")]
        public double RangeMidDeadZone
        { get { return rangeMidDeadZone; } set { rangeMidDeadZone = Math.Max(0.0, Math.Min(0.45, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Range window start (HHmmss)", Order = 16, GroupName = "02. Range Core",
                 Description = "Hora mínima para buscar setups Range. Default 103000 = 10:30 AM. Evita volatilidad de apertura.")]
        public int RangeWindowStart
        { get { return rangeWindowStart; } set { rangeWindowStart = Math.Max(93000, Math.Min(150000, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Range window end (HHmmss)", Order = 17, GroupName = "02. Range Core",
                 Description = "Hora máxima para buscar setups Range. Default 140000 = 2:00 PM. Evita choppiness del cierre.")]
        public int RangeWindowEnd
        { get { return rangeWindowEnd; } set { rangeWindowEnd = Math.Max(rangeWindowStart + 10000, Math.Min(150000, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Trend day exclusion", Order = 18, GroupName = "02. Range Core",
                 Description = "Bloquea Range Mode si hoy es un día de tendencia (prevDayRange > 1.2x avg Y breakout reciente).")]
        public bool UseTrendDayExclusion
        { get { return useTrendDayExclusion; } set { useTrendDayExclusion = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Range strength min", Order = 16, GroupName = "02. Range Quality")]
        public double RangeStrengthMin
        { get { return rangeStrengthMin; } set { rangeStrengthMin = Math.Max(0.10, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range min touches", Order = 17, GroupName = "02. Range Quality")]
        public int RangeMinTouches
        { get { return rangeMinTouches; } set { rangeMinTouches = Math.Max(1, value); } }

        // 03. Sweep Recovery
        [NinjaScriptProperty]
        [Display(Name = "Use sweep recovery", Order = 18, GroupName = "03. Sweep Recovery")]
        public bool UseSweepRecovery
        { get { return useSweepRecovery; } set { useSweepRecovery = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Sweep recovery buffer", Order = 19, GroupName = "03. Sweep Recovery")]
        public double SweepRecoveryBuffer
        { get { return sweepRecoveryBuffer; } set { sweepRecoveryBuffer = Math.Max(0.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Sweep min range ATR", Order = 20, GroupName = "03. Sweep Recovery")]
        public double SweepMinRangeAtr
        { get { return sweepMinRangeAtr; } set { sweepMinRangeAtr = Math.Max(0.05, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Use HTF EMA filter", Order = 21, GroupName = "03. Sweep Recovery")]
        public bool UseHtfEmaFilter
        { get { return useHtfEmaFilter; } set { useHtfEmaFilter = value; } }

        [NinjaScriptProperty]
        [Display(Name = "HTF EMA period", Order = 22, GroupName = "03. Sweep Recovery")]
        public int HtfEmaPeriod
        { get { return htfEmaPeriod; } set { htfEmaPeriod = Math.Max(10, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Displacement ATR mult", Order = 23, GroupName = "03. Sweep Recovery")]
        public double DisplacementAtrMult
        { get { return displacementAtrMult; } set { displacementAtrMult = Math.Max(0.10, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Pre-compression bars", Order = 24, GroupName = "03. Sweep Recovery")]
        public int PreCompressionBars
        { get { return preCompressionBars; } set { preCompressionBars = Math.Max(2, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Pre-compression max", Order = 25, GroupName = "03. Sweep Recovery")]
        public double PreCompressionMax
        { get { return preCompressionMax; } set { preCompressionMax = Math.Max(0.10, value); } }

        // 04. Risk
        [NinjaScriptProperty]
        [Display(Name = "Initial capital", Order = 1, GroupName = "04. Risk")]
        public double InitialCapital
        { get { return initialCapital; } set { initialCapital = Math.Max(1000, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Risk per trade", Order = 2, GroupName = "04. Risk")]
        public double RiskPerTrade
        { get { return riskPerTrade; } set { riskPerTrade = Math.Max(0.0001, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Point value", Order = 3, GroupName = "04. Risk")]
        public double PointValue
        { get { return pointValue; } set { pointValue = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Max risk points", Order = 4, GroupName = "04. Risk")]
        public double MaxRiskPoints
        { get { return maxRiskPoints; } set { maxRiskPoints = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Daily Loss Limit USD", Order = 5, GroupName = "04. Risk",
                 Description = "Pérdida máxima permitida por día. El bot para y no vuelve a operar hasta el día siguiente.")]
        public double DailyLossUSD { get; set; } = 500;

        // 05. Regime Filters
        [NinjaScriptProperty]
        [Display(Name = "HTF 60m filter ON", Order = 1, GroupName = "05. Regime Filters",
                 Description = "Solo opera Range si precio > EMA-20 en el chart de 60 minutos.")]
        public bool UseHtf60mFilter
        { get { return useHtf60mFilter; } set { useHtf60mFilter = value; } }

        [NinjaScriptProperty]
        [Display(Name = "HTF 60m EMA period", Order = 2, GroupName = "05. Regime Filters")]
        public int Htf60mEmaPeriod
        { get { return htf60mEmaPeriod; } set { htf60mEmaPeriod = Math.Max(5, value); } }

        [NinjaScriptProperty]
        [Display(Name = "ATR history days", Order = 3, GroupName = "05. Regime Filters",
                 Description = "Días de historial para el percentil de ATR (mínimo 20).")]
        public int AtrHistoryDays
        { get { return atrHistoryDays; } set { atrHistoryDays = Math.Max(20, value); } }

        [NinjaScriptProperty]
        [Display(Name = "ATR percentile max", Order = 4, GroupName = "05. Regime Filters",
                 Description = "Bloquea si el ATR del día supera este percentil histórico (0–100). 75 = bloquea los 25% días más volátiles.")]
        public double AtrPercentileMax
        { get { return atrPercentileMax; } set { atrPercentileMax = Math.Max(10, Math.Min(100, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Opening Range bias ON", Order = 5, GroupName = "05. Regime Filters",
                 Description = "Solo opera Range Longs si el cierre está sobre el midpoint del Opening Range (9:30–OR end).")]
        public bool UseOrBiasFilter
        { get { return useOrBiasFilter; } set { useOrBiasFilter = value; } }

        [NinjaScriptProperty]
        [Display(Name = "OR end time (HHmmss)", Order = 6, GroupName = "05. Regime Filters",
                 Description = "Hora de cierre del Opening Range. Default 100000 = 10:00 AM.")]
        public int OrEndTimeInt
        { get { return orEndTimeInt; } set { orEndTimeInt = Math.Max(93000, Math.Min(120000, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Max consecutive losses", Order = 7, GroupName = "05. Regime Filters",
                 Description = "Circuit Breaker: pausa trading tras N pérdidas consecutivas.")]
        public int MaxConsecutiveLosses
        { get { return maxConsecutiveLosses; } set { maxConsecutiveLosses = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Circuit breaker days", Order = 8, GroupName = "05. Regime Filters",
                 Description = "Días sin operar cuando el circuit breaker se activa.")]
        public int CircuitBreakerDays
        { get { return circuitBreakerDays; } set { circuitBreakerDays = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "BB Width filter ON", Order = 9, GroupName = "05. Regime Filters",
                 Description = "Bloquea Range si el ancho de BB (como múltiplo del ATR) está fuera de [min, max].")]
        public bool UseBbwFilter
        { get { return useBbwFilter; } set { useBbwFilter = value; } }

        [NinjaScriptProperty]
        [Display(Name = "BB Width min (x ATR)", Order = 10, GroupName = "05. Regime Filters",
                 Description = "Mínimo ancho de BB en múltiplos de ATR. Default 1.5 — filtra mercados demasiado comprimidos.")]
        public double BbwMin
        { get { return bbwMin; } set { bbwMin = Math.Max(0.0, value); } }

        [NinjaScriptProperty]
        [Display(Name = "BB Width max (x ATR)", Order = 11, GroupName = "05. Regime Filters",
                 Description = "Máximo ancho de BB en múltiplos de ATR. Default 6.0 — filtra expansión caótica.")]
        public double BbwMax
        { get { return bbwMax; } set { bbwMax = Math.Max(bbwMin + 0.1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Range quality min score", Order = 12, GroupName = "05. Regime Filters",
                 Description = "Score mínimo (0–100) para entrar en Range Mode. Score = f(rangeSize, ADX, 60m trend, ATR regime).")]
        public int RangeQualityMin
        { get { return rangeQualityMin; } set { rangeQualityMin = Math.Max(0, Math.Min(100, value)); } }

        [NinjaScriptProperty]
        [Display(Name = "Synthetic VIX ON", Order = 13, GroupName = "05. Regime Filters",
                 Description = "Bloquea Range si el ratio ATR(5)/ATR(20) supera el máximo (spike de volatilidad intraday).")]
        public bool UseSyntheticVix
        { get { return useSyntheticVix; } set { useSyntheticVix = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Synthetic VIX max", Order = 14, GroupName = "05. Regime Filters",
                 Description = "Ratio ATR(5)/ATR(20) máximo. Default 2.0 — bloquea cuando la vol reciente duplica la media.")]
        public double SyntheticVixMax
        { get { return syntheticVixMax; } set { syntheticVixMax = Math.Max(1.0, value); } }

        // ─── DAILY P&L ────────────────────────────────────────────────────────

        private double ObtenerPnLDia(double precio)
        {
            double realizado = 0.0;
            try
            {
                realizado = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit
                            - cumProfitInicioDia;
            }
            catch { }

            double flotante = (Position.MarketPosition != MarketPosition.Flat)
                ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, precio)
                : 0.0;

            return realizado + flotante;
        }
    }
}
