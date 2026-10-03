// Prop Account Manager - the order router (no window in this file; the window is RutaPropManagerWindow.cs).
//
// RutaCryptoMirror strategies with "Route to Prop Account Manager" = on do not trade their own account. They hand
// every LIVE trade to this router, which:
//   - picks ONE of your prop-firm evaluation accounts connected in NinjaTrader (rotation: every trade / until the day is done),
//   - sends a market order there and, once it fills, a real stop + target (OCO) at the strategy's distances from that fill,
//   - closes the trade at market when the strategy exits (signal exit, end time, reversal),
//   - books the result and applies the firm's rules (max loss line, profit target, consistency, daily loss limit)
//     plus your daily goal / daily max loss, per account.
// It starts in DRY RUN every time NinjaTrader starts: nothing is sent to any account until you press "Go LIVE".
namespace NinjaTrader.NinjaScript.AddOns
{
	// usings inside the namespace, so a same-named type from another add-on can't hide these
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Xml.Linq;
	using NinjaTrader.Cbi;

	public enum RutaPropMode { DryRun, Live }
	public enum RutaPropRotation { EveryTrade, UntilDayDone }
	public enum RutaPropDrawdown { EodTrailing, IntradayTrailing, Static }
	public enum RutaPropOrderState { Working, Filled, Cancelled, Rejected }

	/// <summary>What the router needs from NinjaTrader's accounts. RutaPropNtBroker is the real one; the checks use a simulated broker.</summary>
	public interface IRutaPropBroker
	{
		List<string> AccountNames();
		bool IsConnected(string account);
		double CashValue(string account);											// NaN when the connection doesn't report it
		int Position(string account, Instrument instrument);						// signed contracts: + long, - short
		int OpenPositions(string account);											// instruments with an open position
		object SubmitMarket(string account, Instrument instrument, bool buy, bool closing, int quantity, string name);
		object[] SubmitOco(string account, Instrument instrument, bool closeLong, int quantity, double stopPrice, double targetPrice, string oco);
		RutaPropOrderState GetState(object order, out int filled, out double averagePrice);
		void Cancel(string account, object order);
		void Flatten(string account, Instrument instrument);
		double RoundToTick(Instrument instrument, double price);
	}

	/// <summary>Evaluation rules of one firm / plan. Same numbers as the strategy's simulator - verify them against the firm's current rules.</summary>
	public sealed class RutaPropPlan
	{
		public static readonly string[] Keys = { "Topstep50K", "Topstep50K-DLL", "Topstep100K", "Topstep150K", "LucidFlex50K", "LucidPro50K", "LucidPro50K-DLL", "Custom" };

		public string Key, Name;
		public double Target, MaxLoss, DailyLossLimit, ConsistencyPct;	// DailyLossLimit 0 = none, ConsistencyPct 0 = no rule
		public RutaPropDrawdown Drawdown;

		private RutaPropPlan(string key, string name, double target, double maxLoss, RutaPropDrawdown drawdown, double dailyLossLimit, double consistencyPct)
		{
			Key = key;
			Name = name;
			Target = target;
			MaxLoss = maxLoss;
			Drawdown = drawdown;
			DailyLossLimit = dailyLossLimit;
			ConsistencyPct = consistencyPct;
		}

		public static RutaPropPlan Get(string key, RutaPropSettings st)
		{
			switch ((key ?? "").Trim().Replace(" ", "").ToLowerInvariant())
			{
				case "topstep50k":		return new RutaPropPlan("Topstep50K", "Topstep 50K", 3000, 2000, RutaPropDrawdown.EodTrailing, 0, 50);
				case "topstep50k-dll":	return new RutaPropPlan("Topstep50K-DLL", "Topstep 50K+DLL", 3000, 2000, RutaPropDrawdown.EodTrailing, 1000, 50);
				case "topstep100k":		return new RutaPropPlan("Topstep100K", "Topstep 100K", 6000, 3000, RutaPropDrawdown.EodTrailing, 0, 50);
				case "topstep150k":		return new RutaPropPlan("Topstep150K", "Topstep 150K", 9000, 4500, RutaPropDrawdown.EodTrailing, 0, 50);
				case "lucidflex50k":	return new RutaPropPlan("LucidFlex50K", "Lucid Flex 50K", 3000, 2000, RutaPropDrawdown.EodTrailing, 0, 50);
				case "lucidpro50k":		return new RutaPropPlan("LucidPro50K", "Lucid Pro 50K", 3000, 2000, RutaPropDrawdown.EodTrailing, 0, 0);
				case "lucidpro50k-dll":	return new RutaPropPlan("LucidPro50K-DLL", "Lucid Pro 50K+DLL", 3000, 2000, RutaPropDrawdown.EodTrailing, 1200, 0);
				case "custom":			return new RutaPropPlan("Custom", "Custom", st.CustomTarget, st.CustomMaxLoss, st.CustomDrawdown, st.CustomDailyLossLimit, st.CustomConsistencyPct);
				default:				return null;
			}
		}
	}

	public sealed class RutaPropSettings
	{
		public RutaPropRotation Rotation = RutaPropRotation.EveryTrade;
		public double DailyGoal = 1500, GoalTolerance = 100, DailyMaxLoss;		// 0 = off
		public bool BlockOverDailyLoss = true, BlockOverAccountLoss;
		public int MaxContracts = 50;
		public double CommissionPerContract;									// round trip, $ per contract
		public bool Paused;
		public double CustomTarget = 3000, CustomMaxLoss = 2000, CustomDailyLossLimit, CustomConsistencyPct = 50;
		public RutaPropDrawdown CustomDrawdown = RutaPropDrawdown.EodTrailing;

		public RutaPropSettings Clone() { return (RutaPropSettings)MemberwiseClone(); }
	}

	/// <summary>Progress of one evaluation. Money is relative to the starting balance.</summary>
	public sealed class RutaPropBook
	{
		public double Pnl, PeakEod, PeakIntraday, BestDay, DayPnl;
		public int TradingDays, Trades, Wins;
		public long LastTradeSession;		// session number of the last day with a trade
		public string Status = RutaPropRouter.Ready;
		public string Note = "";

		public void Clear()
		{
			Pnl = PeakEod = PeakIntraday = BestDay = DayPnl = 0;
			TradingDays = Trades = Wins = 0;
			LastTradeSession = 0;
			Status = RutaPropRouter.Ready;
			Note = "";
		}
	}

	/// <summary>One row of the manager: a NinjaTrader account, its plan, and separate live / dry-run progress.</summary>
	public sealed class RutaPropSlot
	{
		public bool Enabled = true;
		public string Account = "";
		public string PlanKey = "Topstep50K";
		public readonly RutaPropBook Live = new RutaPropBook();
		public readonly RutaPropBook Dry = new RutaPropBook();
		public RutaPropTrade Trade;		// the account's open trade (one at a time)
	}

	public sealed class RutaPropTrade
	{
		public int Id, Direction, Quantity, EntryFilled;
		public string Source, Phase, ExitReason;
		public Instrument Instrument;
		public bool Live, ExitRequested, EntryCancelSent, Alarmed;
		public RutaPropSlot Slot;
		public double StopDistance, TargetDistance, PointValue, RefPrice;
		public double EntryPrice = double.NaN, StopPrice = double.NaN, TargetPrice = double.NaN, ExitRefPrice = double.NaN, CashAtEntry = double.NaN;
		public DateTime Created, PhaseSince, ExitDeadline;
		public DateTime ExitFilledSeen = DateTime.MinValue, FlatSince = DateTime.MinValue;
		public object EntryOrder, StopOrder, TargetOrder, CloseOrder;

		public string Side { get { return Direction > 0 ? "Long" : "Short"; } }
	}

	/// <summary>Snapshot of one account row for the window.</summary>
	public sealed class RutaPropRow
	{
		public int Index, Days, Trades, Wins, Positions;
		public bool Enabled, Connected, IsNext;
		public string Account, PlanKey, PlanName, Status, Note, OpenTrade;
		public double Pnl, DayPnl, BestDay, PeakEod, Floor, Room, Target, ToTarget, Cash;
	}

