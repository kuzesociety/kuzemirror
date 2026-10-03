// Checks of the Prop Account Manager router (ninjatrader/AddOns/RutaPropRouter.cs) against the simulated broker:
// rotation, firm rules, the live order flow (entry -> stop/target -> exit), rejections, disconnects, timeouts,
// FLATTEN ALL, mode switching and save / load.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.AddOns;

public static class RouterTests
{
	private sealed class Env
	{
		public FakeBroker B;
		public RutaPropRouter R;
		public DateTime Now = new DateTime(2026, 10, 1, 9, 30, 0);
		public readonly Instrument I = new Instrument();
		public void Wait(double seconds) { Now = Now.AddSeconds(seconds); R.Poll(); }
		public void Price(double p) { B.SetPrice(p); R.Poll(); }
		public RutaPropRow Row(string account) { return R.Rows().First(r => r.Account == account); }
		public bool LogHas(string text) { int v; return R.LogLines(out v).Any(l => l.Contains(text)); }
		public void Settings(Action<RutaPropSettings> change) { RutaPropSettings s = R.GetSettings(); change(s); R.SetSettings(s); }
	}

	private static Env Make(string config, RutaPropMode mode, string plan, params string[] accounts)
	{
		Env e = new Env();
		e.B = new FakeBroker(accounts);
		if (config != null && File.Exists(config))
			File.Delete(config);
		e.R = new RutaPropRouter(e.B, config, false);
		Env captured = e;
		e.R.Clock = () => captured.Now;
		foreach (string a in accounts)
			e.R.AddSlot(a, plan);
		e.R.SessionStarted(new DateTime(2026, 9, 30, 18, 5, 0));
		if (mode == RutaPropMode.Live)
			e.R.SetMode(RutaPropMode.Live);
		return e;
	}

	// Dry run: enter and exit at the given prices (MNQ: $2 per point)
	private static void DryTrade(Env e, int dir, int qty, double sl, double tp, double entry, double exit)
	{
		e.R.Enter("s", e.I, dir, qty, sl, tp, entry, 2);
		e.R.Exit("s", "exit", exit, true);
	}

	private static void NextDay(Env e)
	{
		e.Now = e.Now.AddDays(1);
		e.R.SessionStarted(new DateTime(e.Now.Year, e.Now.Month, e.Now.Day, 18, 5, 0).AddDays(-1));
	}

	public static void Run(Action<bool, string> check, string tempDir)
	{
		DryRun(check);
		LiveFlow(check);
		LiveFailures(check);
		ModesAndPersistence(check, tempDir);
		check(!RutaPropRouter.OpenWindow(true), "router: opening the window without RutaPropManagerWindow.cs installed returns false instead of failing");
	}

