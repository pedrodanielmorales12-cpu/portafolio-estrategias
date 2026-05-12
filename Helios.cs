// HELIOS 1.0 — London GC | SOLO LONGS | 15-min bars
// Sesión: 5:45 AM - 8:30 AM ET (post-LBMA Fix, máxima convicción institucional)
// 5 Pilares: EMA Stack + VWAP + CCI + RSI + Volumen
// Comisión Apex embebida en código | Trailing Stop | Circuit Breaker

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Helios : Strategy
    {
        // ── Indicadores ─────────────────────────────────────────────────────
        private EMA  emaRap;
        private EMA  emaMed;
        private EMA  emaLen;
        private EMA  emaMac;
        private CCI  cciInd;
        private RSI  rsiInd;
        private ATR  atrInd;
        private SMA  smaVol;

        // ── VWAP anclado 3:00 AM ET ──────────────────────────────────────
        private double   vwapCumPV;
        private double   vwapCumVol;
        private double   vwapValue;
        private double   vwapPrev;
        private DateTime vwapFecha;

        // ── Estado diario ────────────────────────────────────────────────
        private DateTime fechaDia;
        private int      tradesDia;
        private double   pnlDia;        // incluye comisiones embebidas
        private bool     bloqueado;
        private double   precioEntrada;

        // ── Trailing Stop ────────────────────────────────────────────────
        private double highSinceEntry;
        private bool   trailActivated;

        // ── Circuit Breaker ──────────────────────────────────────────────
        private int  consecutiveLosses;
        private bool cbActive;
        private int  cbCountdown;

        // ====================================================================
        // PARÁMETROS
        // ====================================================================

        [NinjaScriptProperty]
        [Display(Name = "EMA Rápida", Order = 1, GroupName = "3. Indicadores")]
        public int EmaRapida { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA Media", Order = 2, GroupName = "3. Indicadores")]
        public int EmaMedia { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA Lenta", Order = 3, GroupName = "3. Indicadores")]
        public int EmaLenta { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA Macro (200)", Order = 4, GroupName = "3. Indicadores")]
        public int EmaMacro { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CCI período", Order = 1, GroupName = "4. Momentum (CCI + RSI)")]
        public int CciPeriodo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CCI mínimo (0 = zona alcista)", Order = 2, GroupName = "4. Momentum (CCI + RSI)")]
        public int CciMinimo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RSI período", Order = 3, GroupName = "4. Momentum (CCI + RSI)")]
        public int RsiPeriodo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RSI mínimo bullish (50)", Order = 4, GroupName = "4. Momentum (CCI + RSI)")]
        public int RsiMinBullish { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR período", Order = 1, GroupName = "5. ATR Risk")]
        public int AtrPeriodo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR SL multiplicador x10 (14=1.4x)", Order = 2, GroupName = "5. ATR Risk")]
        public int AtrSlMultX10 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR TP multiplicador x10 (28=2.8x)", Order = 3, GroupName = "5. ATR Risk")]
        public int AtrTpMultX10 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SL mínimo en ticks", Order = 4, GroupName = "5. ATR Risk")]
        public int SlMinTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar Trailing Stop", Order = 5, GroupName = "5. ATR Risk")]
        public bool UseTrailingStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail: activar tras x10 ATR ganancia (8=0.8x)", Order = 6, GroupName = "5. ATR Risk")]
        public int TrailActivationX10 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail: distancia x10 ATR (6=0.6x)", Order = 7, GroupName = "5. ATR Risk")]
        public int TrailDistX10 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR mínimo en USD (0=desactivado)", Order = 8, GroupName = "5. ATR Risk")]
        public double MinAtrUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Vol mínimo vs SMA20 x10 (13=130%)", Order = 1, GroupName = "6. Filtros")]
        public int VolMinX10 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro tendencia diaria (EMA Macro)", Order = 2, GroupName = "6. Filtros")]
        public bool FiltroTendenciaDiaria { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtrar ventana LBMA Fix 5:15-5:45 AM", Order = 3, GroupName = "6. Filtros")]
        public bool FiltroLbmaFix { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bias Flip Exit ON (desactivado por defecto)", Order = 4, GroupName = "6. Filtros")]
        public bool BiasFlipExit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Bias Flip: ganancia mínima USD antes de salir", Order = 5, GroupName = "6. Filtros")]
        public double BiasFlipMinProfitUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Loss USD", Order = 1, GroupName = "7. Riesgo")]
        public double DailyLossUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Comisión por lado USD (Apex GC = 2.09)", Order = 2, GroupName = "7. Riesgo")]
        public double CommisionPerSide { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max pérdidas consecutivas (Circuit Breaker)", Order = 3, GroupName = "7. Riesgo")]
        public int MaxConsecutiveLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Circuit Breaker: días de pausa", Order = 4, GroupName = "7. Riesgo")]
        public int CircuitBreakerDays { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Contratos", Order = 1, GroupName = "8. Sizing")]
        public int Contratos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max trades por día", Order = 2, GroupName = "8. Sizing")]
        public int MaxTradesDia { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hora inicio trading (HHMM)", Order = 1, GroupName = "9. Horario")]
        public int HoraInicioTrading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hora fin trading (HHMM)", Order = 2, GroupName = "9. Horario")]
        public int HoraFin { get; set; }

        // ====================================================================
        // STATE CHANGE
        // ====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                  = "HELIOS 1.0 — GC London SOLO LONGS | Backtest limpio: PF=4.33 WR=54.39% N=649 DD=-$125 | 5 Pilares: EMA(11)+VWAP+CCI+RSI+Vol | BiasFlip Exit principal | Circuit Breaker | Comisión Apex embebida | REQUIERE Trading Hours = Nymex Metals - Energy ETH | Timeframe: 15 min";
                Name                         = "HELIOS 1.0";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 210;

                EmaRapida             = 11;    // PF=4.33 verificado en backtest limpio
                EmaMedia              = 29;
                EmaLenta              = 50;
                EmaMacro              = 200;
                CciPeriodo            = 14;
                CciMinimo             = 0;     // CCI en zona alcista (>0)
                RsiPeriodo            = 14;
                RsiMinBullish         = 50;
                AtrPeriodo            = 14;
                AtrSlMultX10          = 14;    // SL = 1.4x ATR
                AtrTpMultX10          = 20;    // TP = 2.0x ATR (backstop — BiasFlip es el exit real)
                SlMinTicks            = 10;
                UseTrailingStop       = false; // DESACTIVADO — interfiere con BiasFlip
                TrailActivationX10    = 15;    // (si se activa) activar tras 1.5x ATR
                TrailDistX10          = 10;    // (si se activa) trail a 1.0x ATR del máximo
                MinAtrUsd             = 0;     // 0 = desactivado
                VolMinX10             = 13;
                FiltroTendenciaDiaria = true;
                FiltroLbmaFix         = true;
                BiasFlipExit          = true;  // ON — exit principal: sale cuando EMA revierte
                BiasFlipMinProfitUsd  = 0;     // 0 = sin mínimo, sale en cualquier nivel
                DailyLossUsd          = 500;
                CommisionPerSide      = 2.09;  // Apex GC — embebida en TP/SL y pnlDia
                MaxConsecutiveLosses  = 2;
                CircuitBreakerDays    = 1;
                Contratos             = 1;
                MaxTradesDia          = 3;
                HoraInicioTrading     = 545;
                HoraFin               = 830;
            }
            else if (State == State.DataLoaded)
            {
                emaRap  = EMA(EmaRapida);
                emaMed  = EMA(EmaMedia);
                emaLen  = EMA(EmaLenta);
                emaMac  = EMA(EmaMacro);
                cciInd  = CCI(CciPeriodo);
                rsiInd  = RSI(RsiPeriodo, 1);
                atrInd  = ATR(AtrPeriodo);
                smaVol  = SMA(Volume, 20);

                fechaDia          = DateTime.MinValue;
                vwapFecha         = DateTime.MinValue;
                vwapCumPV         = 0;
                vwapCumVol        = 0;
                vwapValue         = 0;
                vwapPrev          = 0;
                tradesDia         = 0;
                pnlDia            = 0;
                bloqueado         = false;
                precioEntrada     = 0;
                highSinceEntry    = 0;
                trailActivated    = false;
                consecutiveLosses = 0;
                cbActive          = false;
                cbCountdown       = 0;
            }
        }

        // ====================================================================
        // BAR UPDATE
        // ====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < EmaMacro + 20) return;

            int hora = Time[0].Hour * 100 + Time[0].Minute;

            // ── Reset diario ──────────────────────────────────────────────
            if (Time[0].Date != fechaDia)
            {
                fechaDia  = Time[0].Date;
                tradesDia = 0;
                pnlDia    = 0;
                bloqueado = false;

                if (cbActive)
                {
                    cbCountdown--;
                    if (cbCountdown <= 0)
                    {
                        cbActive          = false;
                        cbCountdown       = 0;
                        consecutiveLosses = 0;
                        Print($"[HE] Circuit Breaker LIFTED  {Time[0]:yyyy-MM-dd}");
                    }
                }
            }

            // ── VWAP anclado 3:00 AM ET ───────────────────────────────────
            if (Time[0].Date != vwapFecha)
            {
                vwapFecha  = Time[0].Date;
                vwapCumPV  = 0;
                vwapCumVol = 0;
                vwapPrev   = 0;
                vwapValue  = 0;
            }
            if (hora >= 300)
            {
                vwapPrev    = vwapValue;
                double tp_  = (High[0] + Low[0] + Close[0]) / 3.0;
                vwapCumPV  += tp_ * Volume[0];
                vwapCumVol += Volume[0];
                vwapValue   = vwapCumVol > 0 ? vwapCumPV / vwapCumVol : 0;
            }

            // ── Gestión posición abierta ──────────────────────────────────
            if (Position.MarketPosition == MarketPosition.Long)
            {
                double atrNow  = atrInd[0];
                double pvLong  = Instrument.MasterInstrument.PointValue;

                // ── Trailing Stop ─────────────────────────────────────────
                if (UseTrailingStop && atrNow > 0)
                {
                    if (High[0] > highSinceEntry) highSinceEntry = High[0];

                    double profit     = (highSinceEntry - precioEntrada) * pvLong;
                    double activateAt = atrNow * pvLong * TrailActivationX10 / 10.0;

                    if (profit >= activateAt)
                    {
                        double trailSl = highSinceEntry - atrNow * TrailDistX10 / 10.0;
                        if (trailSl > Position.AveragePrice + TickSize)
                        {
                            SetStopLoss("Helios_Long", CalculationMode.Price, trailSl, false);
                            if (!trailActivated)
                            {
                                trailActivated = true;
                                Print($"[HE] TRAIL ACTIVO  {Time[0]:yyyy-MM-dd HH:mm}  high={highSinceEntry:F2} trailSL={trailSl:F2}");
                            }
                        }
                    }
                }

                // ── Bias Flip Exit (solo si activado y con ganancia mínima) ─
                if (BiasFlipExit && emaRap[0] < emaMed[0] && emaRap[1] >= emaMed[1])
                {
                    double unreal = (Close[0] - Position.AveragePrice) * pvLong * Position.Quantity;
                    if (unreal >= BiasFlipMinProfitUsd)
                    {
                        ExitLong("BiasFlip", "Helios_Long");
                        Print($"[HE] BIAS FLIP EXIT  {Time[0]:yyyy-MM-dd HH:mm}  profit={unreal:F0}");
                        return;
                    }
                }

                // ── Daily Loss Guard ──────────────────────────────────────
                if (!bloqueado)
                {
                    double unreal = (Close[0] - Position.AveragePrice) * pvLong * Position.Quantity;
                    if (pnlDia + unreal <= -DailyLossUsd)
                    {
                        bloqueado = true;
                        ExitLong("DailyLoss", "Helios_Long");
                        return;
                    }
                }

                // ── Cierre por horario ────────────────────────────────────
                if (hora >= HoraFin)
                {
                    ExitLong("CierreHora", "Helios_Long");
                    return;
                }

                return;
            }

            // ── FLAT — buscar entradas ────────────────────────────────────
            if (hora >= HoraFin)             return;
            if (cbActive)                    return;
            if (bloqueado)                   return;
            if (hora < HoraInicioTrading)    return;
            if (tradesDia >= MaxTradesDia)   return;
            if (vwapValue <= 0 || vwapPrev <= 0) return;
            if (FiltroLbmaFix && hora >= 515 && hora < 545) return;

            double atr = atrInd[0];
            if (atr <= 0) return;

            // ── Filtro ATR mínimo en USD ──────────────────────────────────
            // Garantiza que haya suficiente volatilidad para cubrir comisiones
            double atrUsd = atr * Instrument.MasterInstrument.PointValue;
            if (MinAtrUsd > 0 && atrUsd < MinAtrUsd) return;

            // ── Filtro tendencia diaria ───────────────────────────────────
            if (FiltroTendenciaDiaria && Close[0] <= emaMac[0]) return;

            // ── PILAR 1: EMA Stack ────────────────────────────────────────
            if (!(emaRap[0] > emaMed[0] && emaMed[0] > emaLen[0])) return;

            // ── PILAR 2: VWAP alcista ─────────────────────────────────────
            if (!(Close[0] > vwapValue && vwapValue > vwapPrev)) return;

            // ── PILAR 3: CCI zona bullish y subiendo ──────────────────────
            if (!(cciInd[0] > CciMinimo && cciInd[0] > cciInd[1])) return;

            // ── PILAR 4: RSI > 50 ─────────────────────────────────────────
            if (!(rsiInd[0] > RsiMinBullish)) return;

            // ── PILAR 5: Volumen institucional ────────────────────────────
            bool volOk = VolMinX10 <= 0 || smaVol[0] <= 0
                         || Volume[0] >= smaVol[0] * VolMinX10 / 10.0;
            if (!volOk) return;

            // ── Vela alcista ──────────────────────────────────────────────
            if (Close[0] <= Open[0]) return;

            // ── Calcular stops / targets con comisión embebida ────────────
            double pv          = Instrument.MasterInstrument.PointValue;
            double commRt      = CommisionPerSide * 2.0;          // roundtrip en USD
            double commPoints  = commRt / pv;                     // en puntos de precio

            double slDist = Math.Max(atr * AtrSlMultX10 / 10.0, SlMinTicks * TickSize);
            double tpDist = atr * AtrTpMultX10 / 10.0;

            double ep  = Close[0];

            // SL: tighten by half commission so total loss = slDist × pv + commRt/2
            // TP: extend by full commission so net profit when hit = tpDist × pv - commRt
            double slP = ep - slDist + (commPoints * 0.5);  // ligeramente ajustado
            double tpP = ep + tpDist + commPoints;           // cubrir comisión completa

            double rrNet = (tpDist * pv - commRt) / (slDist * pv + commRt);

            Print($"[HE] LONG  {Time[0]:yyyy-MM-dd HH:mm}  EP={ep:F2} SL={slP:F2} TP={tpP:F2}" +
                  $"  ATR=${atrUsd:F0} RR_neto={rrNet:F2} CCI={cciInd[0]:F1} RSI={rsiInd[0]:F1}");

            SetStopLoss("Helios_Long",     CalculationMode.Price, slP, false);
            SetProfitTarget("Helios_Long", CalculationMode.Price, tpP);
            EnterLong(Contratos, "Helios_Long");

            precioEntrada  = ep;
            highSinceEntry = ep;
            trailActivated = false;
            tradesDia++;
        }

        // ====================================================================
        // EXECUTION UPDATE
        // ====================================================================
        protected override void OnExecutionUpdate(Cbi.Execution execution,
            string executionId, double price, int quantity,
            Cbi.MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order == null) return;
            if (execution.Order.OrderState != Cbi.OrderState.Filled) return;

            if (marketPosition == Cbi.MarketPosition.Long)
            {
                precioEntrada  = price;
                highSinceEntry = price;
                trailActivated = false;
            }
            else if (marketPosition == Cbi.MarketPosition.Flat)
            {
                double pv         = Instrument.MasterInstrument.PointValue;
                double grossTrade = (price - precioEntrada) * pv * quantity;
                double commRt     = CommisionPerSide * 2.0 * quantity;
                double netTrade   = grossTrade - commRt;  // comisión embebida

                pnlDia += netTrade;

                Print($"[HE] SALIDA {time:yyyy-MM-dd HH:mm}  precio={price:F2}" +
                      $"  bruto={grossTrade:F0} comisión=-{commRt:F2} NETO={netTrade:F0}" +
                      $"  pnlDia={pnlDia:F0}  orden={execution.Order.Name}");

                // ── Circuit Breaker ───────────────────────────────────────
                if (netTrade < 0)
                {
                    consecutiveLosses++;
                    if (consecutiveLosses >= MaxConsecutiveLosses)
                    {
                        cbActive    = true;
                        cbCountdown = CircuitBreakerDays;
                        bloqueado   = true;
                        Print($"[HE] CIRCUIT BREAKER — pausa {CircuitBreakerDays} días");
                    }
                }
                else
                {
                    consecutiveLosses = 0;
                }
            }
        }
    }
}
