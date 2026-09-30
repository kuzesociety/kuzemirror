// =====================================================================================
//  RutaCryptoMirror - NinjaTrader 8 strategy
//
//  Bar-for-bar port of the TradingView Pine v4 strategy
//  "rutacrypto Optimized with Martingale and Leverage" (martingale / leverage removed).
//
//  Mirrored exactly (see README.md for the details and the reasons):
//   * Heikin Ashi open/close as returned by security(heikinashi(tickerid), timeframe.period, ...)
//     -> used only as a filter, orders fill at REAL prices, never at Heikin Ashi prices.
//   * Pine's own ema / rma / rsi / atr / sma math. NinjaTrader's built-in RSI / ATR / EMA
//     seed and smooth differently, so they are NOT used.
//   * RSI source = TradingView "Volume" indicator MA (SMA 10), RSI length 14 (your chart
//     settings, not the code defaults of close / 12).
//   * process_orders_on_close=true: every TradingView order fills at the CLOSE of the bar
//     that placed it.
//   * Stop loss / take profit are checked on the CLOSE only. There are no intrabar stop or
//     limit orders in the Pine script, so there are none here either.
//   * The exact order of the Pine script: SL/TP are overwritten by a new entry BEFORE the
//     exit block runs, and on a reversal TradingView flips the position with one order and
//     drops the same-bar close_all (your chart: "-47.2 Short" then "Take Profit +27.9").
//
//  Keep "Calculate" = On bar close and "Exit on session close" = false (both are the defaults).
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Strategies
{
	public class RutaCryptoMirror : Strategy
	{
		private const string TagPrefix = "RCM_";

		private TvEngine engine;
		private int lastProcessedBar = -1;
		private SimpleFont orderFont;
		private SimpleFont signalFont;
		private SimpleFont statsFont;
		private StringBuilder barsCsv;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "NinjaTrader 8 mirror of the TradingView strategy 'rutacrypto Optimized' (fills at bar close, close-based SL/TP, Heikin Ashi + RSI of Volume MA). Martingale removed.";
				Name = "RutaCryptoMirror";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = false;	// TradingView never flattens at the session end
				ExitOnSessionCloseSeconds = 30;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 0;
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 200;			// warm-up: lets Pine's recursive averages converge
				IsInstantiatedOnEachOptimizationIteration = true;

				// Pine inputs, set to the values in your TradingView screenshots
				RsiUpper = 71;
				RsiLower = 20;
				VolumeThreshold = -39;
				StopLossAtrMultiplier = 2;
				TakeProfitAtrMultiplier = 4;
				OscShortLength = 5;
				OscLongLength = 14;
				RsiSource = RutaMirrorRsiSource.VolumeMA;
				RsiLength = 14;
				VolumeMaLength = 10;
				VolumeMaType = RutaMirrorMaType.SMA;

				// Hard-coded in the Pine script
				AtrLength = 14;
				TrendSmaLength = 10;
				RoundHeikinAshiToTick = false;

				// Position size (replaces martingale)
				SizingMode = RutaMirrorSizing.FixedContracts;
				FixedContracts = 1;
				RiskPerTrade = 1000;
				MaxContracts = 10;

				// Execution / chart
				EnableOrders = true;
				ShowSignals = true;
				ShowTvFills = true;
				ShowTradeLines = true;
				ShowStats = true;
				PrintTradeList = false;
				ExportTradesCsv = false;
				ExportBarsCsv = false;

				AddPlot(new Stroke(Brushes.Red, 2), PlotStyle.Line, "Stop Loss");
				AddPlot(new Stroke(Brushes.LimeGreen, 2), PlotStyle.Line, "Take Profit");
			}
			else if (State == State.DataLoaded)
			{
				TvEngine.Settings s = new TvEngine.Settings();
				s.RsiUpper = RsiUpper;
				s.RsiLower = RsiLower;
				s.VolumeThreshold = VolumeThreshold;
				s.SlAtrMult = StopLossAtrMultiplier;
				s.TpAtrMult = TakeProfitAtrMultiplier;
				s.OscShortLength = OscShortLength;
				s.OscLongLength = OscLongLength;
				s.RsiSource = RsiSource;
				s.RsiLength = RsiLength;
				s.VolumeMaLength = VolumeMaLength;
				s.VolumeMaType = VolumeMaType;
				s.AtrLength = AtrLength;
				s.TrendSmaLength = TrendSmaLength;
				s.RoundHaToTick = RoundHeikinAshiToTick;
				s.TickSize = TickSize;
				s.PointValue = Instrument.MasterInstrument.PointValue;
				s.Sizing = SizingMode;
				s.FixedContracts = FixedContracts;
				s.RiskPerTrade = RiskPerTrade;
				s.MaxContracts = MaxContracts;
				engine = new TvEngine(s);

				lastProcessedBar = -1;
				orderFont = new SimpleFont("Arial", 11);
				orderFont.Bold = true;
				signalFont = new SimpleFont("Arial", 10);
				statsFont = new SimpleFont("Arial", 12);
				barsCsv = null;
				Print(Name + " started on " + Instrument.FullName + " " + BarsPeriod.Value + " " + BarsPeriod.BarsPeriodType + " - status box at the bottom-left of the chart when loading finishes.");
				if (ExportBarsCsv)
				{
					barsCsv = new StringBuilder();
					barsCsv.AppendLine("nt_bar_time,tv_bar_time,open,high,low,close,volume,ha_open,ha_close,rsi_source,rsi,vol_osc,atr,sma_trend,longcond,shortcond,position_sign");
				}
			}
			else if (State == State.Transition)
			{
				if (engine != null)
				{
					Print(Name + " " + Instrument.FullName + " historical TV-mirror result: " + engine.SummaryLine());
					Print(Name + " " + engine.DiagnosticText(BarsRequiredToTrade).Replace("\n", " | "));
				}
				WriteCsvFiles();
			}
			else if (State == State.Terminated)
			{
				WriteCsvFiles();
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || engine == null)
				return;

			// Signals are evaluated on CLOSED bars only (TradingView default calc_on_every_tick=false).
			// With Calculate = OnEachTick/OnPriceChange the just-closed bar is processed on the first
			// tick of the next bar, which is the same moment an OnBarClose strategy runs.
			int ago = 0;
			if (Calculate != Calculate.OnBarClose && (State == State.Realtime || Bars.IsTickReplay))
			{
				if (!IsFirstTickOfBar)
					return;
				ago = 1;
			}
			int bar = CurrentBar - ago;
			if (bar < 0 || bar <= lastProcessedBar)
				return;
			lastProcessedBar = bar;

			TvEngine.BarResult r = engine.Update(bar, Time[ago], Open[ago], High[ago], Low[ago], Close[ago], Volume[ago], bar >= BarsRequiredToTrade);

			SetPlotValue(Values[0], ago, r.PlotStopLoss);
			SetPlotValue(Values[1], ago, r.PlotTakeProfit);

			if (EnableOrders)
				SubmitMirrorOrders(r);

			DrawBar(r, bar, ago);

			if (barsCsv != null)
				AppendBarCsv(r, ago);
		}

		#region Orders
		// Real NinjaTrader orders. They are market orders sent at the bar close, so NinjaTrader fills
		// them at the next tick (historical: the next bar's open). TradingView's simulated fill is the
		// close itself; that price is what the diamonds/labels and the stats box show.
		private void SubmitMirrorOrders(TvEngine.BarResult r)
		{
			if (r.EntryDirection > 0)
				EnterLong(r.QtyAfter, "Long");			// reverses a short automatically, like strategy.entry
			else if (r.EntryDirection < 0)
				EnterShort(r.QtyAfter, "Short");
			else if (r.ExitComment != null)
			{
				if (r.PositionBefore > 0)
					ExitLong(r.ExitComment, "Long");
				else if (r.PositionBefore < 0)
					ExitShort(r.ExitComment, "Short");
			}
		}
		#endregion

		#region Chart drawing
		private static void SetPlotValue(Series<double> series, int barsAgo, double value)
		{
			if (double.IsNaN(value))
				series.Reset(barsAgo);
			else
				series[barsAgo] = value;
		}

		private void DrawBar(TvEngine.BarResult r, int bar, int ago)
		{
			if (ShowSignals)
			{
				// plotshape(longcond and strategy.position_size <= 0 ...) / plotshape(shortcond and strategy.position_size >= 0 ...)
				if (r.BuySignal)
					Draw.Text(this, TagPrefix + "sig" + bar, false, "Buy", ago, Low[ago], -14, Brushes.Black, signalFont, TextAlignment.Center, Brushes.Transparent, Brushes.LimeGreen, 90);
				if (r.SellSignal)
					Draw.Text(this, TagPrefix + "sig" + bar, false, "Sell", ago, High[ago], 14, Brushes.Black, signalFont, TextAlignment.Center, Brushes.Transparent, Brushes.Red, 90);
			}

			if (ShowTvFills)
			{
				if (r.EntryDirection != 0)
				{
					bool isBuy = r.EntryDirection > 0;
					DrawOrder(bar, ago, isBuy, isBuy ? "Long" : "Short", r.EntryOrderQty, isBuy ? Brushes.DodgerBlue : Brushes.Red, r.FillPrice);
				}
				else if (r.ExitComment != null)
				{
					bool isBuy = r.PositionBefore < 0;	// closing a short is a buy
					DrawOrder(bar, ago, isBuy, r.ExitComment, r.QtyBefore, Brushes.Magenta, r.FillPrice);
				}
			}

			if (ShowTradeLines && r.ClosedTrade != null)
			{
				TvEngine.TvTrade t = r.ClosedTrade;
				Draw.Line(this, TagPrefix + "trade" + t.Number, false, t.EntryTime, t.EntryPrice, t.ExitTime, t.ExitPrice,
					t.Points > 0 ? Brushes.LimeGreen : Brushes.OrangeRed, DashStyleHelper.Dot, 2);
			}

			if (r.ClosedTrade != null && PrintTradeList)
				Print(FormatTradeLine(r.ClosedTrade));

			// Status box: on every order, on the last historical bars and on every live bar, so it is
			// visible even when there are no trades (it then says which filter blocked them).
			bool lastHistoricalBar = State == State.Historical && CurrentBar >= Bars.Count - 2;
			if (ShowStats && (State != State.Historical || lastHistoricalBar || r.EntryDirection != 0 || r.ExitComment != null))
				Draw.TextFixed(this, TagPrefix + "stats", engine.StatsText(BarsRequiredToTrade), TextPosition.BottomLeft, Brushes.White, statsFont, Brushes.Gray, Brushes.Black, 70);
		}

		// TradingView style order marker: sells above the bar ("-qty" over the name), buys below
		// ("name" over "+qty"), and a diamond at the exact TradingView fill price (the bar close).
		private void DrawOrder(int bar, int ago, bool isBuy, string name, int qty, Brush brush, double fillPrice)
		{
			Draw.Diamond(this, TagPrefix + "px" + bar, false, ago, fillPrice, brush);
			if (isBuy)
				Draw.Text(this, TagPrefix + "ord" + bar, false, name + "\n+" + qty, ago, Low[ago], -46, brush, orderFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			else
				Draw.Text(this, TagPrefix + "ord" + bar, false, "-" + qty + "\n" + name, ago, High[ago], 46, brush, orderFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
		}
		#endregion

		#region Output window / CSV
		// TradingView "List of trades" shows the bar OPEN time; NinjaTrader stamps bars with their CLOSE time.
		private DateTime ToTvBarTime(DateTime ntBarTime)
		{
			if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute)
				return ntBarTime.AddMinutes(-BarsPeriod.Value);
			if (BarsPeriod.BarsPeriodType == BarsPeriodType.Second)
				return ntBarTime.AddSeconds(-BarsPeriod.Value);
			return ntBarTime;
		}

		private string FormatTradeLine(TvEngine.TvTrade t)
		{
			return string.Format(CultureInfo.InvariantCulture,
				"#{0} {1} {2} @ {3} (TV bar {4:yyyy-MM-dd HH:mm}) -> {5} @ {6} (TV bar {7:yyyy-MM-dd HH:mm})  {8:+0.00;-0.00} pts x{9}",
				t.Number, t.Direction > 0 ? "Long" : "Short", t.EntrySignal, t.EntryPrice, ToTvBarTime(t.EntryTime),
				t.ExitSignal, t.ExitPrice, ToTvBarTime(t.ExitTime), t.Points, t.Qty);
		}

		private void AppendBarCsv(TvEngine.BarResult r, int ago)
		{
			barsCsv.AppendLine(string.Format(CultureInfo.InvariantCulture,
				"{0:yyyy-MM-dd HH:mm:ss},{1:yyyy-MM-dd HH:mm:ss},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16}",
				Time[ago], ToTvBarTime(Time[ago]), Open[ago], High[ago], Low[ago], Close[ago], Volume[ago],
				Num(r.HaOpen), Num(r.HaClose), Num(r.RsiSourceValue), Num(r.Rsi), Num(r.Osc), Num(r.Atr), Num(r.TrendMa),
				r.LongCond ? 1 : 0, r.ShortCond ? 1 : 0, r.PositionBefore));
		}

		private static string Num(double v)
		{
			return double.IsNaN(v) ? "" : v.ToString("0.########", CultureInfo.InvariantCulture);
		}

		private string BuildTradesCsv()
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("Trade #,Type,Signal,Date/Time (TV bar open),NT bar time (close),Price,Contracts,Profit pts,Profit $,Cum. Profit $");
			double cum = 0;
			foreach (TvEngine.TvTrade t in engine.Trades)
			{
				string side = t.Direction > 0 ? "Long" : "Short";
				double money = t.Points * t.Qty * engine.PointValue;
				cum += money;
				sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},Entry {1},{2},{3:yyyy-MM-dd HH:mm},{4:yyyy-MM-dd HH:mm},{5},{6},,,",
					t.Number, side, t.EntrySignal, ToTvBarTime(t.EntryTime), t.EntryTime, t.EntryPrice, t.Qty));
				sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},Exit {1},{2},{3:yyyy-MM-dd HH:mm},{4:yyyy-MM-dd HH:mm},{5},{6},{7:0.00},{8:0.00},{9:0.00}",
					t.Number, side, t.ExitSignal, ToTvBarTime(t.ExitTime), t.ExitTime, t.ExitPrice, t.Qty, t.Points, money, cum));
			}
			return sb.ToString();
		}

		private void WriteCsvFiles()
		{
			if (engine == null || (!ExportTradesCsv && barsCsv == null))
				return;
			try
			{
				string dir = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RutaCryptoMirror");
				System.IO.Directory.CreateDirectory(dir);
				string baseName = SafeFileName(Instrument.FullName + "_" + BarsPeriod.Value + BarsPeriod.BarsPeriodType);
				if (ExportTradesCsv)
					System.IO.File.WriteAllText(System.IO.Path.Combine(dir, baseName + "_trades.csv"), BuildTradesCsv());
				if (barsCsv != null)
					System.IO.File.WriteAllText(System.IO.Path.Combine(dir, baseName + "_bars.csv"), barsCsv.ToString());
				Print(Name + ": CSV written to " + dir);
			}
			catch (Exception ex)
			{
				Print(Name + ": CSV export failed: " + ex.Message);
			}
		}

		private static string SafeFileName(string name)
		{
			foreach (char c in System.IO.Path.GetInvalidFileNameChars())
				name = name.Replace(c, '_');
			return name.Replace(' ', '_');
		}
		#endregion

		#region TradingView engine (plain C#, no NinjaTrader calls)
		/// <summary>
		/// Bar-by-bar replica of the Pine script plus TradingView's broker emulator with
		/// process_orders_on_close=true. Call Update() once per CLOSED bar.
		/// </summary>
		public sealed class TvEngine
		{
			public sealed class Settings
			{
				public double RsiUpper = 71, RsiLower = 20, VolumeThreshold = -39, SlAtrMult = 2, TpAtrMult = 4;
				public int OscShortLength = 5, OscLongLength = 14;
				public RutaMirrorRsiSource RsiSource = RutaMirrorRsiSource.VolumeMA;
				public int RsiLength = 14, VolumeMaLength = 10;
				public RutaMirrorMaType VolumeMaType = RutaMirrorMaType.SMA;
				public int AtrLength = 14, TrendSmaLength = 10;
				public bool RoundHaToTick;
				public double TickSize = 0.25, PointValue = 1;
				public RutaMirrorSizing Sizing = RutaMirrorSizing.FixedContracts;
				public int FixedContracts = 1, MaxContracts = 10;
				public double RiskPerTrade = 1000;
			}

			public sealed class TvTrade
			{
				public int Number, Direction, Qty, EntryBar, ExitBar;
				public DateTime EntryTime, ExitTime;
				public double EntryPrice, ExitPrice;
				public double Points;			// per contract, positive = profit
				public string EntrySignal, ExitSignal;
			}

			public sealed class BarResult
			{
				public double HaOpen, HaClose, RsiSourceValue, Rsi, Osc, Atr, TrendMa;
				public bool LongCond, ShortCond;		// raw Pine conditions
				public bool BuySignal, SellSignal;		// the two plotshape() conditions
				public double PlotStopLoss = double.NaN, PlotTakeProfit = double.NaN;	// plot(plotSL) / plot(plotTP)
				public int PositionBefore, PositionAfter;	// sign of strategy.position_size before / after this bar's fills
				public int QtyBefore, QtyAfter;
				public int EntryDirection;			// +1 / -1 when strategy.entry filled on this bar
				public int EntryOrderQty;			// TradingView order size (includes the reversed position)
				public string ExitComment;			// "Take Profit" / "Stop Loss" when strategy.close_all filled
				public double FillPrice = double.NaN;	// TradingView fill price (= close) when an order filled
				public TvTrade ClosedTrade;			// trade closed on this bar (exit or reversal)
			}

			private readonly Settings s;
			private readonly PineMa emaShort, emaLong, volMa, trendMa;
			private readonly PineRsi rsi;
			private readonly PineAtr atr;

			private double haOpenPrev = double.NaN, haClosePrev = double.NaN, rsiPrev = double.NaN;

			// Pine 'var' variables: they keep their value until the next entry overwrites them
			private double stopLossLevel = double.NaN, profitTarget = double.NaN;

			// Broker emulator position
			private int position, positionQty, positionBar;
			private double positionPrice;
			private DateTime positionTime;

			private readonly List<TvTrade> trades = new List<TvTrade>();
			private int wins;
			private double grossProfitPts, grossLossPts, netMoney;

			// Diagnostics for the status box, so an empty chart explains itself
			private int barsSeen, rsiCrossUps, rsiCrossDowns, buySignals, sellSignals;
			private double minVolume = double.MaxValue, maxVolume = double.MinValue, lastRsi = double.NaN;

			public TvEngine(Settings settings)
			{
				s = settings;
				emaShort = new PineEma(s.OscShortLength, 2.0 / (s.OscShortLength + 1));
				emaLong = new PineEma(s.OscLongLength, 2.0 / (s.OscLongLength + 1));
				volMa = CreateMa(s.VolumeMaType, s.VolumeMaLength);
				trendMa = new PineSma(s.TrendSmaLength);
				rsi = new PineRsi(s.RsiLength);
				atr = new PineAtr(s.AtrLength);
			}

			public List<TvTrade> Trades { get { return trades; } }
			public double PointValue { get { return s.PointValue; } }
			public int Position { get { return position; } }

			public BarResult Update(int barIndex, DateTime time, double open, double high, double low, double close, double volume, bool canTrade)
			{
				BarResult r = new BarResult();

				// hclose / hopen = security(heikinashi(syminfo.tickerid), timeframe.period, close / open)
				double haClose = (open + high + low + close) / 4.0;
				double haOpen = double.IsNaN(haOpenPrev) ? (open + close) / 2.0 : (haOpenPrev + haClosePrev) / 2.0;
				haOpenPrev = haOpen;
				haClosePrev = haClose;
				double hclose = s.RoundHaToTick ? RoundToTick(haClose) : haClose;
				double hopen = s.RoundHaToTick ? RoundToTick(haOpen) : haOpen;

				// osc = 100 * (ema(volume, shortlen) - ema(volume, longlen)) / ema(volume, longlen)
				double shortEma = emaShort.Update(volume);
				double longEma = emaLong.Update(volume);
				double osc = (double.IsNaN(shortEma) || double.IsNaN(longEma) || longEma == 0) ? double.NaN : 100.0 * (shortEma - longEma) / longEma;

				// rsi1 = rsi(rsisrc, rsilen);  atr = atr(14);  trendma = sma(close, 10)
				double volMaValue = volMa.Update(volume);
				double src = SelectSource(open, high, low, close, volume, volMaValue);
				double rsi1 = rsi.Update(src);
				double atrValue = atr.Update(high, low, close);
				double trend = trendMa.Update(close);

				// crossover(rsi1, rsilower) / crossunder(rsi1, rsiupper). Comparisons with NaN are false, like na in Pine.
				bool crossUp = rsi1 > s.RsiLower && rsiPrev <= s.RsiLower;
				bool crossDown = rsi1 < s.RsiUpper && rsiPrev >= s.RsiUpper;
				rsiPrev = rsi1;

				barsSeen++;
				lastRsi = rsi1;
				minVolume = Math.Min(minVolume, volume);
				maxVolume = Math.Max(maxVolume, volume);

				bool longCond = hclose > hopen && crossUp && osc > s.VolumeThreshold && close > trend;
				bool shortCond = hclose < hopen && crossDown && osc > s.VolumeThreshold && close < trend;

				r.HaOpen = hopen;
				r.HaClose = hclose;
				r.RsiSourceValue = src;
				r.Rsi = rsi1;
				r.Osc = osc;
				r.Atr = atrValue;
				r.TrendMa = trend;
				r.LongCond = longCond;
				r.ShortCond = shortCond;

				if (!canTrade)
				{
					longCond = false;
					shortCond = false;
				}

				// strategy.position_size as the script sees it (fills of earlier bars only)
				int pos = position;
				r.PositionBefore = pos;
				r.QtyBefore = positionQty;
				r.BuySignal = longCond && pos <= 0;
				r.SellSignal = shortCond && pos >= 0;

				if (canTrade)
				{
					if (crossUp) rsiCrossUps++;
					if (crossDown) rsiCrossDowns++;
					if (r.BuySignal) buySignals++;
					if (r.SellSignal) sellSignals++;
				}

				// Long Entry / Short Entry blocks: overwrite the levels first...
				int entryDir = 0;
				if (longCond && pos <= 0)
				{
					stopLossLevel = close - atrValue * s.SlAtrMult;
					profitTarget = close + atrValue * s.TpAtrMult;
					entryDir = 1;
				}
				if (shortCond && pos >= 0)
				{
					stopLossLevel = close + atrValue * s.SlAtrMult;
					profitTarget = close - atrValue * s.TpAtrMult;
					entryDir = -1;
				}

				// ...then the exit block runs with the OLD position size and the (possibly NEW) levels
				bool takeProfitHit = (pos > 0 && close >= profitTarget) || (pos < 0 && close <= profitTarget);
				bool stopLossHit = (pos > 0 && close <= stopLossLevel) || (pos < 0 && close >= stopLossLevel);

				// plotSL / plotTP
				if (pos != 0 || r.BuySignal || r.SellSignal)
				{
					r.PlotStopLoss = stopLossLevel;
					r.PlotTakeProfit = profitTarget;
				}

				// Broker emulator, process_orders_on_close=true: everything fills at this bar's close.
				if (entryDir != 0)
				{
					// strategy.entry reverses an opposite position in ONE order (old qty + new qty).
					// The script's close_all calls on this bar are always true on a reversal bar (the levels
					// were just overwritten) and TradingView drops them - the position ends up reversed.
					int newQty = ComputeQty(atrValue);
					r.EntryOrderQty = newQty + (pos != 0 ? positionQty : 0);
					if (pos != 0)
						r.ClosedTrade = CloseTrade(barIndex, time, close, entryDir > 0 ? "Long" : "Short");
					position = entryDir;
					positionQty = newQty;
					positionPrice = close;
					positionBar = barIndex;
					positionTime = time;
					r.EntryDirection = entryDir;
					r.FillPrice = close;
				}
				else if (pos != 0 && (takeProfitHit || stopLossHit))
				{
					// strategy.close_all(comment="Take Profit") then strategy.close_all(comment="Stop Loss"): last call wins
					r.ExitComment = stopLossHit ? "Stop Loss" : "Take Profit";
					r.ClosedTrade = CloseTrade(barIndex, time, close, r.ExitComment);
					position = 0;
					positionQty = 0;
					r.FillPrice = close;
				}

				r.PositionAfter = position;
				r.QtyAfter = positionQty;
				return r;
			}

			private TvTrade CloseTrade(int barIndex, DateTime time, double price, string exitSignal)
			{
				TvTrade t = new TvTrade();
				t.Number = trades.Count + 1;
				t.Direction = position;
				t.Qty = positionQty;
				t.EntryBar = positionBar;
				t.EntryTime = positionTime;
				t.EntryPrice = positionPrice;
				t.EntrySignal = position > 0 ? "Long" : "Short";
				t.ExitBar = barIndex;
				t.ExitTime = time;
				t.ExitPrice = price;
				t.ExitSignal = exitSignal;
				t.Points = (price - positionPrice) * position;
				trades.Add(t);

				if (t.Points > 0)
				{
					wins++;
					grossProfitPts += t.Points * t.Qty;
				}
				else
					grossLossPts -= t.Points * t.Qty;
				netMoney += t.Points * t.Qty * s.PointValue;
				return t;
			}

			// Replaces the martingale/leverage sizing. RiskPerTrade is the non-martingale "USD" mode of the
			// Pine script: its qty = initial_size / (sl_multiplier * atr), i.e. it loses initial_size at the stop.
			private int ComputeQty(double atrValue)
			{
				int maxQty = Math.Max(1, s.MaxContracts);
				if (s.Sizing == RutaMirrorSizing.FixedContracts)
					return Math.Max(1, s.FixedContracts);
				double riskPerContract = atrValue * s.SlAtrMult * s.PointValue;
				if (double.IsNaN(riskPerContract) || riskPerContract <= 0)
					return 1;
				int qty = (int)Math.Floor(s.RiskPerTrade / riskPerContract);
				return Math.Min(maxQty, Math.Max(1, qty));
			}

			private double SelectSource(double o, double h, double l, double c, double v, double volMaValue)
			{
				switch (s.RsiSource)
				{
					case RutaMirrorRsiSource.VolumeMA:	return volMaValue;
					case RutaMirrorRsiSource.Volume:	return v;
					case RutaMirrorRsiSource.Open:		return o;
					case RutaMirrorRsiSource.High:		return h;
					case RutaMirrorRsiSource.Low:		return l;
					case RutaMirrorRsiSource.HL2:		return (h + l) / 2.0;
					case RutaMirrorRsiSource.HLC3:		return (h + l + c) / 3.0;
					case RutaMirrorRsiSource.OHLC4:		return (o + h + l + c) / 4.0;
					default:							return c;
				}
			}

			// round_to_mintick(): nearest tick, ties up
			private double RoundToTick(double value)
			{
				if (s.TickSize <= 0)
					return value;
				return Math.Floor(value / s.TickSize + 0.5) * s.TickSize;
			}

			public string SummaryLine()
			{
				int n = trades.Count;
				return string.Format(CultureInfo.InvariantCulture, "{0} closed trades, win rate {1:0.0}%, net {2:+0.00;-0.00} pts x contracts, ${3:0.00}, profit factor {4}",
					n, n > 0 ? 100.0 * wins / n : 0.0, grossProfitPts - grossLossPts, netMoney,
					grossLossPts > 0 ? (grossProfitPts / grossLossPts).ToString("0.00", CultureInfo.InvariantCulture) : "n/a");
			}

			public string StatsText(int barsRequired)
			{
				int n = trades.Count;
				StringBuilder sb = new StringBuilder();
				sb.AppendLine("RutaCryptoMirror - TradingView mirror (fills at bar close)");
				sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Bars processed: {0}   (trades allowed from bar {1})", barsSeen, barsRequired));
				sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Closed trades: {0}   Win rate: {1:0.0}%", n, n > 0 ? 100.0 * wins / n : 0.0));
				sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Net: {0:+0.00;-0.00} pts x contracts   ${1:0.00}   Profit factor: {2}", grossProfitPts - grossLossPts, netMoney,
					grossLossPts > 0 ? (grossProfitPts / grossLossPts).ToString("0.00", CultureInfo.InvariantCulture) : "n/a"));
				if (position == 0)
					sb.AppendLine("Position: flat");
				else
					sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Position: {0} {1} @ {2}   SL {3:0.00}   TP {4:0.00}",
						position > 0 ? "Long" : "Short", positionQty, positionPrice, stopLossLevel, profitTarget));
				sb.Append(DiagnosticText(barsRequired));
				return sb.ToString();
			}

			// Which filter is stopping trades, in plain words
			public string DiagnosticText(int barsRequired)
			{
				string text = string.Format(CultureInfo.InvariantCulture,
					"Since bar {0}: RSI crossed up {1}: {2}x, crossed down {3}: {4}x -> Buy signals {5}, Sell signals {6}   (RSI now {7})",
					barsRequired, s.RsiLower, rsiCrossUps, s.RsiUpper, rsiCrossDowns, buySignals, sellSignals,
					double.IsNaN(lastRsi) ? "n/a" : lastRsi.ToString("0.0", CultureInfo.InvariantCulture));
				if (barsSeen <= barsRequired)
					return text + string.Format(CultureInfo.InvariantCulture, "\nNOT ENOUGH DATA: only {0} bars loaded, the first {1} are warm-up. Load more days on the chart.", barsSeen, barsRequired);
				if (maxVolume <= minVolume)
					return text + string.Format(CultureInfo.InvariantCulture, "\nNO VOLUME: every bar has volume {0}. The RSI of the volume MA and the volume oscillator can never trigger on this data.", minVolume);
				if (rsiCrossUps + rsiCrossDowns == 0)
					return text + "\nThe RSI never crossed its levels. Check RSI Source / RSI Length / Volume MA Length.";
				if (buySignals + sellSignals == 0)
					return text + "\nThe RSI crossed, but the Heikin Ashi / volume oscillator / SMA filters rejected every cross.";
				return text;
			}

			private static PineMa CreateMa(RutaMirrorMaType type, int length)
			{
				switch (type)
				{
					case RutaMirrorMaType.EMA:	return new PineEma(length, 2.0 / (length + 1));
					case RutaMirrorMaType.RMA:	return new PineEma(length, 1.0 / length);
					case RutaMirrorMaType.WMA:	return new PineWma(length);
					default:					return new PineSma(length);
				}
			}

			#region Pine built-ins
			public abstract class PineMa
			{
				public abstract double Update(double x);
			}

			// sma(): na until 'length' values exist
			public sealed class PineSma : PineMa
			{
				private readonly double[] window;
				private int count, next;

				public PineSma(int length) { window = new double[Math.Max(1, length)]; }

				public override double Update(double x)
				{
					window[next] = x;
					next = (next + 1) % window.Length;
					if (count < window.Length)
						count++;
					if (count < window.Length)
						return double.NaN;
					double sum = 0;
					for (int i = 0; i < window.Length; i++)
						sum += window[i];
					return sum / window.Length;
				}
			}

			// ema() (alpha = 2/(len+1)) and rma() (alpha = 1/len): seeded with the SMA of the first
			// 'length' non-na values, na before that; na inputs are skipped.
			public sealed class PineEma : PineMa
			{
				private readonly int length;
				private readonly double alpha;
				private double seedSum, value = double.NaN;
				private int seedCount;

				public PineEma(int length, double alpha)
				{
					this.length = Math.Max(1, length);
					this.alpha = alpha;
				}

				public double Value { get { return value; } }

				public override double Update(double x)
				{
					if (double.IsNaN(x))
						return value;
					if (double.IsNaN(value))
					{
						seedSum += x;
						seedCount++;
						if (seedCount >= length)
							value = seedSum / length;
						return value;
					}
					value = alpha * x + (1 - alpha) * value;
					return value;
				}
			}

			// wma(): weights length..1, newest bar heaviest
			public sealed class PineWma : PineMa
			{
				private readonly double[] window;
				private int count, next;

				public PineWma(int length) { window = new double[Math.Max(1, length)]; }

				public override double Update(double x)
				{
					window[next] = x;
					next = (next + 1) % window.Length;
					if (count < window.Length)
						count++;
					if (count < window.Length)
						return double.NaN;
					int n = window.Length;
					double sum = 0, norm = 0;
					for (int i = 0; i < n; i++)
					{
						double w = n - i;			// i bars ago
						sum += window[((next - 1 - i) % n + n) % n] * w;
						norm += w;
					}
					return sum / norm;
				}
			}

			// rsi(x, len): rma of up/down changes; same edge cases as TradingView's RSI
			public sealed class PineRsi
			{
				private readonly PineEma up, down;
				private double prev = double.NaN;

				public PineRsi(int length)
				{
					up = new PineEma(length, 1.0 / Math.Max(1, length));
					down = new PineEma(length, 1.0 / Math.Max(1, length));
				}

				public double Update(double x)
				{
					if (!double.IsNaN(x) && !double.IsNaN(prev))
					{
						up.Update(Math.Max(x - prev, 0));
						down.Update(Math.Max(prev - x, 0));
					}
					prev = x;
					double u = up.Value, d = down.Value;
					if (double.IsNaN(u) || double.IsNaN(d))
						return double.NaN;
					if (d == 0)
						return 100;
					if (u == 0)
						return 0;
					return 100.0 - 100.0 / (1.0 + u / d);
				}
			}

			// atr(len) = rma(tr(true), len); the first bar's true range is high - low
			public sealed class PineAtr
			{
				private readonly PineEma rma;
				private double prevClose = double.NaN;

				public PineAtr(int length) { rma = new PineEma(length, 1.0 / Math.Max(1, length)); }

				public double Update(double high, double low, double close)
				{
					double tr = double.IsNaN(prevClose)
						? high - low
						: Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
					prevClose = close;
					return rma.Update(tr);
				}
			}
			#endregion
		}
		#endregion

		#region Properties
		[NinjaScriptProperty]
		[Range(1.0, 1000.0)]
		[Display(Name = "RSI Upper Level", Description = "rsiupper - short needs RSI to cross DOWN through this level", Order = 1, GroupName = "1. Strategy Conditions (Pine)")]
		public double RsiUpper { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 1000.0)]
		[Display(Name = "RSI Lower Level", Description = "rsilower - long needs RSI to cross UP through this level", Order = 2, GroupName = "1. Strategy Conditions (Pine)")]
		public double RsiLower { get; set; }

		[NinjaScriptProperty]
		[Range(-1000.0, 1000.0)]
		[Display(Name = "Volume Increase Threshold", Description = "vollevel - volume oscillator must be above this", Order = 3, GroupName = "1. Strategy Conditions (Pine)")]
		public double VolumeThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Stop Loss ATR Multiplier", Order = 4, GroupName = "1. Strategy Conditions (Pine)")]
		public double StopLossAtrMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Take Profit ATR Multiplier", Order = 5, GroupName = "1. Strategy Conditions (Pine)")]
		public double TakeProfitAtrMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Short Length", Description = "Volume oscillator fast EMA", Order = 1, GroupName = "2. Volume Oscillator (Pine)")]
		public int OscShortLength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Long Length", Description = "Volume oscillator slow EMA", Order = 2, GroupName = "2. Volume Oscillator (Pine)")]
		public int OscLongLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "RSI Source", Description = "Your TradingView chart uses 'Vol.: Volume MA' = VolumeMA", Order = 1, GroupName = "3. RSI (Pine)")]
		public RutaMirrorRsiSource RsiSource { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "RSI Length", Description = "Your TradingView chart uses 14 (the code default is 12)", Order = 2, GroupName = "3. RSI (Pine)")]
		public int RsiLength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Volume MA Length", Description = "MA length of the TradingView Volume indicator you picked as RSI source", Order = 3, GroupName = "3. RSI (Pine)")]
		public int VolumeMaLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Volume MA Type", Description = "TradingView's built-in Volume indicator uses SMA", Order = 4, GroupName = "3. RSI (Pine)")]
		public RutaMirrorMaType VolumeMaType { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "ATR Length", Description = "Hard-coded atr(14) in Pine", Order = 1, GroupName = "4. Hard-coded in Pine")]
		public int AtrLength { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Trend SMA Length", Description = "Hard-coded sma(close, 10) in Pine", Order = 2, GroupName = "4. Hard-coded in Pine")]
		public int TrendSmaLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Round Heikin Ashi to tick", Description = "Only matters when HA open and close are within half a tick. Toggle if one rare trade differs.", Order = 3, GroupName = "4. Hard-coded in Pine")]
		public bool RoundHeikinAshiToTick { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sizing Mode", Description = "FixedContracts, or RiskPerTrade (= the Pine 'USD' sizing without martingale)", Order = 1, GroupName = "5. Position Size")]
		public RutaMirrorSizing SizingMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Fixed Contracts", Order = 2, GroupName = "5. Position Size")]
		public int FixedContracts { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, double.MaxValue)]
		[Display(Name = "Risk Per Trade ($)", Description = "RiskPerTrade mode: contracts = floor(risk / (SL ATR mult x ATR x point value))", Order = 3, GroupName = "5. Position Size")]
		public double RiskPerTrade { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max Contracts", Order = 4, GroupName = "5. Position Size")]
		public int MaxContracts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Orders", Description = "Off = only draw the TradingView trades (indicator mode)", Order = 1, GroupName = "6. Execution")]
		public bool EnableOrders { get; set; }

		[Display(Name = "Show Buy/Sell Signals", Order = 1, GroupName = "7. Chart / Compare")]
		public bool ShowSignals { get; set; }

		[Display(Name = "Show TradingView Fills", Description = "Order labels + diamond at the TradingView fill price (bar close)", Order = 2, GroupName = "7. Chart / Compare")]
		public bool ShowTvFills { get; set; }

		[Display(Name = "Show Trade Lines", Order = 3, GroupName = "7. Chart / Compare")]
		public bool ShowTradeLines { get; set; }

		[Display(Name = "Show Stats Box", Order = 4, GroupName = "7. Chart / Compare")]
		public bool ShowStats { get; set; }

		[Display(Name = "Print Trades To Output", Order = 5, GroupName = "7. Chart / Compare")]
		public bool PrintTradeList { get; set; }

		[Display(Name = "Export Trades CSV", Description = "Documents\\NinjaTrader 8\\RutaCryptoMirror\\<instrument>_trades.csv", Order = 6, GroupName = "7. Chart / Compare")]
		public bool ExportTradesCsv { get; set; }

		[Display(Name = "Export Bar Values CSV", Description = "Per-bar HA / RSI / osc / ATR values to compare with the debug Pine script", Order = 7, GroupName = "7. Chart / Compare")]
		public bool ExportBarsCsv { get; set; }
		#endregion
	}
}

public enum RutaMirrorRsiSource
{
	VolumeMA,
	Volume,
	Close,
	Open,
	High,
	Low,
	HL2,
	HLC3,
	OHLC4
}

public enum RutaMirrorMaType
{
	SMA,
	EMA,
	RMA,
	WMA
}

public enum RutaMirrorSizing
{
	FixedContracts,
	RiskPerTrade
}