	public sealed class RutaPropRouter
	{
		// Account status
		public const string Ready = "READY", DoneToday = "DONE TODAY", TargetReached = "TARGET REACHED", MaxLossHit = "MAX LOSS HIT", CheckAccount = "CHECK ACCOUNT";
		// Trade phases
		public const string Queued = "QUEUED", EntrySent = "ENTRY SENT", Protected = "PROTECTED", Closing = "CLOSING", ExitSent = "EXIT SENT",
			FlattenSent = "FLATTEN SENT", DryOpen = "OPEN (dry run)";
		// Timeouts in seconds
		public const double BrokerExitGrace = 3, QueueTimeout = 20, EntryTimeout = 20, OcoSiblingTimeout = 5, ClosingTimeout = 10,
			ExitTimeout = 15, FlattenAlarm = 30, FlatConfirm = 1;

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static readonly object instanceLock = new object();
		private static RutaPropRouter instance;

		/// <summary>The one router every strategy and the window use.</summary>
		public static RutaPropRouter Instance
		{
			get
			{
				lock (instanceLock)
				{
					if (instance == null)
						instance = new RutaPropRouter(new RutaPropNtBroker(), DefaultConfigPath(), true);
					return instance;
				}
			}
		}

		public static void UseForTesting(RutaPropRouter router)
		{
			lock (instanceLock)
				instance = router;
		}

		public static string DefaultConfigPath()
		{
			return System.IO.Path.Combine(System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "RutaCryptoMirror"), "PropAccountManager.xml");
		}

