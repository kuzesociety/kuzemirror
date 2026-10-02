// Drives RutaCryptoMirror.cs through the stubbed NinjaTrader API.
// 1) writes synthetic OHLCV bars + the strategy's own per-bar values and trade list, for
//    tools/verify/pine_reference.py to recompute independently with Pine semantics;
// 2) checks the NinjaTrader order calls line up with the TradingView trades;
// 3) checks Calculate=OnEachTick in realtime gives the same trades as OnBarClose.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;

public class Harness : RutaCryptoMirror
{
	public void Step(State state) { State = state; OnStateChange(); }
	public void Bar() { OnBarUpdate(); }
	public TvEngine EngineForTest
	{
		get { return (TvEngine)typeof(RutaCryptoMirror).GetField("engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(this); }
	}
}

public static class Program
{
	private struct BarData { public DateTime T; public double O, H, L, C, V; }

	private static List<BarData> MakeBars(int n, int seed)
	{
		Random rnd = new Random(seed);
		List<BarData> bars = new List<BarData>();
		double price = 30000;
		double baseVol = 300;
		DateTime t = new DateTime(2026, 9, 1, 0, 5, 0);
		for (int i = 0; i < n; i++)
		{
			// volume regimes: quiet stretches and bursts so the RSI of the volume MA swings 0..100
			if (rnd.NextDouble() < 0.04)
				baseVol = 80 + rnd.NextDouble() * 900;
			double vol = Math.Max(1, Math.Round(baseVol * (0.6 + 0.8 * rnd.NextDouble())));
			double drift = (rnd.NextDouble() - 0.5) * 40;
			double o = price;
			double c = Math.Round((o + drift) * 4) / 4;
			double h = Math.Max(o, c) + Math.Round(rnd.NextDouble() * 12 * 4) / 4;
			double l = Math.Min(o, c) - Math.Round(rnd.NextDouble() * 12 * 4) / 4;
			BarData b = new BarData();
			b.T = t; b.O = o; b.H = h; b.L = l; b.C = c; b.V = vol;
			bars.Add(b);
			// next open: usually the close, sometimes a small gap
			price = rnd.NextDouble() < 0.9 ? c : c + Math.Round((rnd.NextDouble() - 0.5) * 8 * 4) / 4;
			t = t.AddMinutes(5);
		}
		return bars;
	}

	private static Harness NewHarness(bool exportCsv)
	{
		Harness h = new Harness();
		h.Step(State.SetDefaults);
		h.BarsPeriod.BarsPeriodType = NinjaTrader.Data.BarsPeriodType.Minute;
		h.BarsPeriod.Value = 5;
		h.ExportTradesCsv = exportCsv;
		h.ExportBarsCsv = exportCsv;
		h.PrintTradeList = true;
		return h;
	}

	// A new trading session starts at 18:00 (first 5-minute bar is stamped 18:05), like CME ETH
	private static bool IsSessionStart(BarData b) { return b.T.Hour == 18 && b.T.Minute == 5; }

	private static void Push(Harness h, BarData b)
	{
		if (IsSessionStart(b))
			h.Bars.SessionStarts.Add(h.TimeData.Count);
		h.TimeData.Add(b.T); h.OpenData.Add(b.O); h.HighData.Add(b.H); h.LowData.Add(b.L); h.CloseData.Add(b.C); h.VolumeData.Add(b.V);
	}

	// Runs a historical load and returns the last status-box text drawn
	private static string RunStatusOnly(List<BarData> data, bool enableOrders)
	{
		DrawingLog.Calls.Clear();
		Harness h = NewHarness(false);
		h.PrintTradeList = false;
		h.EnableOrders = enableOrders;
		h.ShowTvFills = false;
		h.ShowSignals = false;
		h.ShowTradeLines = false;
		h.Step(State.Configure);
		h.Step(State.DataLoaded);
		h.State = State.Historical;
		h.Bars.Count = data.Count;
		for (int i = 0; i < data.Count; i++)
		{
			Push(h, data[i]);
			h.CurrentBar = i;
			h.IsFirstTickOfBar = true;
			h.Bar();
		}
		// only the final redraw counts (not ones made on order bars)
		string last = DrawingLog.Calls.Where(c => c.StartsWith("TextFixed ")).LastOrDefault();
		return last ?? "";
	}

	// Historical OnBarClose run with custom settings; copies the trade/setup CSVs + report rows with a suffix
	private static Harness RunCustom(List<BarData> data, Action<Harness> configure, string outDir, string suffix)
	{
		Harness h = NewHarness(true);
		h.ExportBarsCsv = false;
		h.PrintTradeList = false;
		configure(h);
		h.Step(State.Configure);
		h.Step(State.DataLoaded);
		h.State = State.Historical;
		h.Bars.Count = data.Count;
		for (int i = 0; i < data.Count; i++)
		{
			Push(h, data[i]);
			h.CurrentBar = i;
			h.IsFirstTickOfBar = true;
			h.Bar();
		}
		h.Step(State.Transition);
		h.Step(State.Terminated);
		string csvDir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RutaCryptoMirror");
		File.Copy(Path.Combine(csvDir, "NQ_12-26_5Minute_trades.csv"), Path.Combine(outDir, "nt_trades_" + suffix + ".csv"), true);
		File.Copy(Path.Combine(csvDir, "NQ_12-26_5Minute_setups.csv"), Path.Combine(outDir, "nt_setups_" + suffix + ".csv"), true);
		File.WriteAllLines(Path.Combine(outDir, "nt_buckets_" + suffix + ".csv"), h.EngineForTest.BucketRows().ToArray());
		return h;
	}

	// After a session's closed trades reach the goal (minus tolerance) or the max loss, no trade may start in that session
	private static bool NoEntryAfterDayDone(Harness h, List<BarData> data, double goalReached, double lossReached)
	{
		int[] session = new int[data.Count];
		for (int i = 0, k = 0; i < data.Count; i++) { if (IsSessionStart(data[i])) k++; session[i] = k; }
		Dictionary<int, double> pnl = new Dictionary<int, double>();
		Dictionary<int, int> doneAtBar = new Dictionary<int, int>();
		foreach (RutaCryptoMirror.TvEngine.TvTrade t in h.EngineForTest.Trades)
		{
			int sx = session[t.ExitBar];
			if (!pnl.ContainsKey(sx)) pnl[sx] = 0;
			pnl[sx] += t.Points * t.Qty * h.Instrument.MasterInstrument.PointValue;
			if (!doneAtBar.ContainsKey(sx) && (pnl[sx] >= goalReached || pnl[sx] <= lossReached)) doneAtBar[sx] = t.ExitBar;
		}
		return h.EngineForTest.Trades.All(t => !doneAtBar.ContainsKey(session[t.EntryBar]) || t.EntryBar <= doneAtBar[session[t.EntryBar]]);
	}

	private static int failures;
	private static void Check(bool ok, string what)
	{
		Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
		if (!ok) failures++;
	}

	public static int Main(string[] args)
	{
		string outDir = args.Length > 0 ? args[0] : ".";
		int n = 6000;
		int seed = args.Length > 1 ? int.Parse(args[1]) : 12345;
		List<BarData> bars = MakeBars(n, seed);

		// write raw bars for the Python reference
		StringBuilder raw = new StringBuilder("time,open,high,low,close,volume,new_session\n");
		foreach (BarData b in bars)
			raw.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5},{6}", b.T, b.O, b.H, b.L, b.C, b.V, IsSessionStart(b) ? 1 : 0));
		File.WriteAllText(Path.Combine(outDir, "bars_input.csv"), raw.ToString());

		// ---- Run 1: historical, OnBarClose ----
		Harness a = NewHarness(true);
		a.Step(State.Configure);
		a.Step(State.DataLoaded);
		a.State = State.Historical;
		a.Bars.Count = n;
		for (int i = 0; i < n; i++)
		{
			Push(a, bars[i]);
			a.CurrentBar = i;
			a.IsFirstTickOfBar = true;
			a.Bar();
		}
		a.Step(State.Transition);
		a.Step(State.Terminated);

		string csvDir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RutaCryptoMirror");
		File.Copy(Path.Combine(csvDir, "NQ_12-26_5Minute_bars.csv"), Path.Combine(outDir, "nt_bars.csv"), true);
		File.Copy(Path.Combine(csvDir, "NQ_12-26_5Minute_trades.csv"), Path.Combine(outDir, "nt_trades.csv"), true);
		File.Copy(Path.Combine(csvDir, "NQ_12-26_5Minute_setups.csv"), Path.Combine(outDir, "nt_setups.csv"), true);
		File.WriteAllLines(Path.Combine(outDir, "nt_buckets.csv"), a.EngineForTest.BucketRows().ToArray());

		RutaCryptoMirror.TvEngine eng = a.EngineForTest;
		List<RutaCryptoMirror.TvEngine.TvTrade> trades = eng.Trades;
		Console.WriteLine("Summary: " + eng.SummaryLine());
		Check(trades.Count > 20, "synthetic data produces a meaningful number of trades (" + trades.Count + ")");
		Check(trades.All(t => t.EntryBar >= a.BarsRequiredToTrade), "no trade before BarsRequiredToTrade");
		Check(trades.Any(t => t.ExitSignal == "Take Profit"), "has Take Profit exits");
		Check(trades.Any(t => t.ExitSignal == "Stop Loss"), "has Stop Loss exits");
		Check(trades.Any(t => t.ExitSignal == "Long" || t.ExitSignal == "Short"), "has reversals");
		Check(trades.All(t => t.ExitBar > t.EntryBar), "a trade never exits on its entry bar");
		for (int i = 1; i < trades.Count; i++)
			if (trades[i].EntryBar < trades[i - 1].ExitBar) { Check(false, "trades overlap at #" + trades[i].Number); break; }

		// Every TradingView fill must produce exactly one NinjaTrader order call on the same bar
		List<string> expected = new List<string>();
		int pos = 0;
		int lastExitBar = -1;
		foreach (RutaCryptoMirror.TvEngine.TvTrade t in trades)
		{
			if (t.EntryBar != lastExitBar)	// fresh entry from flat (a reversal was emitted with the previous trade)
				expected.Add(t.EntryBar + (t.Direction > 0 ? " EnterLong 1 Long" : " EnterShort 1 Short"));
			bool reversal = t.ExitSignal == "Long" || t.ExitSignal == "Short";
			if (reversal)
				expected.Add(t.ExitBar + (t.Direction > 0 ? " EnterShort 1 Short" : " EnterLong 1 Long"));
			else
				expected.Add(t.ExitBar + (t.Direction > 0 ? " ExitLong " : " ExitShort ") + t.ExitSignal + (t.Direction > 0 ? " Long" : " Short"));
			lastExitBar = reversal ? t.ExitBar : -1;
			pos = reversal ? -t.Direction : 0;
		}
		if (pos != 0 || eng.Position != 0)
		{
			// the last position is still open: its entry order is already in the list when it came from a reversal
			if (lastExitBar == -1)
				expected.Add(a.Orders.Last());
		}
		Check(expected.SequenceEqual(a.Orders), "NinjaTrader order calls match TradingView fills one-to-one (" + a.Orders.Count + " orders)");
		if (!expected.SequenceEqual(a.Orders))
		{
			for (int i = 0; i < Math.Max(expected.Count, a.Orders.Count); i++)
			{
				string e = i < expected.Count ? expected[i] : "<none>";
				string g = i < a.Orders.Count ? a.Orders[i] : "<none>";
				if (e != g) { Console.WriteLine("   first diff at " + i + ": expected '" + e + "' got '" + g + "'"); break; }
			}
		}
		Check(a.Printed.Count(p => p.StartsWith("#")) == trades.Count, "one Output line per closed trade");
		Check(DrawingLog.Calls.Count(c => c.StartsWith("Line ")) == trades.Count, "one trade line per closed trade");

		// ---- Run 2: realtime, OnEachTick (processes the closed bar on the next bar's first tick) ----
		Harness b2 = NewHarness(false);
		b2.PrintTradeList = false;
		b2.Calculate = Calculate.OnEachTick;
		b2.Step(State.Configure);
		b2.Step(State.DataLoaded);
		b2.State = State.Realtime;
		for (int i = 0; i < n; i++)
		{
			Push(b2, bars[i]);
			b2.CurrentBar = i;
			for (int tick = 0; tick < 3; tick++)
			{
				b2.IsFirstTickOfBar = tick == 0;
				b2.Bar();
			}
		}
		List<RutaCryptoMirror.TvEngine.TvTrade> t2 = b2.EngineForTest.Trades;
		List<RutaCryptoMirror.TvEngine.TvTrade> t1 = trades.Where(t => t.ExitBar < n - 1).ToList();
		bool same = t1.Count == t2.Count;
		for (int i = 0; same && i < t1.Count; i++)
			same = t1[i].EntryBar == t2[i].EntryBar && t1[i].ExitBar == t2[i].ExitBar && t1[i].ExitSignal == t2[i].ExitSignal && t1[i].EntryPrice == t2[i].EntryPrice;
		Check(same, "OnEachTick realtime processing gives the same trades as OnBarClose");

		// ---- Status box: always visible after the historical load, and explains an empty chart ----
		string lastBox = DrawingLog.Calls.Where(c => c.StartsWith("TextFixed ")).LastOrDefault() ?? "";
		Check(a.Printed.Any(p => p.StartsWith("SETUP REPORT")), "prints the setup report at the end of the historical load");
		Check(a.Printed.Any(p => p.Contains("Since bar 200")), "prints the filter diagnostics at the end of the historical load");

		Check(RunStatusOnly(bars.Take(150).ToList(), false).Contains("NOT ENOUGH DATA"), "status box says NOT ENOUGH DATA with 150 bars");
		List<BarData> flat = bars.Select(x => { BarData y = x; y.V = 1; return y; }).ToList();
		string flatBox = RunStatusOnly(flat, false);
		Check(flatBox.Contains("NO VOLUME") && flatBox.Contains("Closed trades: 0"), "status box explains zero trades on data without volume");
		Check(RunStatusOnly(bars, false).Contains("Bars processed: " + n), "status box drawn on the last historical bar with no order on it");

		Console.WriteLine("Status box: " + lastBox);

		// ---- Setup report consistency: a taken trade that ended by TP/SL must have the same hindsight outcome ----
		Dictionary<int, RutaCryptoMirror.TvEngine.Setup> takenByBar = new Dictionary<int, RutaCryptoMirror.TvEngine.Setup>();
		foreach (RutaCryptoMirror.TvEngine.Setup x in eng.Candidates)
			if (x.Taken)
				takenByBar[x.Bar] = x;
		bool consistent = trades.All(t => takenByBar.ContainsKey(t.EntryBar));
		foreach (RutaCryptoMirror.TvEngine.TvTrade t in trades)
		{
			if (!consistent || (t.ExitSignal != "Take Profit" && t.ExitSignal != "Stop Loss"))
				continue;
			RutaCryptoMirror.TvEngine.Setup x = takenByBar[t.EntryBar];
			consistent = x.ResolvedBar == t.ExitBar && (x.Outcome > 0) == (t.ExitSignal == "Take Profit") && x.Direction == t.Direction;
		}
		Check(consistent, "every real trade appears as a taken setup, and TP/SL trades match their hindsight outcome");
		Check(eng.Candidates.Any(x => !x.Taken && x.Blockers == "SMA") && eng.Candidates.Any(x => !x.Taken && x.Blockers == "HA"), "skipped setups record the blocking filter");

		// ---- Filter switch: SMA trend filter off (Python recomputes the same trades) ----
		Harness noSma = NewHarness(true);
		noSma.ExportBarsCsv = false;
		noSma.UseTrendFilter = false;
		noSma.Step(State.Configure);
		noSma.Step(State.DataLoaded);
		noSma.State = State.Historical;
		noSma.Bars.Count = n;
		for (int i = 0; i < n; i++)
		{
			Push(noSma, bars[i]);
			noSma.CurrentBar = i;
			noSma.IsFirstTickOfBar = true;
			noSma.Bar();
		}
		noSma.Step(State.Terminated);
		File.Copy(Path.Combine(csvDir, "NQ_12-26_5Minute_trades.csv"), Path.Combine(outDir, "nt_trades_nosma.csv"), true);
		Check(noSma.EngineForTest.Trades.Count > trades.Count, "turning the SMA filter off gives more trades (" + noSma.EngineForTest.Trades.Count + " vs " + trades.Count + ")");

		// ---- Dollar exits: $500 stop / $1000 target on 1 NQ ($20/pt) = 25 / 50 points ----
		Harness usdClose = RunCustom(bars, h => { h.ExitUnits = RutaMirrorExitUnits.Dollars; h.StopLossDollars = 500; h.TakeProfitDollars = 1000; }, outDir, "usd_close");
		Harness usdOrders = RunCustom(bars, h => { h.ExitUnits = RutaMirrorExitUnits.Dollars; h.StopLossDollars = 500; h.TakeProfitDollars = 1000;
			h.ExitExecution = RutaMirrorExitExecution.StopTargetOrders; }, outDir, "usd_orders");
		List<RutaCryptoMirror.TvEngine.TvTrade> uo = usdOrders.EngineForTest.Trades;
		Check(uo.Count > 20, "stop/target-order mode trades (" + uo.Count + ")");
		Check(uo.Where(t => t.ExitSignal == "Stop Loss").All(t => Math.Abs((t.EntryPrice - t.ExitPrice) * t.Direction - 25) < 1e-9 || (t.EntryPrice - t.ExitPrice) * t.Direction > 25),
			"stop/target orders: a stop loses exactly $500 (25 pts), or more only on a gap");
		Check(uo.Where(t => t.ExitSignal == "Take Profit").All(t => (t.ExitPrice - t.EntryPrice) * t.Direction >= 50 - 1e-9),
			"stop/target orders: a target wins at least $1000 (50 pts)");
		Check(uo.Any(t => t.ExitSignal == "Stop Loss" && Math.Abs((t.EntryPrice - t.ExitPrice) * t.Direction - 25) < 1e-9),
			"stop/target orders: most stops fill exactly at the level");
		Check(usdClose.EngineForTest.Trades.Where(t => t.ExitSignal == "Stop Loss").Any(t => (t.EntryPrice - t.ExitPrice) * t.Direction > 25),
			"on-close mode can overshoot the $ stop (why StopTargetOrders exists)");
		int entries = usdOrders.Orders.Count(o => o.Contains(" EnterLong ") || o.Contains(" EnterShort "));
		int stops = usdOrders.Orders.Count(o => o.Contains(" SetStopLoss ") && o.Contains(" Price "));
		int targets = usdOrders.Orders.Count(o => o.Contains(" SetProfitTarget ") && o.Contains(" Price "));
		Check(entries > 0 && stops == entries && targets == entries, "stop/target orders: SetStopLoss + SetProfitTarget (Price) before every entry");
		Check(!usdOrders.Orders.Any(o => o.Contains(" ExitLong ") || o.Contains(" ExitShort ")), "stop/target orders: no on-close exit orders are sent");
		Check(!a.Orders.Any(o => o.Contains("SetStopLoss") || o.Contains("SetProfitTarget")), "TradingView mode never sends stop/target orders");

		// ---- Fixed $ with ATR sizing: MNQ ($2/pt), $1000 stop / $1500 target, contracts for a ~2 ATR stop, cap 12 ----
		Harness usdAtr = RunCustom(bars, h => { h.Instrument.MasterInstrument.PointValue = 2; h.ExitUnits = RutaMirrorExitUnits.DollarsSizedByAtr;
			h.StopLossDollars = 1000; h.TakeProfitDollars = 1500; h.MaxContracts = 12; h.ExitExecution = RutaMirrorExitExecution.StopTargetOrders; }, outDir, "usd_atr");
		List<RutaCryptoMirror.TvEngine.TvTrade> ua = usdAtr.EngineForTest.Trades;
		Check(ua.Select(t => t.Qty).Distinct().Count() > 1, "ATR sizing: contract count changes with volatility (" + string.Join(",", ua.Select(t => t.Qty).Distinct().OrderBy(q => q).Select(q => q.ToString()).ToArray()) + ")");
		Check(ua.Any(t => t.Qty == 12), "ATR sizing: Max Contracts cap is applied");
		// with q contracts the tick-rounded stop is within 0.125 pt x q x $2 of the $ amount; more only on a gap
		Check(ua.Where(t => t.ExitSignal == "Stop Loss").All(t => (t.EntryPrice - t.ExitPrice) * t.Direction * t.Qty * 2 >= 1000 - 0.25 * t.Qty - 1e-9),
			"ATR sizing: every stop loses ~$1000 (never less)");
		Check(ua.Where(t => t.ExitSignal == "Stop Loss").Count(t => Math.Abs((t.EntryPrice - t.ExitPrice) * t.Direction * t.Qty * 2 - 1000) <= 0.25 * t.Qty + 1e-9)
			>= ua.Count(t => t.ExitSignal == "Stop Loss") * 8 / 10, "ATR sizing: most stops lose $1000 +- tick rounding");
		Check(ua.Where(t => t.ExitSignal == "Take Profit").All(t => (t.ExitPrice - t.EntryPrice) * t.Direction * t.Qty * 2 >= 1500 - 0.25 * t.Qty - 1e-9),
			"ATR sizing: every target wins ~$1500");
		Check(usdAtr.Orders.Where(o => o.Contains(" EnterLong ") || o.Contains(" EnterShort ")).All(o => int.Parse(o.Split(' ')[2]) >= 1 && int.Parse(o.Split(' ')[2]) <= 12),
			"ATR sizing: NinjaTrader entry orders carry the sized quantity");
		string sizeLine = usdAtr.Printed.FirstOrDefault(p => p.Contains("Contracts per entry")) ?? "";
		Check(sizeLine.Length > 0, "setup report prints the contracts-per-entry line: " + sizeLine.Trim());

		// ---- PDH/PDL filter on (TradingView exits otherwise) ----
		Harness pd = RunCustom(bars, h => { h.UsePdhPdlFilter = true; }, outDir, "pd");
		Check(pd.EngineForTest.Trades.Count > 5, "PDH/PDL filter: still trades (" + pd.EngineForTest.Trades.Count + " vs " + trades.Count + " without)");
		Check(pd.EngineForTest.Candidates.Any(x => !x.Taken && x.Blockers == "PD50%"), "PD filter at 50%: skipped setups name PD50% as the blocker");
		Check(pd.EngineForTest.StatsText(200).Contains("PDH/PDL filter ON at 50% of the range - longs above"), "PD filter: stats box shows the current levels");
		Harness pd100 = RunCustom(bars, h => { h.UsePdhPdlFilter = true; h.PdLevelPercent = 100; }, outDir, "pd100");
		Check(pd100.EngineForTest.Candidates.Any(x => !x.Taken && (x.Blockers == "PDH" || x.Blockers == "PDL")), "PD filter at 100%: skipped setups name PDH / PDL as the blocker");
		Check(pd.EngineForTest.Trades.Count > pd100.EngineForTest.Trades.Count, "PD filter: 50% allows more trades than 100% (" + pd.EngineForTest.Trades.Count + " vs " + pd100.EngineForTest.Trades.Count + ")");
		Check(!a.EngineForTest.StatsText(200).Contains("PDH/PDL"), "PDH/PDL filter off: no PDH/PDL line in the stats box");

		// ---- Daily rules: the planned prop setup (MNQ, $1500 / $750, PD 50%, goal $1500 -$100, max loss $2000) ----
		Harness daily = RunCustom(bars, h => { h.Instrument.MasterInstrument.PointValue = 2; h.ExitUnits = RutaMirrorExitUnits.DollarsSizedByAtr;
			h.StopLossDollars = 1500; h.TakeProfitDollars = 750; h.MaxContracts = 40; h.ExitExecution = RutaMirrorExitExecution.StopTargetOrders;
			h.UsePdhPdlFilter = true; h.PdLevelPercent = 50; h.DailyProfitGoal = 1500; h.DailyGoalTolerance = 100; h.DailyMaxLoss = 2000; }, outDir, "daily");
		Check(daily.EngineForTest.Trades.Count > 0, "daily rules: trades (" + daily.EngineForTest.Trades.Count + ")");
		Check(NoEntryAfterDayDone(daily, bars, 1400, -2000), "daily rules: no new trade once a session reached +$1400 or -$2000");
		Check(daily.EngineForTest.Candidates.Any(x => x.Blockers == "daily goal" || x.Blockers == "loss room" || x.Blockers == "daily loss"), "daily rules: blocked signals are labelled");
		Check(daily.EngineForTest.StatsText(200).Contains("Today: "), "daily rules: stats box shows today's result");
		Harness dailyClose = RunCustom(bars, h => { h.DailyProfitGoal = 1500; h.DailyGoalTolerance = 100; h.DailyMaxLoss = 1000; }, outDir, "daily_close");
		Check(NoEntryAfterDayDone(dailyClose, bars, 1400, -1000), "daily rules (on-close exits): no new trade once a session is done");

		// ---- Trading hours ----
		TimeSpan xs = new TimeSpan(18, 0, 0), xe = new TimeSpan(16, 45, 0);
		Harness hx = RunCustom(bars, h => { h.UseTradingHours = true; h.TradeStartTime = new DateTime(2000, 1, 1, 18, 0, 0); h.TradeEndTime = new DateTime(2000, 1, 1, 16, 45, 0); }, outDir, "hours_x");
		Check(hx.EngineForTest.Trades.All(t => t.EntryTime.TimeOfDay >= xs || t.EntryTime.TimeOfDay < xe), "hours 18:00-16:45: every entry inside the window");
		Check(hx.EngineForTest.Trades.All(t => (t.ExitTime.TimeOfDay >= xs || t.ExitTime.TimeOfDay < xe) || (t.ExitSignal == "End time" && t.ExitTime.TimeOfDay == xe)),
			"hours 18:00-16:45: flattened on the first bar at 16:45, never held past it");
		Check(hx.EngineForTest.Trades.Any(t => t.ExitSignal == "End time"), "hours: some trades are flattened at the end time");
		TimeSpan rs = new TimeSpan(9, 30, 0), re = new TimeSpan(16, 0, 0);
		Harness hr = RunCustom(bars, h => { h.UseTradingHours = true; h.TradeStartTime = new DateTime(2000, 1, 1, 9, 30, 0); h.TradeEndTime = new DateTime(2000, 1, 1, 16, 0, 0); h.FlattenAtEndTime = false; }, outDir, "hours_rth");
		Check(hr.EngineForTest.Trades.All(t => t.EntryTime.TimeOfDay >= rs && t.EntryTime.TimeOfDay < re), "hours 09:30-16:00: every entry inside the window");
		Check(!hr.EngineForTest.Trades.Any(t => t.ExitSignal == "End time"), "hours without flatten: no End time exits");

		Console.WriteLine(failures == 0 ? "ALL C# CHECKS PASSED" : failures + " C# CHECK(S) FAILED");
		return failures == 0 ? 0 : 1;
	}
}
