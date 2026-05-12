// PanzerGold.cs v6 — ORB | Londres GC | Solo LONGS
// Rango: 3:00-3:30 AM ET | Trading: 3:30-9:00 AM ET
// Filtro tendencia diaria calculado internamente (sin serie secundaria)
// ESTRATEGIA SEPARADA de JupiterV7. No tocar JupiterV7.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class PanzerGold : Strategy
    {
        private EMA  emaRap;
        private EMA  emaLen;
        private EMA  emaMac;
        private RSI  rsiInd;
        private SMA  smaVol;

        // Estado diario del rango
        private double   rangoHigh;
        private double   rangoLow;
        private bool     rangoDefinido;
        private DateTime fechaDia;
        private int      tradesDia;
        private double   pnlDia;
        private bool     bloqueado;
        private double   precioEntrada;

        // Filtro tendencia diaria — calculado sin serie secundaria
        private Queue<double> cierresDiarios;  // ultimos 51 cierres diarios
        private double        ultimoCierreDia;  // cierre del dia anterior
        private bool          tendenciaDiariaOk;

        // =====================================================================
        // PARAMETROS
        // =====================================================================
        [NinjaScriptProperty]
        [Display(Name = "EMA Rapida", Order = 1, GroupName = "3. Indicadores")]
        public int EmaRapida { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA Lenta", Order = 2, GroupName = "3. Indicadores")]
        public int EmaLenta { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA Macro (200)", Order = 3, GroupName = "3. Indicadores")]
        public int EmaMacro { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RSI maximo para long", Order = 1, GroupName = "4. RSI")]
        public int RsiMaxLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Vol minimo vs SMA20 x10 (10=100%)", Order = 1, GroupName = "5. Filtros")]
        public int VolMinX10 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Minutos del rango (30=3:00-3:30 AM)", Order = 2, GroupName = "5. Filtros")]
        public int RangoMinutos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro tendencia diaria SMA50 (recomendado: Si)", Order = 3, GroupName = "5. Filtros")]
        public bool FiltroTendenciaDiaria { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "TP multiplo del rango x10 (20=2.0x)", Order = 1, GroupName = "7. Riesgo")]
        public int TpMultX10 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SL % del rango (100=rango completo)", Order = 2, GroupName = "7. Riesgo")]
        public int SlPctRango { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SL minimo en ticks", Order = 3, GroupName = "7. Riesgo")]
        public int SlMinTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Loss USD", Order = 4, GroupName = "7. Riesgo")]
        public double DailyLossUsd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Contratos", Order = 1, GroupName = "8. Sizing")]
        public int Contratos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max trades por dia", Order = 2, GroupName = "8. Sizing")]
        public int MaxTradesDia { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hora inicio rango (HHMM)", Order = 1, GroupName = "9. Horario")]
        public int HoraRango { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hora inicio trading (HHMM) — >= fin rango", Order = 2, GroupName = "9. Horario")]
        public int HoraInicioTrading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hora fin trading (HHMM)", Order = 3, GroupName = "9. Horario")]
        public int HoraFin { get; set; }

        // =====================================================================
        // STATE CHANGE
        // =====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                  = "PanzerGold v6 ORB — GC solo LONGS | Optimizer: PF=5.17 WR=87% N=931 DD=-$18K R2=0.98 | REQUIERE Trading Hours = Nymex Metals - En... (NO usar instrument default)";
                Name                         = "PanzerGold";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.UniqueEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 220;

                EmaRapida             = 12;   // Optimizer: PF=5.17 WR=87.4% N=931 DD=-$18K R2=0.98
                EmaLenta              = 21;
                EmaMacro              = 200;
                RsiMaxLong            = 65;
                VolMinX10             = 10;   // 100% del SMA20 de volumen
                RangoMinutos          = 20;   // Rango 3:00-3:20 AM
                FiltroTendenciaDiaria = true;
                TpMultX10             = 10;   // TP = 1.0x el rango
                SlPctRango            = 200;  // SL = 200% del rango
                SlMinTicks            = 10;
                DailyLossUsd          = 500;
                Contratos             = 1;
                MaxTradesDia          = 5;
                HoraRango             = 300;  // 3:00 AM ET — inicio del rango
                HoraInicioTrading     = 400;  // 4:00 AM ET — inicio del trading (espera confirmacion)
                HoraFin               = 1000; // 10:00 AM ET
            }
            else if (State == State.DataLoaded)
            {
                emaRap = EMA(EmaRapida);
                emaLen = EMA(EmaLenta);
                emaMac = EMA(EmaMacro);
                rsiInd = RSI(14, 1);
                smaVol = SMA(Volume, 20);

                fechaDia          = DateTime.MinValue;
                rangoHigh         = 0;
                rangoLow          = double.MaxValue;
                rangoDefinido     = false;
                tradesDia         = 0;
                pnlDia            = 0;
                bloqueado         = false;
                precioEntrada     = 0;
                cierresDiarios    = new Queue<double>();
                ultimoCierreDia   = 0;
                tendenciaDiariaOk = false;
            }
        }

        // =====================================================================
        // BAR UPDATE
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < EmaMacro + 20) return;

            int hora = Time[0].Hour * 100 + Time[0].Minute;

            // ── Reset diario + actualizar SMA50 diaria ────────────────────────
            if (Time[0].Date != fechaDia)
            {
                // Guardar cierre del dia anterior para el filtro diario
                if (fechaDia != DateTime.MinValue && ultimoCierreDia > 0)
                {
                    cierresDiarios.Enqueue(ultimoCierreDia);
                    if (cierresDiarios.Count > 51) cierresDiarios.Dequeue();

                    // Calcular SMA50 de cierres diarios
                    if (cierresDiarios.Count >= 50)
                    {
                        double suma = 0;
                        int    cnt  = 0;
                        foreach (double c in cierresDiarios)
                        {
                            if (cnt < 50) { suma += c; cnt++; }
                        }
                        // Tomar los ultimos 50 de la cola
                        double[] arr = cierresDiarios.ToArray();
                        int      len = arr.Length;
                        double   s50 = 0;
                        for (int k = len - 50; k < len; k++) s50 += arr[k];
                        s50 /= 50.0;
                        tendenciaDiariaOk = (ultimoCierreDia > s50);
                    }
                    else
                    {
                        tendenciaDiariaOk = true;  // sin datos suficientes, permitir
                    }
                }

                fechaDia      = Time[0].Date;
                tradesDia     = 0;
                pnlDia        = 0;
                bloqueado     = false;
                rangoHigh     = 0;
                rangoLow      = double.MaxValue;
                rangoDefinido = false;
                precioEntrada = 0;
            }

            // Guardar ultimo cierre del dia (se actualiza cada barra)
            ultimoCierreDia = Close[0];

            // ── Construir rango de apertura ───────────────────────────────────
            int horaFinRangoReal = (HoraRango / 100) * 100
                                 + (HoraRango % 100) + RangoMinutos;
            int hh = horaFinRangoReal / 100;
            int mm = horaFinRangoReal % 100;
            if (mm >= 60) { hh += mm / 60; mm = mm % 60; }
            horaFinRangoReal = hh * 100 + mm;

            if (hora >= HoraRango && hora < horaFinRangoReal)
            {
                if (High[0] > rangoHigh) rangoHigh = High[0];
                if (Low[0]  < rangoLow)  rangoLow  = Low[0];
                return;
            }

            if (!rangoDefinido && hora >= horaFinRangoReal
                && rangoHigh > 0 && rangoLow < double.MaxValue
                && rangoHigh > rangoLow)
            {
                rangoDefinido = true;
                Print($"[PG] {Time[0]:yyyy-MM-dd}  Rango: H={rangoHigh:F1} L={rangoLow:F1} Amp={(rangoHigh-rangoLow)/TickSize:F0}t  TendOk={tendenciaDiariaOk}");
            }

            // ── Cerrar posicion fuera de horario ──────────────────────────────
            if (hora >= HoraFin && Position.MarketPosition == MarketPosition.Long)
            {
                ExitLong("CierreHora", "PanzerORB");
                return;
            }

            // ── Daily Loss Guard ─────────────────────────────────────────────
            if (!bloqueado && Position.MarketPosition == MarketPosition.Long)
            {
                double unreal = (Close[0] - Position.AveragePrice)
                                * Instrument.MasterInstrument.PointValue;
                if (pnlDia + unreal <= -DailyLossUsd)
                {
                    bloqueado = true;
                    ExitLong("DailyLoss", "PanzerORB");
                    return;
                }
            }
            if (bloqueado) return;

            // ── Guardias de entrada ───────────────────────────────────────────
            if (hora < HoraInicioTrading || hora >= HoraFin) return;
            if (!rangoDefinido) return;
            if (Position.MarketPosition != MarketPosition.Flat) return;
            if (tradesDia >= MaxTradesDia) return;

            // ── Filtro tendencia diaria (SMA50 calculada internamente) ─────────
            if (FiltroTendenciaDiaria && !tendenciaDiariaOk) return;

            // ── Filtros de tendencia intradiaria ──────────────────────────────
            bool tendAlcista = emaRap[0] > emaLen[0]
                            && emaRap[0] > emaMac[0]
                            && Close[0]  > emaMac[0];
            if (!tendAlcista) return;

            if (rsiInd[0] >= RsiMaxLong) return;

            if (VolMinX10 > 0 && smaVol[0] > 0)
                if (Volume[0] < smaVol[0] * VolMinX10 / 10.0) return;

            // ── ENTRADA ORB ───────────────────────────────────────────────────
            if (Close[0] <= rangoHigh) return;

            double amplitud = rangoHigh - rangoLow;
            double tpDist   = amplitud * TpMultX10 / 10.0;
            double slDist   = amplitud * SlPctRango / 100.0;

            double slDistMin = SlMinTicks * TickSize;
            if (slDist < slDistMin) slDist = slDistMin;

            double ep  = rangoHigh + TickSize;
            double tpP = ep + tpDist;
            double slP = ep - slDist;

            Print($"[PG] ENTRADA {Time[0]:yyyy-MM-dd HH:mm}  EP={ep:F1} TP={tpP:F1} SL={slP:F1} Amp={(amplitud/TickSize):F0}t RSI={rsiInd[0]:F1}");

            SetStopLoss("PanzerORB",     CalculationMode.Price, slP, false);
            SetProfitTarget("PanzerORB", CalculationMode.Price, tpP);

            EnterLong(Contratos, "PanzerORB");
            precioEntrada = ep;
            tradesDia++;
        }

        // =====================================================================
        // EXECUTION UPDATE
        // =====================================================================
        protected override void OnExecutionUpdate(Cbi.Execution execution,
            string executionId, double price, int quantity,
            Cbi.MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order == null) return;
            if (execution.Order.OrderState != Cbi.OrderState.Filled) return;

            if (marketPosition == Cbi.MarketPosition.Long)
            {
                precioEntrada = price;
            }
            else if (marketPosition == Cbi.MarketPosition.Flat)
            {
                double pnlTrade = (price - precioEntrada)
                                  * Instrument.MasterInstrument.PointValue * quantity;
                pnlDia += pnlTrade;
                Print($"[PG] SALIDA {time:yyyy-MM-dd HH:mm}  precio={price:F1} PnL={pnlTrade:F0} orden={execution.Order.Name}");
            }
        }
    }
}
