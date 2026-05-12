// ╔══════════════════════════════════════════════════════════════════╗
// ║                      DESCARTES 1.0                               ║
// ║        Gana rápido · Arriesga poco · Pierde poco                 ║
// ║                                                                  ║
// ║  COMPILAR: NinjaScript Editor → F5                               ║
// ║  ⚠ Trading Hours ES → "CME US Index Futures ETH"                ║
// ╚══════════════════════════════════════════════════════════════════╝

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Descartes_1_0 : Strategy
    {
        #region Indicadores
        private EMA  ema18;
        private EMA  ema40;
        private VWAP vwap;
        #endregion

        #region Variables de estado
        private int      operacionesHoy        = 0;
        private DateTime fechaUltReseteo       = DateTime.MinValue;
        private double   cumProfitInicioDia    = 0.0;
        private bool     tradingBloqueadoHoy   = false;
        private bool     inicializado          = false;

        private bool   longSetupActivo  = false;
        private bool   shortSetupActivo = false;
        private double longTriggerHigh  = 0;
        private double shortTriggerLow  = 0;
        private int    barsDesdeSetup   = 0;
        private int    barsEnTrade      = 0;
        #endregion

        // ══════════════════════════════════════════════════════════
        //  PARÁMETROS
        // ══════════════════════════════════════════════════════════

        #region Parámetros — Días operables
        [NinjaScriptProperty]
        [Display(Name = "Lunes",      GroupName = "1 · Días operables", Order = 1)]
        public bool OperarLunes     { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Martes",     GroupName = "1 · Días operables", Order = 2)]
        public bool OperarMartes    { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Miércoles",  GroupName = "1 · Días operables", Order = 3)]
        public bool OperarMiercoles { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Jueves",     GroupName = "1 · Días operables", Order = 4)]
        public bool OperarJueves    { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Viernes",    GroupName = "1 · Días operables", Order = 5)]
        public bool OperarViernes   { get; set; } = true;
        #endregion

        #region Parámetros — Sesión
        [NinjaScriptProperty]
        [Display(Name = "Hora inicio (HHMM)", GroupName = "2 · Sesión", Order = 1)]
        public int HoraInicio { get; set; } = 830;

        [NinjaScriptProperty]
        [Display(Name = "Hora fin (HHMM)", GroupName = "2 · Sesión", Order = 2)]
        public int HoraFin { get; set; } = 1700;

        [NinjaScriptProperty]
        [Display(Name = "Máx. trades por día", GroupName = "2 · Sesión", Order = 3)]
        public int MaxTradesPorDia { get; set; } = 10;
        #endregion

        #region Parámetros — Dirección
        [NinjaScriptProperty]
        [Display(Name = "Habilitar Compras", GroupName = "3 · Dirección", Order = 1)]
        public bool HabilitarCompras { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Habilitar Ventas",  GroupName = "3 · Dirección", Order = 2)]
        public bool HabilitarVentas  { get; set; } = false;
        #endregion

        #region Parámetros — Gestión de riesgo
        [NinjaScriptProperty]
        [Display(Name = "Take Profit (ticks)", GroupName = "4 · Gestión de riesgo", Order = 1,
                 Description = "TECHO de seguridad — exit primario es bias flip + trailing stop.\n" +
                               "100t raramente se toca. ES: 100t=25pts | GC: 100t=$1000")]
        public int TP_Ticks { get; set; } = 100;

        [NinjaScriptProperty]
        [Display(Name = "Stop Loss fijo (ticks, 0=calcular por USD)", GroupName = "4 · Gestión de riesgo", Order = 2,
                 Description = "SL directo en ticks. 0 = usar Pérdida máx. USD (recomendado).\n" +
                               "ES: 30t=7.5pts | 40t=10pts")]
        public int SL_Ticks { get; set; } = 0;

        [NinjaScriptProperty]
        [Display(Name = "Profit objetivo USD/trade", GroupName = "4 · Gestión de riesgo", Order = 3)]
        public double ProfitUSDPorTrade { get; set; } = 400;

        [NinjaScriptProperty]
        [Display(Name = "Pérdida máxima USD/trade (backup)", GroupName = "4 · Gestión de riesgo", Order = 4,
                 Description = "Solo se usa si SL_Ticks = 0. Normalmente ignorado en v1.3.")]
        public double MaxPerdidaUSDTrade { get; set; } = 300;

        [NinjaScriptProperty]
        [Display(Name = "Daily Loss USD", GroupName = "4 · Gestión de riesgo", Order = 4)]
        public double DailyLossUSD { get; set; } = 1700;

        [NinjaScriptProperty]
        [Display(Name = "Mínimo contratos", GroupName = "4 · Gestión de riesgo", Order = 5)]
        public int MinContratos { get; set; } = 1;

        [NinjaScriptProperty]
        [Display(Name = "Máximo contratos", GroupName = "4 · Gestión de riesgo", Order = 6)]
        public int MaxContratos { get; set; } = 10;
        #endregion

        #region Parámetros — Calidad de entrada
        [NinjaScriptProperty]
        [Display(Name = "Cuerpo mínimo pullback (ticks)", GroupName = "5 · Calidad entrada", Order = 1,
                 Description = "Filtra dojis. ES: 2t | GC: 3t | 0=desactivado")]
        public int CuerpoMinTicks { get; set; } = 2;

        [NinjaScriptProperty]
        [Display(Name = "Barras máx. para confirmar setup", GroupName = "5 · Calidad entrada", Order = 2,
                 Description = "Cancela el setup si no se confirma en X barras.")]
        public int MaxBarrasSetup { get; set; } = 5;
        #endregion

        #region Parámetros — Control de exit
        [NinjaScriptProperty]
        [Display(Name = "Mín. barras antes de exit por bias",
                 GroupName = "6 · Control de exit", Order = 1,
                 Description = "El bias flip NO puede cerrar antes de X barras.\n" +
                               "En ES 1min: 8 barras = 8 minutos de hold mínimo.\n" +
                               "También activa trailing stop.")]
        public int MinBarrasAntesExitBias { get; set; } = 8;

        [NinjaScriptProperty]
        [Display(Name = "Mín. ticks ganancia antes de exit por bias",
                 GroupName = "6 · Control de exit", Order = 2,
                 Description = "El bias flip solo cierra si ganancia flotante ≥ X ticks.\n" +
                               "ES: 6 ticks = 1.5 puntos. 0 = desactivado")]
        public int MinTicksAntesExitBias { get; set; } = 6;

        [NinjaScriptProperty]
        [Display(Name = "Trailing Stop (ticks, 0=desactivado)",
                 GroupName = "6 · Control de exit", Order = 3,
                 Description = "Trailing stop activo desde barra MinBarras.\n" +
                               "Protege ganancia acumulada. ES: 4t = 1 punto.")]
        public int TrailingStopTicks { get; set; } = 4;
        #endregion

        // ══════════════════════════════════════════════════════════
        //  INICIALIZACIÓN
        // ══════════════════════════════════════════════════════════

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "DESCARTES 1.0";
                Description = "VWAP + EMA18 + EMA40. Gana rápido, arriesga poco, pierde poco.";

                Calculate                    = Calculate.OnBarClose;
                IncludeCommission            = true;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
            }
            else if (State == State.DataLoaded)
            {
                ema18 = EMA(18);
                ema40 = EMA(40);
                vwap  = VWAP();

                cumProfitInicioDia = 0.0;
                inicializado       = false;
            }
        }
        #endregion

        // ══════════════════════════════════════════════════════════
        //  LÓGICA PRINCIPAL
        // ══════════════════════════════════════════════════════════

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            // ── Guardias ─────────────────────────────────────────────
            if (CurrentBar < 50)         return;
            if (!EsDiaOperable(Time[0])) return;

            int hora = ToTime(Time[0]);
            if (hora < HoraInicio * 100 || hora > HoraFin * 100) return;

            // ── Reset diario ──────────────────────────────────────────
            if (fechaUltReseteo.Date != Time[0].Date)
                ResetDiario(Time[0]);

            // ── Indicadores ───────────────────────────────────────────
            double vwapVal  = vwap[0];
            double ema18Val = ema18[0];
            double ema40Val = ema40[0];
            double closeVal = Close[0];
            double openVal  = Open[0];
            double valorTick = Instrument.MasterInstrument.PointValue * TickSize;

            // ── BIAS ──────────────────────────────────────────────────
            bool biasLong  = closeVal > vwapVal && ema18Val > ema40Val && ema18Val > vwapVal;
            bool biasShort = closeVal < vwapVal && ema18Val < ema40Val && ema18Val < vwapVal;

            // ── PnL del día ───────────────────────────────────────────
            double pnlDia = ObtenerPnLDia(closeVal);

            // ── DAILY LOSS LOCK ───────────────────────────────────────
            if (!tradingBloqueadoHoy && pnlDia <= -DailyLossUSD)
            {
                tradingBloqueadoHoy = true;
                ResetSetups();
                if      (Position.MarketPosition == MarketPosition.Long)  ExitLong ("ExitDailyLoss", "Compra");
                else if (Position.MarketPosition == MarketPosition.Short) ExitShort("ExitDailyLoss", "Venta");
                return;
            }
            if (tradingBloqueadoHoy) return;

            // ══════════════════════════════════════════════════════════
            //  GESTIÓN DE POSICIÓN ABIERTA
            // ══════════════════════════════════════════════════════════
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                barsEnTrade++;

                // ── Trailing Stop (activo desde barra MinBarras) ──────
                if (TrailingStopTicks > 0 && barsEnTrade >= MinBarrasAntesExitBias)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        SetTrailStop("Compra", CalculationMode.Ticks, TrailingStopTicks, false);
                    else if (Position.MarketPosition == MarketPosition.Short)
                        SetTrailStop("Venta",  CalculationMode.Ticks, TrailingStopTicks, false);
                }

                // ── Salida por cambio de bias (v1.1 controlada) ───────
                // Condición 1: mínimo de barras en trade superado
                bool superaBarrasMin = (MinBarrasAntesExitBias == 0)
                                    || (barsEnTrade >= MinBarrasAntesExitBias);

                // Condición 2: ganancia flotante mínima en ticks
                bool superaTicksMin = true;
                if (MinTicksAntesExitBias > 0 && valorTick > 0)
                {
                    double pnlFlotante = Position.GetUnrealizedProfitLoss(
                                            PerformanceUnit.Currency, closeVal);
                    double ticksGanados = pnlFlotante / (valorTick * Position.Quantity);
                    superaTicksMin = ticksGanados >= MinTicksAntesExitBias;
                }

                // Salida por bias flip (controlada por barras + ticks mínimos)
                if (superaBarrasMin && superaTicksMin)
                {
                    if (Position.MarketPosition == MarketPosition.Long  && !biasLong)
                        ExitLong ("ExitBiasFlip", "Compra");

                    if (Position.MarketPosition == MarketPosition.Short && !biasShort)
                        ExitShort("ExitBiasFlip", "Venta");
                }

                return; // posición abierta — no buscar nueva entrada
            }

            // ══════════════════════════════════════════════════════════
            //  BÚSQUEDA DE ENTRADA
            // ══════════════════════════════════════════════════════════
            barsEnTrade = 0;
            if (operacionesHoy >= MaxTradesPorDia) return;

            // ── Expiración de setup ───────────────────────────────────
            if (longSetupActivo || shortSetupActivo)
            {
                barsDesdeSetup++;
                if (barsDesdeSetup > MaxBarrasSetup) ResetSetups();
            }

            double cuerpoMin  = CuerpoMinTicks * TickSize;
            double cuerpoVela = Math.Abs(closeVal - openVal);

            // ── SETUP LONG ────────────────────────────────────────────
            if (biasLong && HabilitarCompras)
            {
                if (!longSetupActivo && closeVal < openVal && cuerpoVela >= cuerpoMin)
                {
                    longSetupActivo = true;
                    longTriggerHigh = High[0];
                    barsDesdeSetup  = 0;
                }
                else if (longSetupActivo && closeVal > longTriggerHigh)
                {
                    Entrar("Compra", true, valorTick);
                    longSetupActivo = false;
                    barsDesdeSetup  = 0;
                }
            }
            else { longSetupActivo = false; }

            // ── SETUP SHORT ───────────────────────────────────────────
            if (biasShort && HabilitarVentas)
            {
                if (!shortSetupActivo && closeVal > openVal && cuerpoVela >= cuerpoMin)
                {
                    shortSetupActivo = true;
                    shortTriggerLow  = Low[0];
                    barsDesdeSetup   = 0;
                }
                else if (shortSetupActivo && closeVal < shortTriggerLow)
                {
                    Entrar("Venta", false, valorTick);
                    shortSetupActivo = false;
                    barsDesdeSetup   = 0;
                }
            }
            else { shortSetupActivo = false; }
        }
        #endregion

        // ══════════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════════

        #region Helpers

        private void Entrar(string señal, bool esLong, double valorTick)
        {
            int qty     = CalcularContratos(valorTick);
            if (qty <= 0) return;

            // v1.3: SL directo en ticks (ignorando el cálculo por USD)
            int slTicks = (SL_Ticks > 0) ? SL_Ticks : CalcularStopTicks(qty, valorTick);

            if (esLong)
            {
                SetStopLoss("Compra",     CalculationMode.Ticks, slTicks, false);
                SetProfitTarget("Compra", CalculationMode.Ticks, TP_Ticks);
                EnterLong(qty, "Compra");
            }
            else
            {
                SetStopLoss("Venta",     CalculationMode.Ticks, slTicks, false);
                SetProfitTarget("Venta", CalculationMode.Ticks, TP_Ticks);
                EnterShort(qty, "Venta");
            }
            operacionesHoy++;
        }

        private int CalcularContratos(double valorTick)
        {
            double profitPorContrato = TP_Ticks * valorTick;
            if (profitPorContrato <= 0) return MinContratos;
            int qty = (int)Math.Floor(ProfitUSDPorTrade / profitPorContrato);
            return Math.Max(MinContratos, Math.Min(MaxContratos, qty));
        }

        private int CalcularStopTicks(int qty, double valorTick)
        {
            double ticks = MaxPerdidaUSDTrade / (Math.Max(1, qty) * valorTick);
            return Math.Max(1, (int)Math.Ceiling(ticks));
        }

        private double ObtenerPnLDia(double precio)
        {
            double realizado = 0.0;
            try
            {
                realizado = SystemPerformance.AllTrades
                                .TradesPerformance.Currency.CumProfit
                            - cumProfitInicioDia;
            }
            catch { }

            double flotante =
                (Position.MarketPosition != MarketPosition.Flat)
                ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, precio)
                : 0.0;

            return realizado + flotante;
        }

        private void ResetDiario(DateTime t)
        {
            operacionesHoy      = 0;
            fechaUltReseteo     = t;
            tradingBloqueadoHoy = false;
            barsEnTrade         = 0;
            ResetSetups();

            try
            {
                cumProfitInicioDia = SystemPerformance.AllTrades
                                         .TradesPerformance.Currency.CumProfit;
            }
            catch
            {
                if (!inicializado) cumProfitInicioDia = 0.0;
            }
            inicializado = true;
        }

        private void ResetSetups()
        {
            longSetupActivo  = false;
            shortSetupActivo = false;
            barsDesdeSetup   = 0;
        }

        private bool EsDiaOperable(DateTime t)
        {
            return (t.DayOfWeek == DayOfWeek.Monday    && OperarLunes)
                || (t.DayOfWeek == DayOfWeek.Tuesday   && OperarMartes)
                || (t.DayOfWeek == DayOfWeek.Wednesday && OperarMiercoles)
                || (t.DayOfWeek == DayOfWeek.Thursday  && OperarJueves)
                || (t.DayOfWeek == DayOfWeek.Friday    && OperarViernes);
        }

        #endregion
    }
}