	private static void DryRun(Action<bool, string> check)
	{
		// rotation: every trade goes to the next account
		Env e = Make(null, RutaPropMode.DryRun, "Topstep50K", "A", "B", "C");
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);		// A +500
		DryTrade(e, -1, 5, 100, 50, 20000, 20100);		// B -1000
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);		// C +500
		check(e.Row("A").Pnl == 500 && e.Row("B").Pnl == -1000 && e.Row("C").Pnl == 500 && e.Row("A").Trades == 1, "router dry run: every trade rotates to the next account, P&L booked per account");
		check(e.B.Orders.Count == 0, "router dry run: no order reaches the broker");
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);		// A 1000
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);		// B -500
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);		// C 1000
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);		// A 1500 -> daily goal
		check(e.Row("A").Status == RutaPropRouter.DoneToday && e.Row("A").Note == "daily goal", "router: daily goal 1500 (tolerance 100) ends the account's day");
		for (int k = 0; k < 4; k++)
			DryTrade(e, 1, 5, 100, 50, 20000, 20050);
		// trades 8-11: B, C (C reaches the goal on it), B, B
		check(e.Row("A").Trades == 3 && e.Row("B").Trades == 5 && e.Row("C").Trades == 3 && e.Row("C").Status == RutaPropRouter.DoneToday,
			"router: an account that is done for the day is skipped");
		NextDay(e);
		RutaPropRow a = e.Row("A");
		check(a.Status == RutaPropRouter.Ready && a.DayPnl == 0 && a.BestDay == 1500 && a.PeakEod == 1500 && a.Floor == -500 && a.Days == 1,
			"router: new session -> READY, best day and EOD peak updated, max loss line trails to -500");

		// one account: goal, consistency, target
		e = Make(null, RutaPropMode.DryRun, "Topstep50K", "X");
		for (int k = 0; k < 4; k++)
			DryTrade(e, 1, 5, 100, 50, 20000, 20050);
		check(e.Row("X").Pnl == 1500 && e.LogHas("skipped: no account available (X done today)"), "router: after the goal the next signal is skipped, with the reason");
		NextDay(e);
		for (int k = 0; k < 3; k++)
			DryTrade(e, 1, 5, 100, 50, 20000, 20050);
		check(e.Row("X").Status == RutaPropRouter.TargetReached && e.Row("X").Days == 2, "router: $3,000 with best day $1,500 passes Topstep 50K in 2 days");
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);
		check(e.Row("X").Trades == 6, "router: a passed account takes no more trades");

		e = Make(null, RutaPropMode.DryRun, "Topstep50K", "X");
		DryTrade(e, 1, 5, 100, 200, 20000, 20200);		// +2000 in one day
		NextDay(e);
		DryTrade(e, 1, 5, 100, 100, 20000, 20100);		// +1000
		check(e.Row("X").Pnl == 3000 && e.Row("X").Status == RutaPropRouter.Ready && e.Row("X").Target == 4000,
			"router: consistency 50% - best day $2,000 means $4,000 is needed");

		// max loss line
		e = Make(null, RutaPropMode.DryRun, "Topstep50K", "Y");
		DryTrade(e, 1, 5, 100, 100, 20000, 20100);		// +1000
		NextDay(e);
		DryTrade(e, 1, 5, 100, 100, 20000, 19900);		// -1000 -> 0
		DryTrade(e, 1, 5, 100, 100, 20000, 19900);		// -1000 -> -1000 = line
		check(e.Row("Y").Status == RutaPropRouter.MaxLossHit && e.Row("Y").Floor == -1000, "router: EOD trailing max loss line (peak 1000 - 2000) fails the account at -1000");

		// firm daily loss limit
		e = Make(null, RutaPropMode.DryRun, "Topstep50K-DLL", "Z");
		DryTrade(e, 1, 5, 150, 100, 20000, 20100);		// risk 1500 > DLL 1000
		check(e.Row("Z").Trades == 0 && e.LogHas("can't fit the stop"), "router: a $1,500 stop never goes to an account with a $1,000 daily loss limit");
		DryTrade(e, 1, 5, 50, 100, 20000, 19950);		// -500
		DryTrade(e, 1, 5, 50, 100, 20000, 19950);		// -1000
		check(e.Row("Z").Status == RutaPropRouter.DoneToday && e.Row("Z").Note == "firm daily loss limit", "router: hitting the firm's daily loss limit ends the day");

		// until day done
		e = Make(null, RutaPropMode.DryRun, "Topstep50K", "A", "B", "C");
		e.Settings(s => s.Rotation = RutaPropRotation.UntilDayDone);
		for (int k = 0; k < 5; k++)
			DryTrade(e, 1, 5, 100, 50, 20000, 20050);
		check(e.Row("A").Trades == 3 && e.Row("B").Trades == 2 && e.Row("C").Trades == 0, "router: UntilDayDone stays on one account until its day is done");

		// paused / size cap / bad input
		e = Make(null, RutaPropMode.DryRun, "Topstep50K", "A");
		e.Settings(s => s.Paused = true);
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);
		check(e.Row("A").Trades == 0 && e.LogHas("routing is PAUSED"), "router: paused -> signals are skipped");
		e.Settings(s => { s.Paused = false; s.MaxContracts = 3; });
		DryTrade(e, 1, 10, 100, 50, 20000, 20050);
		check(e.Row("A").Pnl == 300 && e.LogHas("capped to Max contracts = 3"), "router: Max contracts caps the size");
		e.R.Enter("s", e.I, 1, 5, 0, 50, 20000, 2);
		check(e.R.Rows()[0].OpenTrade == "" && e.LogHas("signal ignored"), "router: a trade without a stop is never sent");

		// session dedupe: another chart reports the same session a few minutes later
		e = Make(null, RutaPropMode.DryRun, "Topstep50K", "A");
		DryTrade(e, 1, 5, 100, 50, 20000, 20050);
		e.R.SessionStarted(new DateTime(2026, 9, 30, 18, 1, 0));
		e.R.SessionStarted(new DateTime(2026, 9, 30, 18, 9, 0));
		check(e.Row("A").DayPnl == 500, "router: the same session reported by other charts does not reset the day");
	}

	private static void LiveFlow(Action<bool, string> check)
	{
		// entry -> fill -> OCO at the fill +- the distances -> target
		Env e = Make(null, RutaPropMode.Live, "Topstep50K", "A", "B");
		e.R.Enter("s", e.I, 1, 5, 100, 50, 20000, 2);
		FakeOrder entry = e.B.Orders.Single();
		check(entry.Account == "A" && entry.Kind == "MKT" && entry.Buy && entry.Quantity == 5 && e.R.Rows()[0].OpenTrade.Contains(RutaPropRouter.EntrySent),
			"router live: a long signal sends a market BUY 5 to the first account");
		e.Price(20001.25);
		List<FakeOrder> oco = e.B.Working("A");
		check(oco.Count == 2 && oco.Any(o => o.Kind == "STP" && !o.Buy && o.StopPrice == 19901.25) && oco.Any(o => o.Kind == "LMT" && !o.Buy && o.LimitPrice == 20051.25),
			"router live: stop and target are placed at the actual fill -100 / +50 points");
		e.Price(20030);
		check(e.Row("A").Status == "IN TRADE" && e.Row("A").Pnl == 0, "router live: nothing is booked while the trade is open");
		e.Price(20052);
		check(e.Row("A").Pnl == 500 && e.Row("A").Status == RutaPropRouter.Ready && e.B.Pos["A"] == 0 && e.B.Working("A").Count == 0,
			"router live: target fill books +$500 and the stop is cancelled (OCO)");
		e.R.Exit("s", "Take Profit", 20051.25, true);
		check(e.B.Orders.Count == 3, "router live: the strategy's own target exit afterwards sends nothing more");

		// short -> stop
		e.R.Enter("s", e.I, -1, 4, 100, 50, 20050, 2);
		check(e.B.Orders.Last().Account == "B" && !e.B.Orders.Last().Buy, "router live: the next trade goes to the next account");
		e.Price(20050);
		e.Price(20160);		// gaps over the stop at 20150
		check(e.Row("B").Pnl == -880 && e.B.Pos["B"] == 0, "router live: stop fill (with slippage) books the real result, -$880");

		// signal exit: cancel stop/target, then close at market
		e.R.Enter("s", e.I, 1, 2, 100, 50, 20160, 2);
		e.Price(20160);
		e.R.Exit("s", "Signal exit", 20170, false);
		check(e.B.Working("A").Count(o => o.Kind != "MKT") == 0 && e.B.Working("A").Count(o => o.Kind == "MKT" && !o.Buy && o.Quantity == 2) == 1,
			"router live: a signal exit cancels the stop/target and sends a market SELL 2");
		e.Price(20170);
		check(e.Row("A").Pnl == 540 && e.B.Pos["A"] == 0, "router live: the exit fill is booked (+$40)");

		// strategy stop/target exit that the account did not fill: wait 3 s, then close
		e.R.Enter("s", e.I, 1, 1, 100, 50, 20170, 2);
		e.Price(20170);
		e.R.Exit("s", "Take Profit", 20220, true);
		e.Wait(2);
		check(e.B.Working("B").Count == 2, "router live: after a stop/target exit the account's own orders get a grace period");
		e.Wait(1.5);
		e.Price(20219);
		check(e.Row("B").Pnl == -782 && e.B.Pos["B"] == 0 && e.B.Working("B").Count == 0, "router live: ...then the rest is closed at market");

		// reversal on the same account (UntilDayDone): the new entry waits until the old trade is booked
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A", "B");
		e.Settings(s => s.Rotation = RutaPropRotation.UntilDayDone);
		e.R.Enter("s", e.I, 1, 3, 100, 50, 20000, 2);
		e.Price(20000);
		e.R.Exit("s", "Short", 20010, false);
		e.R.Enter("s", e.I, -1, 3, 100, 50, 20010, 2);
		check(e.B.Working("A").Count(o => o.Kind == "MKT") == 1 && e.B.Working("A").Single(o => o.Kind == "MKT").Closing,
			"router live: reversal - only the close is sent first (no new entry while the account is in a trade)");
		e.Price(20010);
		e.Price(20010);
		check(e.Row("A").Pnl == 60 && e.B.Pos["A"] == -3 && e.B.Working("A").Count == 2 && e.Row("A").Status == "IN TRADE",
			"router live: ...after it is booked the short goes to the same account and gets its stop/target");

		// exit before the entry fills: no stop/target, closed after the fill
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A");
		e.R.Enter("s", e.I, 1, 2, 100, 50, 20000, 2);
		e.R.Exit("s", "End time", 20000, false);
		e.Price(20000);
		e.Price(19990);
		check(e.B.Orders.All(o => o.Kind == "MKT") && e.B.Pos["A"] == 0 && e.Row("A").Pnl == -40, "router live: exit before the entry filled -> closed at market, no stop/target placed");
	}

	private static void LiveFailures(Action<bool, string> check)
	{
		// rejected entry
		Env e = Make(null, RutaPropMode.Live, "Topstep50K", "A", "B");
		e.B.RejectNextMarket = true;
		e.R.Enter("s", e.I, 1, 2, 100, 50, 20000, 2);
		e.Wait(0.3);
		check(e.R.Rows().All(r => r.OpenTrade == "") && e.Row("A").Trades == 0 && e.Row("A").Days == 0 && e.LogHas("entry rejected"),
			"router live: a rejected entry leaves no trade and no trading day");

		// disconnected account / position not opened by the manager
		e.B.Connected.Remove("A");
		e.R.Enter("s", e.I, 1, 2, 100, 50, 20000, 2);
		check(e.B.Orders.Last().Account == "B", "router live: a disconnected account is skipped");
		e.Price(20000);
		e.R.Exit("s", "x", 20000, false);
		e.Price(20000);
		e.B.Foreign["B"] = 1;
		e.R.Enter("s", e.I, 1, 2, 100, 50, 20000, 2);
		check(e.B.Orders.Count == 5 && e.LogHas("A not connected, B has an open position"), "router live: an account with another position is skipped; none left -> no order");
		check(e.Row("B").Status == "OPEN POSITION" && e.Row("A").Status == "NOT CONNECTED", "router window: shows NOT CONNECTED / OPEN POSITION");

		// stop/target rejected -> close at market
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A");
		e.B.RejectNextOco = true;
		e.R.Enter("s", e.I, 1, 2, 100, 50, 20000, 2);
		e.Price(20000);
		e.Wait(0.3);
		e.Price(19995);
		check(e.B.Pos["A"] == 0 && e.Row("A").Pnl == -20 && e.LogHas("stop rejected"), "router live: rejected stop/target -> the position is closed at once");

		// exit order rejected -> NinjaTrader Flatten, P&L from the account's cash
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A");
		e.R.Enter("s", e.I, -1, 2, 100, 50, 20000, 2);
		e.Price(20000);
		e.B.RejectNextMarket = true;
		e.R.Exit("s", "Signal exit", 20010, false);
		e.B.Price = 20010;
		e.Wait(0.3);
		check(e.B.FlattenCalls == 1 && e.B.Pos["A"] == 0, "router live: rejected exit order -> Flatten");
		e.Wait(1.1);
		check(e.Row("A").Pnl == -40 && e.Row("A").Note.Contains("estimated") && e.R.Rows()[0].OpenTrade == "", "router live: after Flatten the result comes from the account's cash change");

		// entry never fills -> cancelled after 20 s
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A");
		e.B.HoldMarket = true;
		e.R.Enter("s", e.I, 1, 2, 100, 50, 20000, 2);
		e.Wait(10);
		check(e.B.Orders[0].State == RutaPropOrderState.Working, "router live: a slow entry is not cancelled before 20 s");
		e.Wait(11);
		e.Wait(0.3);
		check(e.B.Orders[0].State == RutaPropOrderState.Cancelled && e.R.Rows()[0].OpenTrade == "", "router live: an entry still unfilled after 20 s is cancelled");

		// partial fill then cancel: protect only what filled
		e.B.HoldMarket = true;
		e.R.Enter("s", e.I, 1, 4, 100, 50, 20000, 2);
		FakeOrder part = e.B.Orders.Last();
		e.B.FillPartially(part, 3, 20000);
		e.Wait(21);
		e.Wait(0.3);
		check(e.B.Working("A").Count == 2 && e.B.Working("A").All(o => o.Quantity == 3), "router live: partly filled entry -> stop/target for the 3 contracts filled");
		e.B.HoldMarket = false;
		e.Price(20050);
		check(e.Row("A").Pnl == 300 && e.B.Pos["A"] == 0, "router live: ...booked on those 3 contracts");

		// the broker doesn't cancel the other OCO order: the router does after 5 s
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A");
		e.B.NoOcoCancel = true;
		e.R.Enter("s", e.I, 1, 1, 100, 50, 20000, 2);
		e.Price(20000);
		e.Price(20050);
		e.Wait(6);
		e.Wait(0.3);
		check(e.B.Working("A").Count == 0 && e.Row("A").Pnl == 100, "router live: a leftover OCO order is cancelled, then the trade is booked");

		// cancels ignored -> Flatten after 10 s
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A");
		e.B.IgnoreCancels = true;
		e.R.Enter("s", e.I, 1, 1, 100, 50, 20000, 2);
		e.Price(20000);
		e.R.Exit("s", "Signal exit", 20000, false);
		e.Wait(5);
		check(e.B.FlattenCalls == 0, "router live: waits for the cancel confirmation");
		e.Wait(6);
		e.Wait(1.1);
		check(e.B.FlattenCalls == 1 && e.B.Pos["A"] == 0 && e.R.Rows()[0].OpenTrade == "", "router live: stop/target not cancelled after 10 s -> Flatten");

		// Flatten that doesn't work -> alarm, pause, CHECK ACCOUNT
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A", "B");
		e.B.IgnoreCancels = true;
		e.B.FlattenDoesNothing = true;
		e.R.Enter("s", e.I, 1, 1, 100, 50, 20000, 2);
		e.Price(20000);
		e.R.Exit("s", "Signal exit", 20000, false);
		e.Wait(11);
		e.Wait(31);
		check(e.R.GetSettings().Paused && e.Row("A").Note.Contains("could not flatten") && e.LogHas("STILL OPEN"), "router live: a position that won't close pauses routing and flags the account");
		e.B.FlattenDoesNothing = false;
		e.R.FlattenAll();
		e.Wait(1.1);
		e.Wait(0.1);
		e.R.ClearStatus(0);
		check(e.B.Pos["A"] == 0 && e.R.Rows()[0].OpenTrade == "" && e.Row("A").Status == RutaPropRouter.Ready, "router live: FLATTEN ALL pressed again forces NinjaTrader's Flatten; Ready clears the flag");

		// FLATTEN ALL with two open trades
		e = Make(null, RutaPropMode.Live, "Topstep50K", "A", "B");
		e.R.Enter("s1", e.I, 1, 1, 100, 50, 20000, 2);
		e.R.Enter("s2", e.I, -1, 1, 100, 50, 20000, 2);
		e.Price(20000);
		e.R.FlattenAll();
		e.Price(20005);
		check(e.B.Pos["A"] == 0 && e.B.Pos["B"] == 0 && e.R.Rows().All(r => r.OpenTrade == "") && e.R.GetSettings().Paused && e.Row("A").Pnl == 10 && e.Row("B").Pnl == -10,
			"router: FLATTEN ALL closes every trade, books it and pauses");
		e.R.Enter("s1", e.I, 1, 1, 100, 50, 20000, 2);
		check(e.B.Orders.Count(o => o.Kind == "MKT") == 4, "router: no new trades after FLATTEN ALL until resumed");
	}

	private static void ModesAndPersistence(Action<bool, string> check, string tempDir)
	{
		string config = Path.Combine(tempDir, "PropAccountManager.xml");
		Env e = Make(config, RutaPropMode.DryRun, "Topstep50K");
		check(e.R.SetMode(RutaPropMode.Live) != null, "router: LIVE refused without an enabled account");
		check(e.R.AddSlot("A", "LucidFlex50K") == null && e.R.AddSlot("a", "Topstep50K") != null, "router: the same account can't be added twice");
		e.R.AddSlot("B", "Custom");
		e.R.AddSlot("", "LucidPro50K");
		e.R.SetSlot(2, false, "", "LucidPro50K-DLL");
		e.Settings(s => { s.DailyGoal = 1200; s.GoalTolerance = 50; s.DailyMaxLoss = 900; s.Rotation = RutaPropRotation.UntilDayDone; s.CustomTarget = 2500;
			s.CustomDrawdown = RutaPropDrawdown.Static; s.CustomConsistencyPct = 30; s.CommissionPerContract = 1.5; s.MaxContracts = 7; s.BlockOverAccountLoss = true; });
		DryTrade(e, 1, 2, 100, 50, 20000, 20050);		// dry: A +200 - 3 commission
		check(e.Row("A").Pnl == 197, "router: commission per contract is subtracted");
		check(e.R.SetMode(RutaPropMode.Live) == null && e.Row("A").Pnl == 0 && e.Row("A").Trades == 0, "router: LIVE and DRY RUN keep separate numbers");
		e.R.EditBook(1, 1234.5, 800, 600, 3);
		e.B.Connected.Add("A");
		e.B.Connected.Add("B");
		e.R.Enter("s", e.I, 1, 2, 100, 50, 20000, 2);
		e.Price(20000);
		check(e.R.SetMode(RutaPropMode.DryRun) != null, "router: can't go back to DRY RUN with a live trade open");
		check(e.R.RemoveSlot(0) != null && e.R.NewEvaluation(0) != null, "router: a row with an open trade can't be removed or reset");
		e.R.Save();

		RutaPropRouter r2 = new RutaPropRouter(e.B, config, false);
		RutaPropSettings s2 = r2.GetSettings();
		List<RutaPropRow> rows = r2.Rows();
		check(r2.Mode == RutaPropMode.DryRun, "router: after a restart it is always DRY RUN");
		check(s2.DailyGoal == 1200 && s2.GoalTolerance == 50 && s2.DailyMaxLoss == 900 && s2.Rotation == RutaPropRotation.UntilDayDone && s2.CustomTarget == 2500
			&& s2.CustomDrawdown == RutaPropDrawdown.Static && s2.CustomConsistencyPct == 30 && s2.CommissionPerContract == 1.5 && s2.MaxContracts == 7 && s2.BlockOverAccountLoss,
			"router: settings are saved and loaded");
		check(rows.Count == 3 && rows[0].Account == "A" && rows[0].PlanKey == "LucidFlex50K" && rows[1].PlanKey == "Custom" && !rows[2].Enabled && rows[2].PlanKey == "LucidPro50K-DLL",
			"router: account rows are saved and loaded");
		check(rows[0].Pnl == 197, "router: dry-run numbers are saved");
		r2.SetMode(RutaPropMode.Live);
		rows = r2.Rows();
		check(rows[1].Pnl == 1234.5 && rows[1].PeakEod == 800 && rows[1].BestDay == 600 && rows[1].Days == 3, "router: live numbers (edited) are saved");
		check(rows[0].Status == RutaPropRouter.CheckAccount && rows[0].Note.Contains("was open when NinjaTrader closed"), "router: a trade open at shutdown flags the account");
		check(rows[1].Floor == -2000 && rows[1].Target == 2500, "router: Custom plan uses the saved custom rules (static $2,000 line, $2,500 target)");
		File.Delete(config);
		File.WriteAllText(config, "<not xml");
		RutaPropRouter r3 = new RutaPropRouter(e.B, config, false);
		int v;
		check(r3.Rows().Count == 0 && r3.LogLines(out v).Any(l => l.Contains("could not read")), "router: a damaged settings file is reported, not fatal");
	}
}
