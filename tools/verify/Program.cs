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

	private static void Push(Harness h, BarData b)
	{
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
		StringBuilder raw = new StringBuilder("time,open,high,low,close,volume\n");
		foreach (BarData b in bars)
			raw.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5}", b.T, b.O, b.H, b.L, b.C, b.V));
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
		Check(a.Printed.Any(p => p.Contains("started on")), "prints a 'started' line when data is loaded");
		Check(a.Printed.Any(p => p.Contains("Since bar 200")), "prints the filter diagnostics at the end of the historical load");

		Check(RunStatusOnly(bars.Take(150).ToList(), false).Contains("NOT ENOUGH DATA"), "status box says NOT ENOUGH DATA with 150 bars");
		List<BarData> flat = bars.Select(x => { BarData y = x; y.V = 1; return y; }).ToList();
		string flatBox = RunStatusOnly(flat, false);
		Check(flatBox.Contains("NO VOLUME") && flatBox.Contains("Closed trades: 0"), "status box explains zero trades on data without volume");
		Check(RunStatusOnly(bars, false).Contains("Bars processed: " + n), "status box drawn on the last historical bar with no order on it");

		Console.WriteLine("Status box: " + lastBox);

		Console.WriteLine(failures == 0 ? "ALL C# CHECKS PASSED" : failures + " C# CHECK(S) FAILED");
		return failures == 0 ? 0 : 1;
	}
}