		/// <summary>
		/// Opens the Prop Account Manager window (RutaPropManagerWindow.cs). Looked up at run time, so this file and the
		/// strategy don't need the window code to compile. Returns false when the window file isn't installed.
		/// </summary>
		public static bool OpenWindow(bool bringToFront)
		{
			try
			{
				Type t = typeof(RutaPropRouter).Assembly.GetType("NinjaTrader.NinjaScript.AddOns.RutaPropManagerWindow");
				System.Reflection.MethodInfo m = t != null ? t.GetMethod("OpenOrActivate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : null;
				if (m == null)
					return false;
				m.Invoke(null, new object[] { bringToFront });
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static DateTime SystemClock() { return DateTime.Now; }

		private readonly object sync = new object();
		private readonly IRutaPropBroker broker;
		private readonly string configPath, logPath;
		private readonly System.Threading.Timer timer;
		private int pollBusy, linesSinceSizeCheck;
		public Func<DateTime> Clock = SystemClock;

		private RutaPropMode mode = RutaPropMode.DryRun;		// never saved: every start is a dry run
		private RutaPropSettings settings = new RutaPropSettings();
		private readonly List<RutaPropSlot> slots = new List<RutaPropSlot>();
		private readonly List<RutaPropTrade> trades = new List<RutaPropTrade>();	// queued, open and closing trades
		private DateTime sessionStart = DateTime.MinValue;
		private long sessionId = 1;
		private int lastAssigned = -1, nextTradeId = 1;
		private double lastRisk = double.NaN;
		private readonly List<string> logLines = new List<string>();
		private int logVersion;

		public RutaPropRouter(IRutaPropBroker broker, string configPath, bool startTimer)
		{
			this.broker = broker;
			this.configPath = configPath;
			if (!string.IsNullOrEmpty(configPath))
			{
				logPath = System.IO.Path.ChangeExtension(configPath, ".log");
				try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(configPath)); }
				catch (Exception) { }
			}
			Load();
			Log("Prop Account Manager started in DRY RUN with " + slots.Count + " account(s)");
			if (startTimer)
				timer = new System.Threading.Timer(OnTimer, null, 250, 250);
		}

		private void OnTimer(object state)
		{
			if (System.Threading.Interlocked.Exchange(ref pollBusy, 1) == 1)
				return;
			try { Poll(); }
			catch (Exception ex) { Log("router error: " + ex.Message); }
			finally { System.Threading.Interlocked.Exchange(ref pollBusy, 0); }
		}

		#region Called by the strategies
		/// <summary>
		/// A strategy saw the first bar of a trading session (its chart's Trading Hours). Charts with other bar sizes report
		/// the same session minutes apart; a start at least 2 hours after the known one is a new day.
		/// </summary>
		public void SessionStarted(DateTime start)
		{
			lock (sync)
			{
				if (sessionStart == DateTime.MinValue)
				{
					sessionStart = start;
					Save();
					return;
				}
				if (start < sessionStart.AddHours(2))
					return;
				foreach (RutaPropSlot s in slots)
				{
					EndOfDay(s.Live);
					EndOfDay(s.Dry);
				}
				sessionStart = start;
				sessionId++;
				Log("New session " + start.ToString("yyyy-MM-dd HH:mm", Inv) + ": today's P&L reset, DONE TODAY accounts are READY again");
				Save();
			}
		}

		/// <summary>A new trade from a strategy (live bars only). Distances are in price points from the fill.</summary>
		public void Enter(string source, Instrument instrument, int direction, int quantity, double stopDistance, double targetDistance, double refPrice, double pointValue)
		{
			lock (sync)
			{
				DateTime now = Clock();
				string side = direction > 0 ? "Long" : "Short";
				RutaPropTrade current = CurrentTrade(source);
				if (current != null)
				{
					Log(source + ": new " + side + " signal while trade #" + current.Id + " is still open - closing it first");
					RequestExit(current, "replaced by a new signal", refPrice, false, now);
				}
				if (direction == 0 || quantity < 1 || instrument == null || !(stopDistance > 0) || !(targetDistance > 0) || !(pointValue > 0))
				{
					Log(source + ": signal ignored (no direction, size, stop or target)");
					return;
				}
				if (settings.Paused)
				{
					Log(source + ": " + side + " signal skipped - routing is PAUSED");
					return;
				}
				int qty = quantity;
				if (qty > settings.MaxContracts)
				{
					Log(source + ": " + quantity + " contracts capped to Max contracts = " + settings.MaxContracts);
					qty = settings.MaxContracts;
				}
				RutaPropTrade t = new RutaPropTrade();
				t.Id = nextTradeId++;
				t.Source = source;
				t.Instrument = instrument;
				t.Direction = direction > 0 ? 1 : -1;
				t.Quantity = qty;
				t.StopDistance = stopDistance;
				t.TargetDistance = targetDistance;
				t.RefPrice = refPrice;
				t.PointValue = pointValue;
				t.Live = mode == RutaPropMode.Live;
				t.Created = now;
				SetPhase(t, Queued, now);
				trades.Add(t);
				Log(Tag(t) + " signal from " + source + " (stop " + Money(stopDistance * qty * pointValue) + ", target " + Money(targetDistance * qty * pointValue) + ")");
				TryDispatch(t, now);
			}
		}

		/// <summary>
		/// The strategy closed its trade. brokerHandles = it was closed by the strategy's stop / target, which the account's
		/// own OCO orders fill: the router gives them a few seconds and only then closes what is left at market.
		/// </summary>
		public void Exit(string source, string reason, double refPrice, bool brokerHandles)
		{
			lock (sync)
			{
				RutaPropTrade t = CurrentTrade(source);
				if (t != null)
					RequestExit(t, reason, refPrice, brokerHandles, Clock());
			}
		}

		/// <summary>One line for the strategy's status box.</summary>
		public string ShortStatus()
		{
			lock (sync)
			{
				int ready = 0, used = 0;
				foreach (RutaPropSlot s in slots)
				{
					if (!s.Enabled || string.IsNullOrEmpty(s.Account))
						continue;
					used++;
					if (Unavailable(s, double.NaN) == null)
						ready++;
				}
				int next = SelectSlot(lastRisk);
				return (mode == RutaPropMode.Live ? "LIVE" : "DRY RUN") + (settings.Paused ? " (PAUSED)" : "") + " | " + ready + "/" + used
					+ " accounts ready | next: " + (next >= 0 ? SlotName(next) : "none") + " | open trades: " + trades.Count;
			}
		}
		#endregion

		#region Called by the window
		public RutaPropMode Mode { get { lock (sync) return mode; } }

		/// <summary>Returns null when done, otherwise why not.</summary>
		public string SetMode(RutaPropMode newMode)
		{
			lock (sync)
			{
				if (newMode == mode)
					return null;
				if (newMode == RutaPropMode.DryRun)
				{
					foreach (RutaPropTrade t in trades)
						if (t.Live)
							return "Live trades are still open. Wait until they are closed (or press FLATTEN ALL) before going back to DRY RUN.";
				}
				else if (!slots.Any(s => s.Enabled && !string.IsNullOrEmpty(s.Account)))
					return "Add at least one enabled account first.";
				foreach (RutaPropTrade t in trades.ToArray())
					if (!t.Live)
						Remove(t);
				mode = newMode;
				Log(newMode == RutaPropMode.Live ? "*** LIVE: new trades are sent to the accounts" : "DRY RUN: nothing is sent to any account");
				return null;
			}
		}

		public RutaPropSettings GetSettings()
		{
			lock (sync)
				return settings.Clone();
		}

		public void SetSettings(RutaPropSettings value)
		{
			lock (sync)
			{
				RutaPropSettings v = value.Clone();
				v.DailyGoal = Math.Max(0, v.DailyGoal);
				v.GoalTolerance = Math.Max(0, v.GoalTolerance);
				v.DailyMaxLoss = Math.Max(0, v.DailyMaxLoss);
				v.MaxContracts = Math.Max(1, v.MaxContracts);
				v.CommissionPerContract = Math.Max(0, v.CommissionPerContract);
				v.CustomTarget = Math.Max(1, v.CustomTarget);
				v.CustomMaxLoss = Math.Max(1, v.CustomMaxLoss);
				v.CustomDailyLossLimit = Math.Max(0, v.CustomDailyLossLimit);
				v.CustomConsistencyPct = Math.Min(100, Math.Max(0, v.CustomConsistencyPct));
				if (v.Paused != settings.Paused)
					Log(v.Paused ? "PAUSED: new signals are skipped (open trades keep their stop / target)" : "Routing resumed");
				settings = v;
				Save();
			}
		}

		public List<string> BrokerAccounts()
		{
			try { return broker.AccountNames(); }
			catch (Exception) { return new List<string>(); }
		}

		public string AddSlot(string account, string planKey)
		{
			lock (sync)
			{
				string error = CheckAccountName(-1, account);
				if (error != null)
					return error;
				RutaPropSlot s = new RutaPropSlot();
				s.Account = (account ?? "").Trim();
				s.PlanKey = RutaPropPlan.Get(planKey, settings) != null ? RutaPropPlan.Get(planKey, settings).Key : "Topstep50K";
				slots.Add(s);
				Log("Added account " + (s.Account.Length > 0 ? s.Account : "(none yet)") + " - " + s.PlanKey);
				Save();
				return null;
			}
		}

		public string RemoveSlot(int index)
		{
			lock (sync)
			{
				if (index < 0 || index >= slots.Count)
					return "No such row.";
				if (slots[index].Trade != null)
					return "This account has an open trade.";
				Log("Removed account " + SlotName(index));
				slots.RemoveAt(index);
				if (lastAssigned >= index)
					lastAssigned--;
				Save();
				return null;
			}
		}

		public string SetSlot(int index, bool enabled, string account, string planKey)
		{
			lock (sync)
			{
				if (index < 0 || index >= slots.Count)
					return "No such row.";
				RutaPropSlot s = slots[index];
				account = (account ?? "").Trim();
				RutaPropPlan plan = RutaPropPlan.Get(planKey, settings);
				if (plan == null)
					return "Unknown plan " + planKey;
				if (account != s.Account)
				{
					if (s.Trade != null)
						return "This account has an open trade.";
					string error = CheckAccountName(index, account);
					if (error != null)
						return error;
				}
				if (enabled != s.Enabled || account != s.Account || plan.Key != s.PlanKey)
				{
					s.Enabled = enabled;
					s.Account = account;
					s.PlanKey = plan.Key;
					Log("Row " + (index + 1) + ": " + (account.Length > 0 ? account : "(no account)") + ", " + plan.Key + (enabled ? "" : ", OFF"));
					Save();
				}
				return null;
			}
		}

		/// <summary>Copies the numbers from the firm's dashboard into the current mode's book, then re-checks the status.</summary>
		public string EditBook(int index, double pnl, double peakEod, double bestDay, int days)
		{
			lock (sync)
			{
				if (index < 0 || index >= slots.Count)
					return "No such row.";
				RutaPropSlot s = slots[index];
				RutaPropBook b = BookOf(s);
				b.Pnl = pnl;
				b.PeakEod = Math.Max(0, peakEod);
				b.PeakIntraday = Math.Max(b.PeakEod, pnl);
				b.BestDay = Math.Max(0, bestDay);
				b.TradingDays = Math.Max(0, days);
				if (b.Status != CheckAccount)
				{
					b.Status = Ready;
					b.Note = "";
					Evaluate(s, b, false);
				}
				Log(SlotName(index) + " edited: P&L " + Money(pnl) + ", EOD peak " + Money(b.PeakEod) + ", best day " + Money(b.BestDay) + ", " + b.TradingDays + " days");
				Save();
				return null;
			}
		}

		/// <summary>Back to READY (after DONE TODAY, CHECK ACCOUNT, ...). The P&L is kept.</summary>
		public string ClearStatus(int index)
		{
			lock (sync)
			{
				if (index < 0 || index >= slots.Count)
					return "No such row.";
				RutaPropBook b = BookOf(slots[index]);
				b.Status = Ready;
				b.Note = "";
				Log(SlotName(index) + " set to READY by you");
				Save();
				return null;
			}
		}

		/// <summary>A new evaluation on this account: all numbers back to zero.</summary>
		public string NewEvaluation(int index)
		{
			lock (sync)
			{
				if (index < 0 || index >= slots.Count)
					return "No such row.";
				if (slots[index].Trade != null)
					return "This account has an open trade.";
				BookOf(slots[index]).Clear();
				Log(SlotName(index) + ": new evaluation (" + (mode == RutaPropMode.Live ? "live" : "dry run") + " numbers reset)");
				Save();
				return null;
			}
		}

		public void ResetDryRun()
		{
			lock (sync)
			{
				foreach (RutaPropTrade t in trades.ToArray())
					if (!t.Live)
						Remove(t);
				foreach (RutaPropSlot s in slots)
					s.Dry.Clear();
				Log("Dry-run numbers reset");
				Save();
			}
		}

		/// <summary>Pauses routing and closes every trade the manager opened. Pressing it again forces NinjaTrader's Flatten.</summary>
		public void FlattenAll()
		{
			lock (sync)
			{
				DateTime now = Clock();
				settings.Paused = true;
				Log("FLATTEN ALL: routing PAUSED, closing " + trades.Count + " trade(s)");
				foreach (RutaPropTrade t in trades.ToArray())
				{
					if (!trades.Contains(t))
						continue;
					if (!t.Live)
						Remove(t);
					else if (!t.ExitRequested)
						RequestExit(t, "FLATTEN ALL", double.NaN, false, now);
					else if (t.Phase != Queued && t.Phase != EntrySent)
						ForceFlatten(t, now, "FLATTEN ALL pressed again");
				}
				Save();
			}
		}

		public List<RutaPropRow> Rows()
		{
			lock (sync)
			{
				List<RutaPropRow> rows = new List<RutaPropRow>();
				int next = SelectSlot(lastRisk);
				bool live = mode == RutaPropMode.Live;
				for (int i = 0; i < slots.Count; i++)
				{
					RutaPropSlot s = slots[i];
					RutaPropBook b = BookOf(s);
					RutaPropPlan p = PlanOf(s);
					RutaPropRow r = new RutaPropRow();
					r.Index = i;
					r.Enabled = s.Enabled;
					r.Account = s.Account;
					r.PlanKey = p.Key;
					r.PlanName = p.Name;
					r.Pnl = b.Pnl;
					r.DayPnl = b.DayPnl;
					r.BestDay = b.BestDay;
					r.PeakEod = b.PeakEod;
					r.Floor = Floor(p, b);
					r.Room = b.Pnl - r.Floor;
					r.Target = Required(p, b);
					r.ToTarget = r.Target - b.Pnl;
					r.Days = b.TradingDays;
					r.Trades = b.Trades;
					r.Wins = b.Wins;
					r.Connected = s.Account.Length > 0 && SafeConnected(s.Account);
					r.Cash = r.Connected ? SafeCash(s.Account) : double.NaN;
					r.Positions = r.Connected ? SafePositions(s.Account) : 0;
					r.Status = b.Status;
					if (s.Trade != null)
						r.Status = "IN TRADE";
					else if (b.Status == Ready)
					{
						if (!s.Enabled)
							r.Status = "OFF";
						else if (s.Account.Length == 0)
							r.Status = "NO ACCOUNT";
						else if (AccountBusy(s))
							r.Status = "ACCOUNT BUSY";
						else if (live && !r.Connected)
							r.Status = "NOT CONNECTED";
						else if (live && r.Positions != 0)
							r.Status = "OPEN POSITION";
						else if (!double.IsNaN(lastRisk) && !Fits(p, b, lastRisk))
							r.Status = "CAN'T FIT";
					}
					r.Note = b.Note ?? "";
					if (live && s.Trade == null && b.Status == Ready && r.Positions != 0 && !AccountBusy(s))
						r.Note = "position not opened by the manager - account skipped until it is flat";
					r.OpenTrade = s.Trade != null ? Describe(s.Trade) : "";
					r.IsNext = i == next;
					rows.Add(r);
				}
				return rows;
			}
		}

		public string[] LogLines(out int version)
		{
			lock (sync)
			{
				version = logVersion;
				return logLines.ToArray();
			}
		}

		public string SessionText()
		{
			lock (sync)
				return sessionStart == DateTime.MinValue ? "no session reported yet (start a strategy)" : "session since " + sessionStart.ToString("ddd yyyy-MM-dd HH:mm", Inv);
		}
		#endregion

		#region Routing
		private RutaPropTrade CurrentTrade(string source)
		{
			foreach (RutaPropTrade t in trades)
				if (t.Source == source && !t.ExitRequested)
					return t;
			return null;
		}

		private RutaPropBook BookOf(RutaPropSlot s) { return mode == RutaPropMode.Live ? s.Live : s.Dry; }

		private RutaPropPlan PlanOf(RutaPropSlot s) { return RutaPropPlan.Get(s.PlanKey, settings) ?? RutaPropPlan.Get("Custom", settings); }

		private string SlotName(int index)
		{
			RutaPropSlot s = slots[index];
			return s.Account.Length > 0 ? s.Account : "row " + (index + 1);
		}

		// NinjaTrader simulation accounts (Sim101, Sim-..., Playback101). Several rows may share one of them, to test
		// the rotation with real simulated orders; a real account can be in the list only once.
		public static bool IsSimulationAccount(string account)
		{
			account = (account ?? "").Trim();
			return account.StartsWith("Sim", StringComparison.OrdinalIgnoreCase) || account.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);
		}

