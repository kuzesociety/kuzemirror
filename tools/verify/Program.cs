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
using NinjaTrader.NinjaScript.AddOns;
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
		string acc = Path.Combine(csvDir, "NQ_12-26_5Minute_accounts.csv");
		if (h.UseAccountRotation && File.Exists(acc))
			File.Copy(acc, Path.Combine(outDir, "nt_accounts_" + suffix + ".csv"), true);
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

	// Route to Prop Account Manager: historical bars first, then live bars. With a broker, each live bar's ticks
	// (open, nearer extreme, other extreme, close) go to the simulated broker before the strategy sees the bar close.
	private static Harness RunRouted(List<BarData> data, int realtimeFrom, Action<Harness> configure, RutaPropRouter router, FakeBroker broker, DateTime[] clock)
	{
		Harness h = NewHarness(false);
		h.PrintTradeList = false;
		configure(h);
		h.Step(State.Configure);
		h.Step(State.DataLoaded);
		h.State = State.Historical;
		h.Bars.Count = data.Count;
		for (int i = 0; i < data.Count; i++)
		{
			BarData b = data[i];
			if (i == realtimeFrom)
			{
				h.Step(State.Transition);
				h.Step(State.Realtime);
			}
			if (broker != null && i >= realtimeFrom)
			{
				DateTime start = b.T.AddMinutes(-5);
				double[] path = b.C >= b.O ? new double[] { b.O, b.L, b.H, b.C } : new double[] { b.O, b.H, b.L, b.C };
				for (int k = 0; k < 4; k++)
				{
					clock[0] = start.AddSeconds(5 + 70 * k);
					broker.SetPrice(path[k]);
					router.Poll();
				}
			}
			clock[0] = b.T;
			Push(h, b);
			h.CurrentBar = i;
			h.IsFirstTickOfBar = true;
			h.Bar();
		}
		return h;
	}

	private static bool SameTrades(List<RutaCryptoMirror.TvEngine.TvTrade> a, List<RutaCryptoMirror.TvEngine.TvTrade> b)
	{
		if (a.Count != b.Count)
			return false;
		for (int i = 0; i < a.Count; i++)
			if (a[i].EntryBar != b[i].EntryBar || a[i].ExitBar != b[i].ExitBar || a[i].Direction != b[i].Direction || a[i].Qty != b[i].Qty
				|| a[i].EntryPrice != b[i].EntryPrice || a[i].ExitPrice != b[i].ExitPrice || a[i].ExitSignal != b[i].ExitSignal)
				return false;
		return true;
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

		// ---- Prop account rotation (simulated) ----
		Action<Harness> plan = h => { h.Instrument.MasterInstrument.PointValue = 2; h.ExitUnits = RutaMirrorExitUnits.DollarsSizedByAtr;
			h.StopLossDollars = 1500; h.TakeProfitDollars = 750; h.MaxContracts = 40; h.ExitExecution = RutaMirrorExitExecution.StopTargetOrders;
			h.UsePdhPdlFilter = true; h.PdLevelPercent = 50; h.DailyProfitGoal = 1500; h.DailyGoalTolerance = 100; h.DailyMaxLoss = 2000;
			h.UseAccountRotation = true; h.PropAccountList = "TS-1=Topstep50K, TS-2=Topstep50K, LU-1=LucidFlex50K, LU-2=LucidPro50K"; };
		Harness rotEvery = RunCustom(bars, h => { plan(h); h.RotationMode = RutaMirrorRotationMode.EveryTrade; }, outDir, "rot_every");
		Harness rotDay = RunCustom(bars, h => { plan(h); h.RotationMode = RutaMirrorRotationMode.UntilDayDone; h.BlockTradesOverAccountMaxLoss = true; }, outDir, "rot_day");
		Harness rotDll = RunCustom(bars, h => { plan(h); h.PropAccountList = "A=Topstep50K-DLL; B=Custom; C=LucidPro50K-DLL"; h.CustomProfitTarget = 3000; h.CustomMaxLoss = 1500;
			h.CustomDrawdownType = RutaMirrorDrawdownType.IntradayTrailing; h.CustomDailyLossLimit = 0; h.CustomConsistencyPct = 40; }, outDir, "rot_dll");
		Harness rotClose = RunCustom(bars, h => { h.UseAccountRotation = true; h.PropAccountList = "X=Custom, Y=Topstep100K"; h.RotationMode = RutaMirrorRotationMode.UntilDayDone;
			h.RestartFinishedAccounts = false; h.CustomProfitTarget = 2500; h.CustomMaxLoss = 1200; h.CustomDrawdownType = RutaMirrorDrawdownType.Static;
			h.CustomDailyLossLimit = 600; h.CustomConsistencyPct = 0; }, outDir, "rot_close");
		foreach (Harness h in new Harness[] { rotEvery, rotDay, rotDll, rotClose })
		{
			RutaCryptoMirror.TvEngine e = h.EngineForTest;
			Check(e.AccountsError == null && e.Trades.Count > 0 && e.Trades.All(t => !string.IsNullOrEmpty(t.Account)), "rotation: every trade belongs to an account (" + e.Trades.Count + " trades)");
			Check(e.Accounts.All(acc => e.AccountsDashboard().Contains(acc.Name)), "rotation: dashboard lists every account");
		}
		Check(new Harness[] { rotEvery, rotDay, rotDll, rotClose }.Sum(h => h.EngineForTest.AccountEvents.Count(x => x.Event == "PASSED" || x.Event == "FAILED")) > 0,
			"rotation: evaluations finish (passed or failed) over 6000 bars");
		List<string> used = rotEvery.EngineForTest.Trades.Select(t => t.Account.Split(' ')[0]).Distinct().OrderBy(x => x).ToList();
		Check(string.Join(",", used.ToArray()) == "LU-1,LU-2,TS-1,TS-2", "rotation every trade: all 4 accounts take trades, Lucid Pro without daily limit included (" + string.Join(",", used.ToArray()) + ")");
		Check(!rotDll.EngineForTest.Trades.Any(t => t.Account.StartsWith("C ")) && rotDll.EngineForTest.AccountsDashboard().Contains("CAN'T FIT $1,"),
			"rotation: Lucid Pro WITH the $1,200 daily limit never takes a $1,500-risk trade (block on) and the dashboard says why");
		Check(rotClose.EngineForTest.AccountEvents.Count(x => x.Event == "START") == 2, "no restarts: each slot runs one evaluation");
		Check(rotDll.EngineForTest.Trades.Any(t => t.ExitSignal == "Firm daily loss" || t.ExitSignal == "Account max loss") ||
			rotClose.EngineForTest.Trades.Any(t => t.ExitSignal == "Firm daily loss" || t.ExitSignal == "Account max loss"), "rotation: firm limits close trades intrabar");
		Harness badSpec = RunCustom(bars.Take(400).ToList(), h => { h.UseAccountRotation = true; h.PropAccountList = "Q=NoSuchFirm, Q2"; }, outDir, "rot_bad");
		Check(badSpec.EngineForTest.AccountsError != null && badSpec.EngineForTest.AccountsError.Contains("unknown plan 'NoSuchFirm'"), "rotation: a wrong plan name is reported");
		Console.WriteLine(rotEvery.EngineForTest.AccountsDashboard());

		// ---- Prop Account Manager router (simulated broker) ----
		RouterTests.Run(Check, outDir);

		// ---- Strategy -> Prop Account Manager ----
		int live0 = 3000;
		Action<Harness> routedPlan = h => { plan(h); h.RouteToPropManager = true; };
		Harness noRules = RunCustom(bars, h => { plan(h); h.DailyProfitGoal = 0; h.DailyMaxLoss = 0; h.UseAccountRotation = false; }, outDir, "route_ref");
		DateTime[] clock = { bars[0].T };
		FakeBroker dryBroker = new FakeBroker("TS-1", "TS-2", "LU-1", "LU-2");
		RutaPropRouter dry = new RutaPropRouter(dryBroker, null, false);
		dry.Clock = () => clock[0];
		RutaPropSettings ds = dry.GetSettings();
		ds.DailyGoal = 0; ds.CustomTarget = 1e9; ds.CustomMaxLoss = 1e9; ds.CustomConsistencyPct = 0;
		dry.SetSettings(ds);
		foreach (string acct in new[] { "TS-1", "TS-2", "LU-1", "LU-2" })
			dry.AddSlot(acct, "Custom");
		RutaPropRouter.UseForTesting(dry);
		Harness routed = RunRouted(bars, live0, routedPlan, dry, null, clock);
		Check(routed.Orders.Count == 0, "route: the strategy sends no orders of its own");
		Check(SameTrades(routed.EngineForTest.Trades, noRules.EngineForTest.Trades),
			"route: the strategy takes every signal (its own daily goal / max loss and account simulator are off) - " + routed.EngineForTest.Trades.Count + " trades");
		List<RutaCryptoMirror.TvEngine.TvTrade> liveTrades = routed.EngineForTest.Trades.Where(t => t.EntryBar >= live0).ToList();
		double expect = liveTrades.Sum(t => t.Points * t.Qty * 2.0);
		List<RutaPropRow> dryRows = dry.Rows();
		Check(Math.Abs(dryRows.Sum(r => r.Pnl) - expect) < 1e-6 && dryRows.Sum(r => r.Trades) == liveTrades.Count && dryRows.All(r => r.Trades >= liveTrades.Count / 4),
			"route (dry run): only live-bar trades are routed, rotated over 4 accounts, each booked with the strategy's result ($" + expect.ToString("0", CultureInfo.InvariantCulture) + ")");
		Check(dryRows.Sum(r => r.Days) > 8 && dry.SessionText().Contains(bars.Last(b => IsSessionStart(b)).T.ToString("yyyy-MM-dd")), "route: the strategy reports each new session to the manager");

		string liveCfg = Path.Combine(outDir, "route_live.xml");
		foreach (string f in new[] { liveCfg, Path.ChangeExtension(liveCfg, ".log") })
			if (File.Exists(f)) File.Delete(f);
		FakeBroker lb = new FakeBroker("TS-1", "TS-2", "LU-1", "LU-2");
		RutaPropRouter live = new RutaPropRouter(lb, liveCfg, false);
		live.Clock = () => clock[0];
		live.AddSlot("TS-1", "Topstep50K");
		live.AddSlot("TS-2", "Topstep50K");
		live.AddSlot("LU-1", "LucidFlex50K");
		live.AddSlot("LU-2", "LucidPro50K");
		live.SetMode(RutaPropMode.Live);
		RutaPropRouter.UseForTesting(live);
		Harness routedLive = RunRouted(bars, live0, h => { routedPlan(h); h.ExitUnits = RutaMirrorExitUnits.Dollars; h.FixedContracts = 5; }, live, lb, clock);
		string[] log = File.ReadAllLines(Path.ChangeExtension(liveCfg, ".log"));
		List<RutaPropRow> liveRows = live.Rows();
		int closedTargets = log.Count(l => l.Contains("closed (target)")), closedStops = log.Count(l => l.Contains("closed (stop)"));
		Check(routedLive.Orders.Count == 0 && liveRows.Sum(r => r.Trades) > 8 && closedTargets > 0 && closedStops > 0,
			"route (live, simulated broker): " + liveRows.Sum(r => r.Trades) + " trades on 4 accounts, " + closedTargets + " at the target, " + closedStops + " at the stop");
		Check(log.Where(l => l.Contains("closed (target)")).All(l => l.Contains(": +$750.00 |")), "route (live): every target exit is exactly +$750 (5 MNQ, 75 points from the real fill)");
		Check(log.Where(l => l.Contains("closed (stop)")).All(l => { int k = l.IndexOf(": -$"); return k > 0 && double.Parse(l.Substring(k + 4, l.IndexOf(' ', k + 4) - k - 4).Replace(",", ""), CultureInfo.InvariantCulture) >= 1500; }),
			"route (live): every stop exit loses at least $1,500 (more only through slippage past the stop)");
		Check(Math.Abs(liveRows.Sum(r => r.Pnl) - lb.Realized.Values.Sum()) < 1e-6, "route (live): the manager's P&L per account adds up to the broker's realized P&L ($"
			+ lb.Realized.Values.Sum().ToString("0", CultureInfo.InvariantCulture) + ")");
		Check(liveRows.All(r => lb.Pos.ContainsKey(r.Account) == false || lb.Pos[r.Account] == 0 || r.Status == "IN TRADE"), "route (live): no account holds a position the manager doesn't know about");
		Check(!log.Any(l => l.Contains("!!!") && !l.Contains("is at the max loss line")), "route (live): no errors, rejections or forced flattens in the log");
		Check(liveRows.Any(r => r.Status == RutaPropRouter.TargetReached || r.Status == RutaPropRouter.MaxLossHit) || liveRows.Sum(r => r.Days) > 8,
			"route (live): accounts progress through the evaluation (" + string.Join(", ", liveRows.Select(r => r.Account + " " + r.Status + " " + RutaPropRouter.Money(r.Pnl)).ToArray()) + ")");

		// stress: TradingView exits (on the close) + reversals, every signal routed, one account at a time
		FakeBroker sb = new FakeBroker("A", "B", "C");
		string stressCfg = Path.Combine(outDir, "route_stress.xml");
		foreach (string f in new[] { stressCfg, Path.ChangeExtension(stressCfg, ".log") })
			if (File.Exists(f)) File.Delete(f);
		RutaPropRouter stress = new RutaPropRouter(sb, stressCfg, false);
		stress.Clock = () => clock[0];
		RutaPropSettings ss = stress.GetSettings();
		ss.DailyGoal = 0; ss.Rotation = RutaPropRotation.UntilDayDone; ss.CustomTarget = 1e9; ss.CustomMaxLoss = 1e9; ss.CustomConsistencyPct = 0; ss.CommissionPerContract = 0;
		stress.SetSettings(ss);
		foreach (string acct in new[] { "A", "B", "C" })
			stress.AddSlot(acct, "Custom");
		stress.SetMode(RutaPropMode.Live);
		RutaPropRouter.UseForTesting(stress);
		Harness st = RunRouted(bars, live0, h => { h.Instrument.MasterInstrument.PointValue = 2; h.ExitUnits = RutaMirrorExitUnits.Dollars; h.FixedContracts = 2;
			h.StopLossDollars = 500; h.TakeProfitDollars = 1000; h.RouteToPropManager = true; }, stress, sb, clock);
		string[] slog = File.ReadAllLines(Path.ChangeExtension(stressCfg, ".log"));
		List<RutaPropRow> srows = stress.Rows();
		int reversals = slog.Count(l => l.Contains("exit (reversal)"));
		Check(srows[0].Trades > 10 && srows.Skip(1).All(r => r.Trades == 0) && reversals > 0,
			"route stress (live, TradingView exits): " + srows[0].Trades + " trades incl. " + reversals + " reversals, all on account A (UntilDayDone, no day limits)");
		Check(Math.Abs(srows.Sum(r => r.Pnl) - sb.Realized.Values.Sum()) < 1e-6 && slog.Where(l => l.Contains("closed (target)")).All(l => l.Contains(": +$1,000.00 |")),
			"route stress: booked P&L = broker realized P&L, every target exactly +$1,000");
		bool open = srows[0].Status == "IN TRADE";
		int engineLive = st.EngineForTest.Trades.Count(t => t.EntryBar >= live0) + (st.EngineForTest.Position != 0 ? 1 : 0);
		Check(srows[0].Trades + (open ? 1 : 0) == engineLive && (sb.Pos["A"] == 0 || open && sb.Pos["A"] == st.EngineForTest.Position * 2),
			"route stress: one account trade per strategy trade; the account is flat or holds the strategy's open position");
		Check(!slog.Any(l => l.Contains("!!!") || l.Contains("dropped") || l.Contains("skipped")), "route stress: no errors, drops or skips");
		RutaPropRouter.UseForTesting(null);

		Console.WriteLine(failures == 0 ? "ALL C# CHECKS PASSED" : failures + " C# CHECK(S) FAILED");
		return failures == 0 ? 0 : 1;
	}
}