		private string CheckAccountName(int index, string account)
		{
			account = (account ?? "").Trim();
			if (account.Length == 0 || IsSimulationAccount(account))
				return null;
			for (int i = 0; i < slots.Count; i++)
				if (i != index && string.Equals(slots[i].Account, account, StringComparison.OrdinalIgnoreCase))
					return "Account " + account + " is already in the list. (Only simulation accounts such as Sim101 can be used by several rows.)";
			return null;
		}

		// Another row on the same NinjaTrader account has a trade open (rows sharing a simulation account)
		private bool AccountBusy(RutaPropSlot s)
		{
			foreach (RutaPropSlot o in slots)
				if (o != s && o.Trade != null && string.Equals(o.Account, s.Account, StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}

		// Max loss line as P&L: trails the highest end-of-day (or intraday) balance, stops at the starting balance
		public static double Floor(RutaPropPlan p, RutaPropBook b)
		{
			if (p.Drawdown == RutaPropDrawdown.Static)
				return -p.MaxLoss;
			double peak = p.Drawdown == RutaPropDrawdown.EodTrailing ? b.PeakEod : b.PeakIntraday;
			return Math.Min(0, peak - p.MaxLoss);
		}

		// Profit needed to pass: the target, or more if the best day is over the consistency share of the total
		public static double Required(RutaPropPlan p, RutaPropBook b)
		{
			double required = p.Target;
			if (p.ConsistencyPct > 0)
				required = Math.Max(required, Math.Max(b.BestDay, b.DayPnl) / (p.ConsistencyPct / 100.0));
			return required;
		}

		private string DayStopReason(RutaPropPlan p, RutaPropBook b)
		{
			if (p.DailyLossLimit > 0 && b.DayPnl <= -p.DailyLossLimit)
				return "firm daily loss limit";
			if (settings.DailyGoal > 0 && b.DayPnl >= settings.DailyGoal - settings.GoalTolerance)
				return "daily goal";
			if (settings.DailyMaxLoss > 0 && b.DayPnl <= -settings.DailyMaxLoss)
				return "daily max loss";
			return null;
		}

		private bool Fits(RutaPropPlan p, RutaPropBook b, double risk)
		{
			if (settings.BlockOverDailyLoss)
			{
				if (settings.DailyMaxLoss > 0 && b.DayPnl - risk < -settings.DailyMaxLoss)
					return false;
				if (p.DailyLossLimit > 0 && b.DayPnl - risk < -p.DailyLossLimit)
					return false;
			}
			if (settings.BlockOverAccountLoss && b.Pnl - risk < Floor(p, b))
				return false;
			return true;
		}

		// null = the account can take a trade with this risk (NaN = don't check the risk)
		private string Unavailable(RutaPropSlot s, double risk)
		{
			if (!s.Enabled)
				return "off";
			if (s.Account.Length == 0)
				return "no account";
			if (s.Trade != null)
				return "in trade";
			if (AccountBusy(s))
				return "account busy (another row on " + s.Account + " is in a trade)";
			RutaPropBook b = BookOf(s);
			if (b.Status != Ready)
				return b.Status.ToLowerInvariant();
			if (mode == RutaPropMode.Live)
			{
				if (!SafeConnected(s.Account))
					return "not connected";
				if (SafePositions(s.Account) != 0)
					return "has an open position";
			}
			if (!double.IsNaN(risk) && !Fits(PlanOf(s), b, risk))
				return "can't fit the stop";
			return null;
		}

		private int SelectSlot(double risk)
		{
			int n = slots.Count;
			if (n == 0)
				return -1;
			int start = settings.Rotation == RutaPropRotation.UntilDayDone && lastAssigned >= 0 ? lastAssigned : lastAssigned + 1;
			for (int k = 0; k < n; k++)
			{
				int idx = (start + k) % n;
				if (Unavailable(slots[idx], risk) == null)
					return idx;
			}
			return -1;
		}

		private string WhyNone(double risk)
		{
			List<string> parts = new List<string>();
			for (int i = 0; i < slots.Count; i++)
				parts.Add(SlotName(i) + " " + (Unavailable(slots[i], risk) ?? "ok"));
			return parts.Count == 0 ? "no accounts in the list" : string.Join(", ", parts.ToArray());
		}

		private void TryDispatch(RutaPropTrade t, DateTime now)
		{
			foreach (RutaPropTrade o in trades)
			{
				if (o == t || o.Source != t.Source || o.Phase == Queued)
					continue;
				// reversal: the strategy's previous trade is still being closed
				if ((now - t.Created).TotalSeconds > QueueTimeout)
				{
					Log(Tag(t) + " dropped: the previous trade took more than " + QueueTimeout + " s to close");
					Remove(t);
				}
				return;
			}
			double risk = t.StopDistance * t.Quantity * t.PointValue;
			lastRisk = risk;
			int idx = SelectSlot(risk);
			if (idx < 0)
			{
				Log(Tag(t) + " skipped: no account available (" + WhyNone(risk) + ")");
				Remove(t);
				return;
			}
			RutaPropSlot s = slots[idx];
			lastAssigned = idx;
			t.Slot = s;
			s.Trade = t;
			if (!t.Live)
			{
				t.EntryFilled = t.Quantity;
				t.EntryPrice = t.RefPrice;
				t.StopPrice = t.RefPrice - t.Direction * t.StopDistance;
				t.TargetPrice = t.RefPrice + t.Direction * t.TargetDistance;
				SetPhase(t, DryOpen, now);
				CountDay(s.Dry);
				Log(Tag(t) + " -> " + s.Account + " (dry run) @ " + Px(t.RefPrice) + ", stop " + Px(t.StopPrice) + ", target " + Px(t.TargetPrice));
				return;
			}
			t.CashAtEntry = SafeCash(s.Account);
			object order = null;
			string error = null;
			try { order = broker.SubmitMarket(s.Account, t.Instrument, t.Direction > 0, false, t.Quantity, t.Direction > 0 ? "RP Long" : "RP Short"); }
			catch (Exception ex) { error = ex.Message; }
			if (order == null)
			{
				Log("!!! " + Tag(t) + " entry order on " + s.Account + " failed: " + (error ?? "account not found"));
				Remove(t);
				return;
			}
			t.EntryOrder = order;
			SetPhase(t, EntrySent, now);
			Log(Tag(t) + " -> " + s.Account + ": market " + (t.Direction > 0 ? "BUY " : "SELL ") + t.Quantity + " " + t.Instrument.FullName);
		}

		private void CountDay(RutaPropBook b)
		{
			if (b.LastTradeSession != sessionId)
			{
				b.LastTradeSession = sessionId;
				b.TradingDays++;
			}
		}

		private void RequestExit(RutaPropTrade t, string reason, double refPrice, bool brokerHandles, DateTime now)
		{
			if (t.ExitRequested)
				return;
			t.ExitRequested = true;
			t.ExitReason = reason;
			t.ExitRefPrice = refPrice;
			t.ExitDeadline = brokerHandles ? now.AddSeconds(BrokerExitGrace) : now;
			if (t.Phase == Queued)
			{
				Log(Tag(t) + " cancelled before entry (" + reason + ")");
				Remove(t);
			}
			else if (t.Phase == DryOpen)
			{
				bool noPrice = double.IsNaN(refPrice);
				double px = noPrice ? t.EntryPrice : refPrice;
				Book(t, t.EntryFilled * px, t.EntryFilled, reason + (noPrice ? ", no price: booked flat" : ""), false, double.NaN);
			}
			else
				Advance(t, now);
		}

		/// <summary>Moves the live trades along (order fills, stop / target, exits). Runs 4 times a second.</summary>
		public void Poll()
		{
			lock (sync)
			{
				DateTime now = Clock();
				foreach (RutaPropTrade t in trades.ToArray())
				{
					if (!trades.Contains(t))
						continue;
					try { Advance(t, now); }
					catch (Exception ex) { Log("!!! " + Tag(t) + " error: " + ex.Message); }
				}
				foreach (RutaPropTrade t in trades.ToArray())
					if (t.Phase == Queued && trades.Contains(t))
						TryDispatch(t, now);
			}
		}

		private int Fills(object order, out double average, out RutaPropOrderState state)
		{
			average = 0;
			if (order == null)
			{
				state = RutaPropOrderState.Cancelled;
				return 0;
			}
			int filled;
			state = broker.GetState(order, out filled, out average);
			if (filled <= 0 || double.IsNaN(average))
			{
				average = 0;
				return 0;
			}
			return filled;
		}

		private void Advance(RutaPropTrade t, DateTime now)
		{
			if (!t.Live || t.Phase == Queued || t.Phase == DryOpen)
				return;
			string acct = t.Slot.Account;
			double since = (now - t.PhaseSince).TotalSeconds;
			double sa, ta, ca;
			RutaPropOrderState ss, ts, cs;

			if (t.Phase == EntrySent)
			{
				double avg;
				RutaPropOrderState es;
				int f = Fills(t.EntryOrder, out avg, out es);
				if (es == RutaPropOrderState.Working)
				{
					if (since > EntryTimeout && !t.EntryCancelSent)
					{
						t.EntryCancelSent = true;
						Log(Tag(t) + " entry not filled after " + EntryTimeout + " s - cancelling it");
						SafeCancel(acct, t.EntryOrder);
					}
					return;
				}
				if (f <= 0)
				{
					Log("!!! " + Tag(t) + " entry " + es.ToString().ToLowerInvariant() + " on " + acct + " - no trade");
					Remove(t);
					return;
				}
				t.EntryFilled = f;
				t.EntryPrice = avg;
				CountDay(t.Slot.Live);
				if (t.ExitRequested)
				{
					Log(Tag(t) + " filled " + f + " @ " + Px(avg) + " after the strategy already exited - closing");
					SendClose(t, f, now);
					return;
				}
				t.StopPrice = broker.RoundToTick(t.Instrument, avg - t.Direction * t.StopDistance);
				t.TargetPrice = broker.RoundToTick(t.Instrument, avg + t.Direction * t.TargetDistance);
				object[] oco = null;
				string error = null;
				try { oco = broker.SubmitOco(acct, t.Instrument, t.Direction > 0, f, t.StopPrice, t.TargetPrice, "RP" + Guid.NewGuid().ToString("N")); }
				catch (Exception ex) { error = ex.Message; }
				if (oco != null && oco.Length == 2)
				{
					t.StopOrder = oco[0];
					t.TargetOrder = oco[1];
				}
				if (t.StopOrder == null || t.TargetOrder == null)
				{
					Log("!!! " + Tag(t) + " filled @ " + Px(avg) + " but the stop / target could not be placed on " + acct + " (" + (error ?? "no order") + ") - closing at market");
					StartClosing(t, now);
					return;
				}
				SetPhase(t, Protected, now);
				Log(Tag(t) + " filled " + f + " @ " + Px(avg) + " on " + acct + ": stop " + Px(t.StopPrice) + ", target " + Px(t.TargetPrice));
				return;
			}

			if (t.Phase == Protected)
			{
				int sf = Fills(t.StopOrder, out sa, out ss);
				int tf = Fills(t.TargetOrder, out ta, out ts);
				bool working = ss == RutaPropOrderState.Working || ts == RutaPropOrderState.Working;
				if (sf + tf >= t.EntryFilled)
				{
					if (!working)
					{
						BookFromOrders(t, sf > 0 && tf > 0 ? "stop + target" : sf > 0 ? "stop" : "target");
						return;
					}
					// OCO: the other order is being cancelled by the broker; make sure it is
					if (t.ExitFilledSeen == DateTime.MinValue)
						t.ExitFilledSeen = now;
					else if ((now - t.ExitFilledSeen).TotalSeconds > OcoSiblingTimeout)
					{
						Log(Tag(t) + " OCO order still working after the other filled - cancelling it");
						if (ss == RutaPropOrderState.Working) SafeCancel(acct, t.StopOrder);
						if (ts == RutaPropOrderState.Working) SafeCancel(acct, t.TargetOrder);
						t.ExitFilledSeen = now;
					}
					return;
				}
				if (ss != RutaPropOrderState.Working || ts != RutaPropOrderState.Working)
				{
					Log("!!! " + Tag(t) + " " + (ss != RutaPropOrderState.Working ? "stop " + ss : "target " + ts).ToLowerInvariant() + " on " + acct + " - closing at market");
					StartClosing(t, now);
					return;
				}
				if (t.ExitRequested && now >= t.ExitDeadline)
				{
					Log(Tag(t) + " exit (" + t.ExitReason + "): cancelling stop / target, then closing at market");
					StartClosing(t, now);
				}
				return;
			}

			if (t.Phase == Closing)
			{
				int sf = Fills(t.StopOrder, out sa, out ss);
				int tf = Fills(t.TargetOrder, out ta, out ts);
				if (ss == RutaPropOrderState.Working || ts == RutaPropOrderState.Working)
				{
					if (since > ClosingTimeout)
						ForceFlatten(t, now, "stop / target not cancelled after " + ClosingTimeout + " s");
					return;
				}
				int remaining = t.EntryFilled - sf - tf;
				if (remaining > 0)
					SendClose(t, remaining, now);
				else
					BookFromOrders(t, sf > 0 ? "stop" : "target");
				return;
			}

			if (t.Phase == ExitSent)
			{
				int sf = Fills(t.StopOrder, out sa, out ss);
				int tf = Fills(t.TargetOrder, out ta, out ts);
				int cf = Fills(t.CloseOrder, out ca, out cs);
				if (cs == RutaPropOrderState.Working)
				{
					if (since > ExitTimeout)
						ForceFlatten(t, now, "exit order not filled after " + ExitTimeout + " s");
					return;
				}
				if (sf + tf + cf >= t.EntryFilled)
					BookFromOrders(t, t.ExitReason ?? "exit");
				else
					ForceFlatten(t, now, "exit order " + cs.ToString().ToLowerInvariant());
				return;
			}

			if (t.Phase == FlattenSent)
			{
				int pos = SafePosition(acct, t.Instrument);
				if (pos == 0)
				{
					if (t.FlatSince == DateTime.MinValue)
						t.FlatSince = now;
					if ((now - t.FlatSince).TotalSeconds >= FlatConfirm)
						BookEstimated(t);
					return;
				}
				t.FlatSince = DateTime.MinValue;
				if (since > FlattenAlarm && !t.Alarmed)
				{
					t.Alarmed = true;
					settings.Paused = true;
					t.Slot.Live.Status = CheckAccount;
					t.Slot.Live.Note = "could not flatten trade #" + t.Id + " - flatten it in NinjaTrader";
					Log("!!! " + Tag(t) + " is STILL OPEN on " + acct + " after Flatten - routing PAUSED, flatten it manually");
					Save();
				}
			}
		}

		private void StartClosing(RutaPropTrade t, DateTime now)
		{
			double a;
			RutaPropOrderState st;
			Fills(t.StopOrder, out a, out st);
			if (t.StopOrder != null && st == RutaPropOrderState.Working)
				SafeCancel(t.Slot.Account, t.StopOrder);
			Fills(t.TargetOrder, out a, out st);
			if (t.TargetOrder != null && st == RutaPropOrderState.Working)
				SafeCancel(t.Slot.Account, t.TargetOrder);
			SetPhase(t, Closing, now);
			Advance(t, now);
		}

		private void SendClose(RutaPropTrade t, int quantity, DateTime now)
		{
			object order = null;
			string error = null;
			try { order = broker.SubmitMarket(t.Slot.Account, t.Instrument, t.Direction < 0, true, quantity, "RP Exit"); }
			catch (Exception ex) { error = ex.Message; }
			if (order == null)
			{
				ForceFlatten(t, now, "exit order failed: " + (error ?? "account not found"));
				return;
			}
			t.CloseOrder = order;
			SetPhase(t, ExitSent, now);
		}

		private void ForceFlatten(RutaPropTrade t, DateTime now, string why)
		{
			Log("!!! " + Tag(t) + " on " + t.Slot.Account + ": " + why + " - using NinjaTrader Flatten");
			try { broker.Flatten(t.Slot.Account, t.Instrument); }
			catch (Exception ex) { Log("!!! Flatten failed on " + t.Slot.Account + ": " + ex.Message); }
			t.FlatSince = DateTime.MinValue;
			t.Alarmed = false;
			SetPhase(t, FlattenSent, now);
			Advance(t, now);
		}

		private void BookFromOrders(RutaPropTrade t, string how)
		{
			double sa, ta, ca;
			RutaPropOrderState st;
			int sf = Fills(t.StopOrder, out sa, out st);
			int tf = Fills(t.TargetOrder, out ta, out st);
			int cf = Fills(t.CloseOrder, out ca, out st);
			int qty = sf + tf + cf;
			if (qty != t.EntryFilled)
				Log("!!! " + Tag(t) + " exit fills (" + qty + ") don't match the entry (" + t.EntryFilled + ") - check the account");
			Book(t, sf * sa + tf * ta + cf * ca, qty, how, false, double.NaN);
		}

		// After NinjaTrader's Flatten the closing fill is not ours: use the account's cash change when it reports one,
		// otherwise the known fills plus the rest at the strategy's exit price
		private void BookEstimated(RutaPropTrade t)
		{
			double cash = SafeCash(t.Slot.Account);
			if (!double.IsNaN(t.CashAtEntry) && !double.IsNaN(cash) && cash != t.CashAtEntry)
			{
				Book(t, double.NaN, t.EntryFilled, "flattened, P&L from the account's cash", true, cash - t.CashAtEntry);
				return;
			}
			double sa, ta, ca;
			RutaPropOrderState st;
			int sf = Fills(t.StopOrder, out sa, out st);
			int tf = Fills(t.TargetOrder, out ta, out st);
			int cf = Fills(t.CloseOrder, out ca, out st);
			int rest = Math.Max(0, t.EntryFilled - sf - tf - cf);
			double px = double.IsNaN(t.ExitRefPrice) ? t.EntryPrice : t.ExitRefPrice;
			Book(t, sf * sa + tf * ta + cf * ca + rest * px, sf + tf + cf + rest, "flattened, P&L ESTIMATED", true, double.NaN);
		}

		// pnlOverride: NaN = from the fills (minus commission)
		private void Book(RutaPropTrade t, double exitValue, int exitQty, string how, bool estimated, double pnlOverride)
		{
			double pnl = pnlOverride;
			if (double.IsNaN(pnl))
				pnl = (exitQty > 0 ? t.Direction * (exitValue - exitQty * t.EntryPrice) * t.PointValue : 0) - settings.CommissionPerContract * t.EntryFilled;
			RutaPropSlot s = t.Slot;
			RutaPropBook b = t.Live ? s.Live : s.Dry;
			b.Pnl += pnl;
			b.DayPnl += pnl;
			b.Trades++;
			if (pnl > 0)
				b.Wins++;
			b.PeakIntraday = Math.Max(b.PeakIntraday, b.Pnl);
			double exitPrice = exitQty > 0 && !double.IsNaN(exitValue) ? exitValue / exitQty : double.NaN;
			Log(Tag(t) + " on " + s.Account + " closed (" + how + ")" + (double.IsNaN(exitPrice) ? "" : " @ " + Px(exitPrice)) + ": " + Money(pnl)
				+ " | account P&L " + Money(b.Pnl) + ", today " + Money(b.DayPnl));
			Remove(t);
			Evaluate(s, b, true);
			if (estimated)
			{
				Log("!!! " + s.Account + ": that P&L is an estimate - compare with the firm's dashboard and use Edit if needed");
				if (b.Status == Ready)
					b.Note = "last P&L estimated - check it";
			}
			Save();
		}

		private void Evaluate(RutaPropSlot s, RutaPropBook b, bool log)
		{
			if (b.Status == CheckAccount)
				return;
			RutaPropPlan p = PlanOf(s);
			string name = s.Account.Length > 0 ? s.Account : "account";
			double floor = Floor(p, b);
			if (b.Pnl <= floor)
			{
				b.Status = MaxLossHit;
				b.Note = "max loss line " + Money(floor);
				if (log) Log("!!! " + name + " is at the max loss line (" + Money(floor) + ") - no more trades on it");
			}
			else if (b.Pnl >= Required(p, b))
			{
				b.Status = TargetReached;
				b.Note = "passed in " + b.TradingDays + " trading days";
				if (log) Log("*** " + name + " reached the profit target (" + Money(Required(p, b)) + ") in " + b.TradingDays + " trading days");
			}
			else if (b.Status == Ready)
			{
				string why = DayStopReason(p, b);
				if (why != null)
				{
					b.Status = DoneToday;
					b.Note = why;
					if (log) Log(name + " done for today: " + why);
				}
			}
		}

		private static void EndOfDay(RutaPropBook b)
		{
			b.PeakEod = Math.Max(b.PeakEod, b.Pnl);
			b.BestDay = Math.Max(b.BestDay, b.DayPnl);
			b.DayPnl = 0;
			if (b.Status == DoneToday)
			{
				b.Status = Ready;
				b.Note = "";
			}
		}

		private void Remove(RutaPropTrade t)
		{
			trades.Remove(t);
			if (t.Slot != null && t.Slot.Trade == t)
				t.Slot.Trade = null;
		}

		private static void SetPhase(RutaPropTrade t, string phase, DateTime now)
		{
			t.Phase = phase;
			t.PhaseSince = now;
		}

		private void SafeCancel(string account, object order)
		{
			try { broker.Cancel(account, order); }
			catch (Exception ex) { Log("!!! cancel failed on " + account + ": " + ex.Message); }
		}

		private bool SafeConnected(string account)
		{
			try { return broker.IsConnected(account); }
			catch (Exception) { return false; }
		}

		private double SafeCash(string account)
		{
			try { return broker.CashValue(account); }
			catch (Exception) { return double.NaN; }
		}

		private int SafePositions(string account)
		{
			try { return broker.OpenPositions(account); }
			catch (Exception) { return 0; }
		}

		private int SafePosition(string account, Instrument instrument)
		{
			try { return broker.Position(account, instrument); }
			catch (Exception) { return int.MaxValue; }		// unknown: never treated as flat
		}
		#endregion

		#region Text
		private static string Tag(RutaPropTrade t) { return "#" + t.Id + " " + t.Side + " " + t.Quantity; }

		private static string Describe(RutaPropTrade t)
		{
			return Tag(t) + (double.IsNaN(t.EntryPrice) ? "" : " @ " + Px(t.EntryPrice))
				+ (double.IsNaN(t.StopPrice) ? "" : ", stop " + Px(t.StopPrice) + ", target " + Px(t.TargetPrice)) + " (" + t.Phase + ")";
		}

		public static string Money(double v)
		{
			if (double.IsNaN(v))
				return "-";
			return v.ToString("+$#,##0.00;-$#,##0.00;$0.00", Inv);
		}

		public static string Px(double v)
		{
			return double.IsNaN(v) ? "?" : v.ToString("0.00####", Inv);
		}

		public void Log(string message)
		{
			lock (sync)
			{
				DateTime now = Clock();
				string line = now.ToString("HH:mm:ss", Inv) + "  " + message;
				logLines.Add(line);
				if (logLines.Count > 500)
					logLines.RemoveRange(0, logLines.Count - 500);
				logVersion++;
				if (logPath == null)
					return;
				try
				{
					if (++linesSinceSizeCheck >= 200)
					{
						linesSinceSizeCheck = 0;
						System.IO.FileInfo fi = new System.IO.FileInfo(logPath);
						if (fi.Exists && fi.Length > 5000000)
						{
							System.IO.File.Copy(logPath, logPath + ".old", true);
							System.IO.File.Delete(logPath);
						}
					}
					System.IO.File.AppendAllText(logPath, now.ToString("yyyy-MM-dd ", Inv) + line + Environment.NewLine);
				}
				catch (Exception) { }
			}
		}
		#endregion

		#region Save / load (Documents\NinjaTrader 8\RutaCryptoMirror\PropAccountManager.xml)
		private static string D(double v) { return v.ToString("R", Inv); }

		private static double DblAttr(XElement e, string name, double def)
		{
			XAttribute a = e != null ? e.Attribute(name) : null;
			double v;
			return a != null && double.TryParse(a.Value, NumberStyles.Float, Inv, out v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : def;
		}

		private static long LongAttr(XElement e, string name, long def)
		{
			XAttribute a = e != null ? e.Attribute(name) : null;
			long v;
			return a != null && long.TryParse(a.Value, NumberStyles.Integer, Inv, out v) ? v : def;
		}

		private static bool BoolAttr(XElement e, string name, bool def)
		{
			XAttribute a = e != null ? e.Attribute(name) : null;
			bool v;
			return a != null && bool.TryParse(a.Value, out v) ? v : def;
		}

		private static string StrAttr(XElement e, string name, string def)
		{
			XAttribute a = e != null ? e.Attribute(name) : null;
			return a != null ? a.Value : def;
		}

		private static T EnumAttr<T>(XElement e, string name, T def) where T : struct
		{
			XAttribute a = e != null ? e.Attribute(name) : null;
			T v;
			return a != null && Enum.TryParse(a.Value, out v) && Enum.IsDefined(typeof(T), v) ? v : def;
		}

		private static XElement BookXml(string name, RutaPropBook b)
		{
			return new XElement(name,
				new XAttribute("Pnl", D(b.Pnl)), new XAttribute("PeakEod", D(b.PeakEod)), new XAttribute("PeakIntraday", D(b.PeakIntraday)),
				new XAttribute("BestDay", D(b.BestDay)), new XAttribute("DayPnl", D(b.DayPnl)), new XAttribute("TradingDays", b.TradingDays),
				new XAttribute("Trades", b.Trades), new XAttribute("Wins", b.Wins), new XAttribute("LastTradeSession", b.LastTradeSession),
				new XAttribute("Status", b.Status), new XAttribute("Note", b.Note ?? ""));
		}

		private static void ReadBook(XElement e, RutaPropBook b)
		{
			if (e == null)
				return;
			b.Pnl = DblAttr(e, "Pnl", 0);
			b.PeakEod = DblAttr(e, "PeakEod", 0);
			b.PeakIntraday = DblAttr(e, "PeakIntraday", 0);
			b.BestDay = DblAttr(e, "BestDay", 0);
			b.DayPnl = DblAttr(e, "DayPnl", 0);
			b.TradingDays = (int)LongAttr(e, "TradingDays", 0);
			b.Trades = (int)LongAttr(e, "Trades", 0);
			b.Wins = (int)LongAttr(e, "Wins", 0);
			b.LastTradeSession = LongAttr(e, "LastTradeSession", 0);
			string status = StrAttr(e, "Status", Ready);
			b.Status = status == DoneToday || status == TargetReached || status == MaxLossHit || status == CheckAccount ? status : Ready;
			b.Note = StrAttr(e, "Note", "");
		}

		public void Save()
		{
			if (configPath == null)
				return;
			lock (sync)
			{
				try
				{
					XElement st = new XElement("Settings",
						new XAttribute("Rotation", settings.Rotation), new XAttribute("DailyGoal", D(settings.DailyGoal)),
						new XAttribute("GoalTolerance", D(settings.GoalTolerance)), new XAttribute("DailyMaxLoss", D(settings.DailyMaxLoss)),
						new XAttribute("BlockOverDailyLoss", settings.BlockOverDailyLoss), new XAttribute("BlockOverAccountLoss", settings.BlockOverAccountLoss),
						new XAttribute("MaxContracts", settings.MaxContracts), new XAttribute("CommissionPerContract", D(settings.CommissionPerContract)),
						new XAttribute("Paused", settings.Paused), new XAttribute("CustomTarget", D(settings.CustomTarget)),
						new XAttribute("CustomMaxLoss", D(settings.CustomMaxLoss)), new XAttribute("CustomDrawdown", settings.CustomDrawdown),
						new XAttribute("CustomDailyLossLimit", D(settings.CustomDailyLossLimit)), new XAttribute("CustomConsistencyPct", D(settings.CustomConsistencyPct)));
					XElement root = new XElement("RutaPropManager", new XAttribute("Version", 1), st,
						new XElement("Session", new XAttribute("Start", sessionStart.Ticks), new XAttribute("Id", sessionId),
							new XAttribute("LastAssigned", lastAssigned), new XAttribute("NextTradeId", nextTradeId)));
					foreach (RutaPropSlot s in slots)
					{
						XElement e = new XElement("Account", new XAttribute("Enabled", s.Enabled), new XAttribute("Name", s.Account), new XAttribute("Plan", s.PlanKey),
							BookXml("Live", s.Live), BookXml("Dry", s.Dry));
						if (s.Trade != null && s.Trade.Live)
							e.SetAttributeValue("OpenTrade", Describe(s.Trade) + " " + s.Trade.Instrument.FullName);
						root.Add(e);
					}
					string tmp = configPath + ".tmp";
					new XDocument(root).Save(tmp);
					System.IO.File.Copy(tmp, configPath, true);
					System.IO.File.Delete(tmp);
				}
				catch (Exception ex)
				{
					Log("!!! could not save " + configPath + ": " + ex.Message);
				}
			}
		}

		private void Load()
		{
			if (configPath == null || !System.IO.File.Exists(configPath))
				return;
			try
			{
				XElement root = XDocument.Load(configPath).Root;
				XElement st = root.Element("Settings");
				RutaPropSettings v = new RutaPropSettings();
				v.Rotation = EnumAttr(st, "Rotation", v.Rotation);
				v.DailyGoal = DblAttr(st, "DailyGoal", v.DailyGoal);
				v.GoalTolerance = DblAttr(st, "GoalTolerance", v.GoalTolerance);
				v.DailyMaxLoss = DblAttr(st, "DailyMaxLoss", v.DailyMaxLoss);
				v.BlockOverDailyLoss = BoolAttr(st, "BlockOverDailyLoss", v.BlockOverDailyLoss);
				v.BlockOverAccountLoss = BoolAttr(st, "BlockOverAccountLoss", v.BlockOverAccountLoss);
				v.MaxContracts = Math.Max(1, (int)LongAttr(st, "MaxContracts", v.MaxContracts));
				v.CommissionPerContract = DblAttr(st, "CommissionPerContract", v.CommissionPerContract);
				v.Paused = BoolAttr(st, "Paused", v.Paused);
				v.CustomTarget = DblAttr(st, "CustomTarget", v.CustomTarget);
				v.CustomMaxLoss = DblAttr(st, "CustomMaxLoss", v.CustomMaxLoss);
				v.CustomDrawdown = EnumAttr(st, "CustomDrawdown", v.CustomDrawdown);
				v.CustomDailyLossLimit = DblAttr(st, "CustomDailyLossLimit", v.CustomDailyLossLimit);
				v.CustomConsistencyPct = DblAttr(st, "CustomConsistencyPct", v.CustomConsistencyPct);
				settings = v;
				XElement se = root.Element("Session");
				long ticks = LongAttr(se, "Start", 0);
				sessionStart = ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks) : DateTime.MinValue;
				sessionId = Math.Max(1, LongAttr(se, "Id", 1));
				lastAssigned = (int)LongAttr(se, "LastAssigned", -1);
				nextTradeId = (int)Math.Max(1, LongAttr(se, "NextTradeId", 1));
				foreach (XElement e in root.Elements("Account"))
				{
					RutaPropSlot s = new RutaPropSlot();
					s.Enabled = BoolAttr(e, "Enabled", true);
					s.Account = StrAttr(e, "Name", "").Trim();
					RutaPropPlan plan = RutaPropPlan.Get(StrAttr(e, "Plan", "Custom"), settings);
					s.PlanKey = plan != null ? plan.Key : "Custom";
					ReadBook(e.Element("Live"), s.Live);
					ReadBook(e.Element("Dry"), s.Dry);
					string open = StrAttr(e, "OpenTrade", null);
					if (open != null)
					{
						s.Live.Status = CheckAccount;
						s.Live.Note = "trade " + open + " was open when NinjaTrader closed: check the account and the P&L (Edit), then press Ready";
					}
					slots.Add(s);
				}
				if (lastAssigned >= slots.Count)
					lastAssigned = slots.Count - 1;
			}
			catch (Exception ex)
			{
				Log("!!! could not read " + configPath + " (" + ex.Message + "), starting with an empty list");
			}
		}
		#endregion
	}

	/// <summary>The real broker: NinjaTrader accounts (Control Center > Accounts). Orders are marked as automated.</summary>
	public sealed class RutaPropNtBroker : IRutaPropBroker
	{
		private static NinjaTrader.Cbi.Account Find(string name)
		{
			lock (NinjaTrader.Cbi.Account.All)
			{
				foreach (NinjaTrader.Cbi.Account a in NinjaTrader.Cbi.Account.All)
					if (a.Name == name)
						return a;
			}
			return null;
		}

		public List<string> AccountNames()
		{
			List<string> names = new List<string>();
			lock (NinjaTrader.Cbi.Account.All)
			{
				foreach (NinjaTrader.Cbi.Account a in NinjaTrader.Cbi.Account.All)
					names.Add(a.Name);
			}
			return names;
		}

		// Accounts are listed while their connection is connected
		public bool IsConnected(string account)
		{
			return Find(account) != null;
		}

		public double CashValue(string account)
		{
			NinjaTrader.Cbi.Account a = Find(account);
			if (a == null)
				return double.NaN;
			double v = a.Get(AccountItem.CashValue, Currency.UsDollar);
			return v != 0 ? v : double.NaN;
		}

		public int Position(string account, Instrument instrument)
		{
			NinjaTrader.Cbi.Account a = Find(account);
			if (a == null)
				return int.MaxValue;		// unknown
			lock (a.Positions)
			{
				foreach (NinjaTrader.Cbi.Position p in a.Positions)
				{
					if (p.Instrument == null || p.Instrument.FullName != instrument.FullName)
						continue;
					if (p.MarketPosition == MarketPosition.Long)
						return p.Quantity;
					if (p.MarketPosition == MarketPosition.Short)
						return -p.Quantity;
				}
			}
			return 0;
		}

		public int OpenPositions(string account)
		{
			NinjaTrader.Cbi.Account a = Find(account);
			if (a == null)
				return 0;
			int n = 0;
			lock (a.Positions)
			{
				foreach (NinjaTrader.Cbi.Position p in a.Positions)
					if (p.MarketPosition != MarketPosition.Flat && p.Quantity != 0)
						n++;
			}
			return n;
		}

		public object SubmitMarket(string account, Instrument instrument, bool buy, bool closing, int quantity, string name)
		{
			NinjaTrader.Cbi.Account a = Find(account);
			if (a == null)
				return null;
			OrderAction action = buy ? (closing ? OrderAction.BuyToCover : OrderAction.Buy) : (closing ? OrderAction.Sell : OrderAction.SellShort);
			NinjaTrader.Cbi.Order o = a.CreateOrder(instrument, action, OrderType.Market, OrderEntry.Automated, TimeInForce.Day, quantity, 0, 0, string.Empty, name,
				NinjaTrader.Core.Globals.MaxDate, null);
			a.Submit(new[] { o });
			return o;
		}

		public object[] SubmitOco(string account, Instrument instrument, bool closeLong, int quantity, double stopPrice, double targetPrice, string oco)
		{
			NinjaTrader.Cbi.Account a = Find(account);
			if (a == null)
				return null;
			OrderAction action = closeLong ? OrderAction.Sell : OrderAction.BuyToCover;
			NinjaTrader.Cbi.Order stop = a.CreateOrder(instrument, action, OrderType.StopMarket, OrderEntry.Automated, TimeInForce.Gtc, quantity, 0, stopPrice, oco, "RP Stop",
				NinjaTrader.Core.Globals.MaxDate, null);
			NinjaTrader.Cbi.Order target = a.CreateOrder(instrument, action, OrderType.Limit, OrderEntry.Automated, TimeInForce.Gtc, quantity, targetPrice, 0, oco, "RP Target",
				NinjaTrader.Core.Globals.MaxDate, null);
			a.Submit(new[] { stop, target });
			return new object[] { stop, target };
		}

		public RutaPropOrderState GetState(object order, out int filled, out double averagePrice)
		{
			NinjaTrader.Cbi.Order o = order as NinjaTrader.Cbi.Order;
			if (o == null)
			{
				filled = 0;
				averagePrice = double.NaN;
				return RutaPropOrderState.Rejected;
			}
			filled = o.Filled;
			averagePrice = o.AverageFillPrice;
			switch (o.OrderState)
			{
				case OrderState.Filled:		return RutaPropOrderState.Filled;
				case OrderState.Cancelled:	return RutaPropOrderState.Cancelled;
				case OrderState.Rejected:	return RutaPropOrderState.Rejected;
				default:					return RutaPropOrderState.Working;
			}
		}

		public void Cancel(string account, object order)
		{
			NinjaTrader.Cbi.Account a = Find(account);
			NinjaTrader.Cbi.Order o = order as NinjaTrader.Cbi.Order;
			if (a != null && o != null)
				a.Cancel(new[] { o });
		}

		public void Flatten(string account, Instrument instrument)
		{
			NinjaTrader.Cbi.Account a = Find(account);
			if (a != null)
				a.Flatten(new[] { instrument });
		}

		public double RoundToTick(Instrument instrument, double price)
		{
			return instrument.MasterInstrument.RoundToTickSize(price);
		}
	}
}
