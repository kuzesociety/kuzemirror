// Prop Account Manager - the window. Control Center > New > Prop Account Manager.
// Needs RutaPropRouter.cs (same folder). Closing the window does NOT stop the manager: open trades keep being
// managed and new trades keep being routed. Use "Back to DRY RUN", "Paused" or FLATTEN ALL to stop it.
namespace NinjaTrader.NinjaScript.AddOns
{
	// usings inside the namespace, so a same-named type from another add-on can't hide the WPF ones
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Windows;
	using System.Windows.Controls;
	using System.Windows.Input;
	using System.Windows.Media;
	using System.Windows.Threading;

	/// <summary>Adds "Prop Account Manager" to the Control Center's New menu.</summary>
	public class RutaPropManager : AddOnBase
	{
		private const string NewMenuId = "ControlCenterMenuItemNew";
		private MenuItem menuItem;
		private ItemsControl menuParent;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Prop Account Manager for RutaCryptoMirror: routes live trades to prop-firm evaluation accounts with rotation, daily goal / max loss and firm rules.";
				Name = "Ruta Prop Account Manager";
			}
			else if (State == State.Terminated)
				RemoveMenuItem();
		}

		protected override void OnWindowCreated(Window window)
		{
			if (window == null || menuItem != null || window.GetType().Name.IndexOf("ControlCenter", StringComparison.Ordinal) < 0)
				return;
			if (TryAddMenuItem(window))
				return;
			// The menu bar may not be built yet: try again once the Control Center is shown
			Action retry = () =>
			{
				if (menuItem == null && !TryAddMenuItem(window))
					Print("Prop Account Manager: could not find the Control Center's New menu. Open the manager from the strategy instead: Route to Prop Account Manager = on opens it.");
			};
			if (window.IsLoaded)
				window.Dispatcher.BeginInvoke(retry, DispatcherPriority.ApplicationIdle);
			else
			{
				RoutedEventHandler onLoaded = null;
				onLoaded = (s, e) =>
				{
					window.Loaded -= onLoaded;
					window.Dispatcher.BeginInvoke(retry, DispatcherPriority.ApplicationIdle);
				};
				window.Loaded += onLoaded;
			}
		}

		protected override void OnWindowDestroyed(Window window)
		{
			if (window != null && window.GetType().Name.IndexOf("ControlCenter", StringComparison.Ordinal) >= 0)
				RemoveMenuItem();
		}

		// Control Center > New, found the way NinjaTrader's own add-on sample does (FindFirst + automation id), then by
		// searching the window's element trees; last resort: a top-level entry in the Control Center's menu bar
		private bool TryAddMenuItem(Window window)
		{
			ItemsControl parent = null;
			try
			{
				parent = NinjaTraderFindFirst(window, NewMenuId) as MenuItem;
				if (parent == null)
					parent = FindElement(window, d => d is MenuItem && (System.Windows.Automation.AutomationProperties.GetAutomationId(d) == NewMenuId || ((MenuItem)d).Name == NewMenuId)) as MenuItem;
				if (parent == null)
					parent = FindElement(window, d => d is MenuItem && ((MenuItem)d).Header is string && ((string)((MenuItem)d).Header).Replace("_", "").Trim() == "New") as MenuItem;
				if (parent == null)
					parent = FindElement(window, d => d is Menu) as Menu;
			}
			catch (Exception ex)
			{
				Print("Prop Account Manager: menu search failed: " + ex.Message);
			}
			if (parent == null)
				return false;
			menuItem = new MenuItem();
			menuItem.Header = "Prop Account Manager";
			Style style = Application.Current != null ? Application.Current.TryFindResource("MainMenuItem") as Style : null;
			if (style != null)
				menuItem.Style = style;
			menuItem.Click += OnMenuItemClick;
			parent.Items.Add(menuItem);
			menuParent = parent;
			return true;
		}

		// NinjaTrader's FindFirst(window, automationId) helper, looked up at run time so this file doesn't depend on where it lives
		private static object NinjaTraderFindFirst(Window window, string automationId)
		{
			foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				string name = asm.GetName().Name;
				if (name != "NinjaTrader.Gui" && name != "NinjaTrader.Core")
					continue;
				Type[] types;
				try { types = asm.GetTypes(); }
				catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
				foreach (Type t in types)
				{
					if (!t.IsAbstract || !t.IsSealed)		// static classes only (extension methods)
						continue;
					foreach (System.Reflection.MethodInfo m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
					{
						if (m.Name != "FindFirst" || m.IsGenericMethodDefinition)
							continue;
						System.Reflection.ParameterInfo[] p = m.GetParameters();
						if (p.Length == 2 && p[1].ParameterType == typeof(string) && p[0].ParameterType.IsInstanceOfType(window))
						{
							try { return m.Invoke(null, new object[] { window, automationId }); }
							catch (Exception) { return null; }
						}
					}
				}
			}
			return null;
		}

		// Breadth-first search over the logical AND visual trees (menu bars often sit in the window's template)
		private static DependencyObject FindElement(DependencyObject root, Func<DependencyObject, bool> match)
		{
			HashSet<DependencyObject> seen = new HashSet<DependencyObject>();
			Queue<DependencyObject> queue = new Queue<DependencyObject>();
			queue.Enqueue(root);
			while (queue.Count > 0 && seen.Count < 50000)
			{
				DependencyObject node = queue.Dequeue();
				if (node == null || !seen.Add(node))
					continue;
				if (match(node))
					return node;
				foreach (object child in LogicalTreeHelper.GetChildren(node))
				{
					DependencyObject d = child as DependencyObject;
					if (d != null)
						queue.Enqueue(d);
				}
				if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
				{
					int n = VisualTreeHelper.GetChildrenCount(node);
					for (int i = 0; i < n; i++)
						queue.Enqueue(VisualTreeHelper.GetChild(node, i));
				}
			}
			return null;
		}

		private void RemoveMenuItem()
		{
			MenuItem item = menuItem;
			ItemsControl parent = menuParent;
			menuItem = null;
			menuParent = null;
			if (item == null || parent == null)
				return;
			Action remove = () =>
			{
				if (parent.Items.Contains(item))
					parent.Items.Remove(item);
				item.Click -= OnMenuItemClick;
			};
			if (parent.Dispatcher.CheckAccess())
				remove();
			else
				parent.Dispatcher.BeginInvoke(remove);
		}

		private void OnMenuItemClick(object sender, RoutedEventArgs e)
		{
			RutaPropManagerWindow.OpenOrActivate(true);
		}
	}

	public class RutaPropManagerWindow : NinjaTrader.Gui.Tools.NTWindow
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static readonly string[] Headers = { "On", "Account", "Plan", "Status", "Eval P&L", "Today", "Best day", "EOD peak", "Max loss line", "To target", "Days", "Trades", "Cash", "Open trade / note", "" };
		private static readonly Thickness Cell = new Thickness(3, 2, 3, 2);

		private sealed class SettingField
		{
			public TextBox Box;
			public Func<RutaPropSettings, double> Get;
			public Action<RutaPropSettings, double> Set;
			public bool IsInt;
		}

		private sealed class RowControls
		{
			public int Index;
			public CheckBox On;
			public ComboBox Account, Plan;
			public TextBlock Status, Today, Floor, ToTarget, Trades, Cash, Info;
			public TextBox Pnl, Best, Peak, Days;
			public Button Ready, NewEval, Remove;
			public double ShownPnl, ShownBest, ShownPeak, ShownDays;
		}

		private static RutaPropManagerWindow openWindow;

		/// <summary>
		/// Opens the window (one at a time) on NinjaTrader's main thread. Called by the Control Center menu, and by the
		/// strategy (through RutaPropRouter.OpenWindow) when it starts with Route to Prop Account Manager = on.
		/// </summary>
		public static void OpenOrActivate(bool bringToFront)
		{
			Application app = Application.Current;
			if (app == null)
				return;
			app.Dispatcher.BeginInvoke(new Action(() =>
			{
				if (openWindow != null && openWindow.IsLoaded)
				{
					if (bringToFront)
					{
						if (openWindow.WindowState == WindowState.Minimized)
							openWindow.WindowState = WindowState.Normal;
						openWindow.Activate();
					}
					return;
				}
				openWindow = new RutaPropManagerWindow();
				openWindow.Closed += (s, a) => openWindow = null;
				openWindow.Show();
			}));
		}

		private readonly RutaPropRouter router;
		private readonly DispatcherTimer timer;
		private readonly List<SettingField> fields = new List<SettingField>();
		private readonly List<RowControls> rows = new List<RowControls>();
		private TextBlock modeText, modeHint, statusText;
		private Button modeButton;
		private CheckBox pausedBox, blockDailyBox, blockAccountBox;
		private ComboBox rotationBox, drawdownBox;
		private Grid accountsGrid;
		private TextBox logBox;
		private int shownLogVersion = -1;
		private bool refreshing;

		public RutaPropManagerWindow()
		{
			router = RutaPropRouter.Instance;
			Caption = "Prop Account Manager";
			Width = 1400;
			Height = 760;

			DockPanel root = new DockPanel();
			root.Margin = new Thickness(8);
			StackPanel top = new StackPanel();
			DockPanel.SetDock(top, Dock.Top);
			top.Children.Add(BuildModeRow());
			top.Children.Add(BuildRulesRow());
			top.Children.Add(BuildCustomRow());
			root.Children.Add(top);
			DockPanel log = BuildLog();
			DockPanel.SetDock(log, Dock.Bottom);
			root.Children.Add(log);
			root.Children.Add(BuildAccounts());
			Content = root;

			LoadSettings();
			RebuildRows();
			Refresh();
			timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
			timer.Interval = TimeSpan.FromMilliseconds(700);
			timer.Tick += (s, e) => Refresh();
			timer.Start();
			Closed += (s, e) =>
			{
				timer.Stop();
				router.Save();
			};
		}

		#region Layout
		private static Button MakeButton(string text, string tip, RoutedEventHandler click)
		{
			Button b = new Button();
			b.Content = text;
			b.ToolTip = tip;
			b.Padding = new Thickness(10, 3, 10, 3);
			b.Margin = new Thickness(4, 0, 4, 0);
			b.VerticalAlignment = VerticalAlignment.Center;
			b.Click += click;
			return b;
		}

		private static TextBlock Label(string text, string tip)
		{
			TextBlock t = new TextBlock();
			t.Text = text;
			t.ToolTip = tip;
			t.VerticalAlignment = VerticalAlignment.Center;
			t.Margin = new Thickness(12, 0, 4, 0);
			return t;
		}

		private CheckBox MakeCheck(string text, string tip, Action<RutaPropSettings, bool> set)
		{
			CheckBox c = new CheckBox();
			c.Content = text;
			c.ToolTip = tip;
			c.VerticalAlignment = VerticalAlignment.Center;
			c.Margin = new Thickness(12, 0, 4, 0);
			c.Click += (s, e) => ChangeSettings(st => set(st, c.IsChecked == true));
			return c;
		}

		private void AddNumber(Panel panel, string label, string tip, Func<RutaPropSettings, double> get, Action<RutaPropSettings, double> set, bool isInt)
		{
			TextBox b = new TextBox();
			b.Width = 70;
			b.ToolTip = tip;
			b.VerticalAlignment = VerticalAlignment.Center;
			b.HorizontalContentAlignment = HorizontalAlignment.Right;
			SettingField f = new SettingField();
			f.Box = b;
			f.Get = get;
			f.Set = set;
			f.IsInt = isInt;
			b.LostFocus += (s, e) => CommitField(f);
			b.KeyDown += (s, e) => { if (e.Key == Key.Enter) CommitField(f); };
			fields.Add(f);
			panel.Children.Add(Label(label, tip));
			panel.Children.Add(b);
		}

		private UIElement BuildModeRow()
		{
			WrapPanel p = new WrapPanel();
			p.Margin = new Thickness(0, 0, 0, 8);
			modeText = new TextBlock();
			modeText.FontSize = 20;
			modeText.FontWeight = FontWeights.Bold;
			modeText.MinWidth = 110;
			modeText.VerticalAlignment = VerticalAlignment.Center;
			p.Children.Add(modeText);
			modeButton = MakeButton("Go LIVE", "DRY RUN: nothing is sent to any account. LIVE: trades from the strategies are sent to the accounts below.", OnModeClick);
			p.Children.Add(modeButton);
			pausedBox = MakeCheck("Paused (no new trades)", "Signals are skipped while paused. Open trades keep their stop / target.", (st, v) => st.Paused = v);
			p.Children.Add(pausedBox);
			Button flatten = MakeButton("FLATTEN ALL", "Pause and close every trade the manager opened (press again to force NinjaTrader's Flatten). Positions you opened yourself are not touched.", OnFlattenClick);
			flatten.FontWeight = FontWeights.Bold;
			flatten.Foreground = Brushes.White;
			flatten.Background = Brushes.DarkRed;
			p.Children.Add(flatten);
			modeHint = new TextBlock();
			modeHint.VerticalAlignment = VerticalAlignment.Center;
			modeHint.Margin = new Thickness(16, 0, 0, 0);
			modeHint.FontStyle = FontStyles.Italic;
			p.Children.Add(modeHint);
			statusText = new TextBlock();
			statusText.VerticalAlignment = VerticalAlignment.Center;
			statusText.Margin = new Thickness(16, 0, 0, 0);
			p.Children.Add(statusText);
			return p;
		}

		private UIElement BuildRulesRow()
		{
			WrapPanel p = new WrapPanel();
			p.Margin = new Thickness(0, 0, 0, 6);
			p.Children.Add(Label("Rotation", "EveryTrade: each trade goes to the next account. UntilDayDone: stay on one account until its day is done (goal / loss limit), then the next."));
			rotationBox = new ComboBox();
			rotationBox.Width = 120;
			rotationBox.VerticalAlignment = VerticalAlignment.Center;
			foreach (string name in Enum.GetNames(typeof(RutaPropRotation)))
				rotationBox.Items.Add(name);
			rotationBox.SelectionChanged += (s, e) =>
			{
				if (!refreshing && rotationBox.SelectedItem != null)
					ChangeSettings(st => st.Rotation = (RutaPropRotation)Enum.Parse(typeof(RutaPropRotation), (string)rotationBox.SelectedItem));
			};
			p.Children.Add(rotationBox);
			AddNumber(p, "Daily goal $", "An account is done for the day when today's closed trades reach this (minus the tolerance). 0 = off.", st => st.DailyGoal, (st, v) => st.DailyGoal = v, false);
			AddNumber(p, "Tolerance $", "Goal 1500, tolerance 100: done from +1400.", st => st.GoalTolerance, (st, v) => st.GoalTolerance = v, false);
			AddNumber(p, "Daily max loss $", "An account is done for the day when today's closed trades lose this much. 0 = off.", st => st.DailyMaxLoss, (st, v) => st.DailyMaxLoss = v, false);
			blockDailyBox = MakeCheck("Skip if the stop could break a daily limit", "Don't give a trade to an account when its full stop would take today past the daily max loss or the firm's daily loss limit.", (st, v) => st.BlockOverDailyLoss = v);
			p.Children.Add(blockDailyBox);
			blockAccountBox = MakeCheck("Skip if the stop could fail the account", "Don't give a trade to an account when its full stop would cross the account's max loss line. Can leave an account unable to trade.", (st, v) => st.BlockOverAccountLoss = v);
			p.Children.Add(blockAccountBox);
			AddNumber(p, "Max contracts", "Safety cap per order.", st => st.MaxContracts, (st, v) => st.MaxContracts = (int)v, true);
			AddNumber(p, "Commission $/contract", "Round trip, subtracted from each trade's P&L.", st => st.CommissionPerContract, (st, v) => st.CommissionPerContract = v, false);
			return p;
		}

		private UIElement BuildCustomRow()
		{
			WrapPanel p = new WrapPanel();
			p.Margin = new Thickness(0, 0, 0, 8);
			TextBlock t = Label("Custom plan:", "Rules used by accounts whose plan is Custom.");
			t.Margin = new Thickness(0, 0, 4, 0);
			t.FontWeight = FontWeights.Bold;
			p.Children.Add(t);
			AddNumber(p, "Target $", "Profit target.", st => st.CustomTarget, (st, v) => st.CustomTarget = v, false);
			AddNumber(p, "Max loss $", "Distance of the max loss line.", st => st.CustomMaxLoss, (st, v) => st.CustomMaxLoss = v, false);
			p.Children.Add(Label("Drawdown", "EodTrailing: trails the highest end-of-day balance. IntradayTrailing: the highest balance. Static: fixed. Trailing lines stop at the starting balance."));
			drawdownBox = new ComboBox();
			drawdownBox.Width = 130;
			drawdownBox.VerticalAlignment = VerticalAlignment.Center;
			foreach (string name in Enum.GetNames(typeof(RutaPropDrawdown)))
				drawdownBox.Items.Add(name);
			drawdownBox.SelectionChanged += (s, e) =>
			{
				if (!refreshing && drawdownBox.SelectedItem != null)
					ChangeSettings(st => st.CustomDrawdown = (RutaPropDrawdown)Enum.Parse(typeof(RutaPropDrawdown), (string)drawdownBox.SelectedItem));
			};
			p.Children.Add(drawdownBox);
			AddNumber(p, "Daily loss limit $", "Firm's daily loss limit. 0 = none.", st => st.CustomDailyLossLimit, (st, v) => st.CustomDailyLossLimit = v, false);
			AddNumber(p, "Consistency %", "Best day at most this % of the total profit. 0 = no rule.", st => st.CustomConsistencyPct, (st, v) => st.CustomConsistencyPct = v, false);
			TextBlock note = Label("Plans: Topstep 50K/100K/150K $3,000/$6,000/$9,000 target, $2,000/$3,000/$4,500 EOD line, 50% consistency; Lucid Flex 50K $3,000 / $2,000, 50%; Lucid Pro 50K $3,000 / $2,000, no consistency. Check your firm's current rules.", null);
			note.Opacity = 0.7;
			note.TextWrapping = TextWrapping.Wrap;
			note.MaxWidth = 700;
			p.Children.Add(note);
			return p;
		}

		private UIElement BuildAccounts()
		{
			DockPanel panel = new DockPanel();
			StackPanel buttons = new StackPanel();
			buttons.Orientation = Orientation.Horizontal;
			buttons.Margin = new Thickness(0, 6, 0, 6);
			DockPanel.SetDock(buttons, Dock.Bottom);
			buttons.Children.Add(MakeButton("+ Add account", "Add a row, then pick the NinjaTrader account and its plan.", OnAddClick));
			buttons.Children.Add(MakeButton("Reset dry-run numbers", "Set every account's DRY RUN numbers back to 0 (live numbers are kept).", OnResetDryClick));
			TextBlock hint = Label("Eval P&L, Best day, EOD peak and Days can be typed in (copy them from the firm's dashboard when you add an account mid-evaluation). \u25BA = next account.", null);
			hint.Opacity = 0.7;
			buttons.Children.Add(hint);
			panel.Children.Add(buttons);
			accountsGrid = new Grid();
			ScrollViewer sv = new ScrollViewer();
			sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
			sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
			sv.Content = accountsGrid;
			panel.Children.Add(sv);
			return panel;
		}

		private DockPanel BuildLog()
		{
			DockPanel p = new DockPanel();
			p.Height = 190;
			TextBlock title = new TextBlock();
			title.Text = "Log (also saved to Documents\\NinjaTrader 8\\RutaCryptoMirror\\PropAccountManager.log)";
			title.Margin = new Thickness(0, 4, 0, 2);
			DockPanel.SetDock(title, Dock.Top);
			p.Children.Add(title);
			logBox = new TextBox();
			logBox.IsReadOnly = true;
			logBox.FontFamily = new FontFamily("Consolas");
			logBox.FontSize = 12;
			logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
			logBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
			p.Children.Add(logBox);
			return p;
		}

		private T Place<T>(T element, int row, int column) where T : UIElement
		{
			Grid.SetRow(element, row);
			Grid.SetColumn(element, column);
			accountsGrid.Children.Add(element);
			return element;
		}

		private TextBlock TextCell(int row, int column)
		{
			TextBlock t = new TextBlock();
			t.Margin = Cell;
			t.VerticalAlignment = VerticalAlignment.Center;
			return Place(t, row, column);
		}

		private TextBox EditCell(RowControls rc, int row, int column, string tip)
		{
			TextBox b = new TextBox();
			b.Width = 80;
			b.Margin = Cell;
			b.ToolTip = tip;
			b.VerticalAlignment = VerticalAlignment.Center;
			b.HorizontalContentAlignment = HorizontalAlignment.Right;
			b.LostFocus += (s, e) => CommitBook(rc);
			b.KeyDown += (s, e) => { if (e.Key == Key.Enter) CommitBook(rc); };
			return Place(b, row, column);
		}

		private void RebuildRows()
		{
			List<RutaPropRow> data = router.Rows();
			refreshing = true;
			accountsGrid.Children.Clear();
			accountsGrid.RowDefinitions.Clear();
			accountsGrid.ColumnDefinitions.Clear();
			rows.Clear();
			for (int c = 0; c < Headers.Length; c++)
			{
				ColumnDefinition cd = new ColumnDefinition();
				cd.Width = GridLength.Auto;
				accountsGrid.ColumnDefinitions.Add(cd);
			}
			for (int r = 0; r <= data.Count; r++)
			{
				RowDefinition rd = new RowDefinition();
				rd.Height = GridLength.Auto;
				accountsGrid.RowDefinitions.Add(rd);
			}
			for (int c = 0; c < Headers.Length; c++)
			{
				TextBlock h = TextCell(0, c);
				h.Text = Headers[c];
				h.FontWeight = FontWeights.Bold;
			}
			for (int i = 0; i < data.Count; i++)
				rows.Add(BuildRow(i, i + 1));
			refreshing = false;
			UpdateRows(data);
		}

		private RowControls BuildRow(int index, int r)
		{
			RowControls rc = new RowControls();
			rc.Index = index;

			rc.On = Place(new CheckBox(), r, 0);
			rc.On.Margin = Cell;
			rc.On.VerticalAlignment = VerticalAlignment.Center;
			rc.On.ToolTip = "Use this account";
			rc.On.Click += (s, e) => CommitSlot(rc, null);

			rc.Account = Place(new ComboBox(), r, 1);
			rc.Account.IsEditable = true;
			rc.Account.Width = 160;
			rc.Account.Margin = Cell;
			rc.Account.ToolTip = "NinjaTrader account (Control Center > Accounts). It must be connected to trade.";
			rc.Account.DropDownOpened += (s, e) => FillAccounts(rc.Account);
			rc.Account.SelectionChanged += (s, e) =>
			{
				if (!refreshing && rc.Account.SelectedItem is string)
					CommitSlot(rc, (string)rc.Account.SelectedItem);
			};
			rc.Account.LostKeyboardFocus += (s, e) =>
			{
				if (!refreshing && !rc.Account.IsKeyboardFocusWithin && !rc.Account.IsDropDownOpen)
					CommitSlot(rc, null);
			};

			rc.Plan = Place(new ComboBox(), r, 2);
			rc.Plan.Width = 140;
			rc.Plan.Margin = Cell;
			foreach (string key in RutaPropPlan.Keys)
				rc.Plan.Items.Add(key);
			rc.Plan.SelectionChanged += (s, e) =>
			{
				if (!refreshing)
					CommitSlot(rc, null);
			};

			rc.Status = TextCell(r, 3);
			rc.Status.FontWeight = FontWeights.Bold;
			rc.Pnl = EditCell(rc, r, 4, "Evaluation P&L (balance - starting balance)");
			rc.Today = TextCell(r, 5);
			rc.Best = EditCell(rc, r, 6, "Best day of this evaluation (for the consistency rule)");
			rc.Peak = EditCell(rc, r, 7, "Highest end-of-day P&L (the trailing max loss line follows it)");
			rc.Floor = TextCell(r, 8);
			rc.ToTarget = TextCell(r, 9);
			rc.Days = EditCell(rc, r, 10, "Trading days");
			rc.Days.Width = 45;
			rc.Trades = TextCell(r, 11);
			rc.Cash = TextCell(r, 12);
			rc.Info = TextCell(r, 13);
			rc.Info.MaxWidth = 420;
			rc.Info.TextWrapping = TextWrapping.Wrap;

			StackPanel actions = Place(new StackPanel(), r, 14);
			actions.Orientation = Orientation.Horizontal;
			rc.Ready = MakeButton("Ready", "Back to READY (after DONE TODAY, CHECK ACCOUNT, ...). The numbers are kept.", (s, e) => ShowError(router.ClearStatus(rc.Index)));
			rc.NewEval = MakeButton("New eval", "Start a new evaluation on this account: its numbers go back to 0.", (s, e) => OnNewEvalClick(rc));
			rc.Remove = MakeButton("Remove", "Remove this row.", (s, e) => OnRemoveClick(rc));
			actions.Children.Add(rc.Ready);
			actions.Children.Add(rc.NewEval);
			actions.Children.Add(rc.Remove);
			return rc;
		}
		#endregion

		#region Refresh
		private void Refresh()
		{
			try
			{
				bool live = router.Mode == RutaPropMode.Live;
				modeText.Text = live ? "LIVE" : "DRY RUN";
				modeText.Foreground = live ? Brushes.OrangeRed : Brushes.DodgerBlue;
				modeButton.Content = live ? "Back to DRY RUN" : "Go LIVE";
				modeHint.Text = live ? "real orders - live numbers shown" : "nothing is sent - dry-run numbers shown";
				Caption = "Prop Account Manager - " + (live ? "LIVE" : "DRY RUN");
				statusText.Text = router.ShortStatus() + "   |   " + router.SessionText();
				LoadSettings();
				UpdateRows(router.Rows());
				int version;
				string[] lines = router.LogLines(out version);
				if (version != shownLogVersion && !logBox.IsKeyboardFocusWithin)
				{
					shownLogVersion = version;
					logBox.Text = string.Join(Environment.NewLine, lines);
					logBox.ScrollToEnd();
				}
			}
			catch (Exception ex)
			{
				statusText.Text = "display error: " + ex.Message;
			}
		}

		private void LoadSettings()
		{
			RutaPropSettings st = router.GetSettings();
			refreshing = true;
			foreach (SettingField f in fields)
				if (!f.Box.IsKeyboardFocusWithin)
					f.Box.Text = Num(f.Get(st));
			pausedBox.IsChecked = st.Paused;
			blockDailyBox.IsChecked = st.BlockOverDailyLoss;
			blockAccountBox.IsChecked = st.BlockOverAccountLoss;
			if (!rotationBox.IsDropDownOpen)
				rotationBox.SelectedItem = st.Rotation.ToString();
			if (!drawdownBox.IsDropDownOpen)
				drawdownBox.SelectedItem = st.CustomDrawdown.ToString();
			refreshing = false;
		}

		private void UpdateRows(List<RutaPropRow> data)
		{
			if (data.Count != rows.Count)
			{
				RebuildRows();
				return;
			}
			refreshing = true;
			try
			{
				for (int i = 0; i < data.Count; i++)
				{
					RutaPropRow r = data[i];
					RowControls rc = rows[i];
					rc.On.IsChecked = r.Enabled;
					if (!rc.Account.IsKeyboardFocusWithin && !rc.Account.IsDropDownOpen && rc.Account.Text != r.Account)
						rc.Account.Text = r.Account;
					if (!rc.Plan.IsDropDownOpen)
						rc.Plan.SelectedItem = r.PlanKey;
					rc.Status.Text = (r.IsNext ? "\u25BA " : "") + r.Status;
					rc.Status.Foreground = StatusBrush(r.Status);
					rc.ShownPnl = ShowEdit(rc.Pnl, r.Pnl, rc.ShownPnl);
					rc.ShownBest = ShowEdit(rc.Best, r.BestDay, rc.ShownBest);
					rc.ShownPeak = ShowEdit(rc.Peak, r.PeakEod, rc.ShownPeak);
					rc.ShownDays = ShowEdit(rc.Days, r.Days, rc.ShownDays);
					rc.Pnl.Foreground = r.Pnl > 0 ? Brushes.LimeGreen : r.Pnl < 0 ? Brushes.OrangeRed : rc.Days.Foreground;
					rc.Today.Text = RutaPropRouter.Money(r.DayPnl);
					rc.Floor.Text = RutaPropRouter.Money(r.Floor) + "  (room " + RutaPropRouter.Money(r.Room).TrimStart('+') + ")";
					rc.ToTarget.Text = r.ToTarget > 0 ? RutaPropRouter.Money(r.ToTarget).TrimStart('+') + " of " + RutaPropRouter.Money(r.Target).TrimStart('+') : "reached";
					rc.Trades.Text = r.Trades + " (" + r.Wins + " won)";
					rc.Cash.Text = r.Account.Length == 0 ? "" : !r.Connected ? "not connected" : double.IsNaN(r.Cash) ? "connected" : r.Cash.ToString("$#,##0.00", Inv);
					rc.Info.Text = r.OpenTrade.Length > 0 ? r.OpenTrade : r.Note;
					bool inTrade = r.OpenTrade.Length > 0;
					rc.Ready.IsEnabled = !inTrade && r.Status != "READY";
					rc.NewEval.IsEnabled = !inTrade;
					rc.Remove.IsEnabled = !inTrade;
				}
			}
			finally
			{
				refreshing = false;
			}
		}

		// Shows a book value in an editable cell unless the user is typing in it
		private static double ShowEdit(TextBox box, double value, double shown)
		{
			if (box.IsKeyboardFocusWithin)
				return shown;
			box.Text = Num(value);
			return value;
		}

		private static Brush StatusBrush(string status)
		{
			switch (status)
			{
				case "READY":			return Brushes.LimeGreen;
				case "IN TRADE":		return Brushes.DodgerBlue;
				case "DONE TODAY":
				case "CAN'T FIT":		return Brushes.Goldenrod;
				case "TARGET REACHED":	return Brushes.Gold;
				case "MAX LOSS HIT":	return Brushes.OrangeRed;
				case "OFF":
				case "NO ACCOUNT":
				case "ACCOUNT BUSY":	return Brushes.Gray;
				default:				return Brushes.Orange;		// CHECK ACCOUNT, NOT CONNECTED, OPEN POSITION
			}
		}
		#endregion

		#region Actions
		private void ShowError(string error)
		{
			if (!string.IsNullOrEmpty(error))
				MessageBox.Show(this, error, "Prop Account Manager", MessageBoxButton.OK, MessageBoxImage.Information);
			Refresh();
		}

		private bool Confirm(string text)
		{
			return MessageBox.Show(this, text, "Prop Account Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
		}

		private void ChangeSettings(Action<RutaPropSettings> change)
		{
			if (refreshing)
				return;
			RutaPropSettings st = router.GetSettings();
			change(st);
			router.SetSettings(st);
			Refresh();
		}

		private void CommitField(SettingField f)
		{
			if (refreshing)
				return;
			RutaPropSettings st = router.GetSettings();
			double v;
			if (TryNumber(f.Box.Text, out v) && v != f.Get(st))
			{
				f.Set(st, f.IsInt ? Math.Round(v) : v);
				router.SetSettings(st);
			}
			f.Box.Text = Num(f.Get(router.GetSettings()));
		}

		private void CommitSlot(RowControls rc, string selectedAccount)
		{
			if (refreshing)
				return;
			string account = selectedAccount ?? (rc.Account.Text ?? "");
			string plan = rc.Plan.SelectedItem as string ?? "Topstep50K";
			string error = router.SetSlot(rc.Index, rc.On.IsChecked == true, account.Trim(), plan);
			if (error != null)
			{
				refreshing = true;
				rc.Account.SelectedItem = null;
				refreshing = false;
				ShowError(error);
			}
		}

		private void CommitBook(RowControls rc)
		{
			if (refreshing)
				return;
			double pnl, best, peak, days;
			if (!TryNumber(rc.Pnl.Text, out pnl) || !TryNumber(rc.Best.Text, out best) || !TryNumber(rc.Peak.Text, out peak) || !TryNumber(rc.Days.Text, out days))
			{
				ShowError("Please type a number (for example 1250.50).");
				return;
			}
			if (pnl == rc.ShownPnl && best == rc.ShownBest && peak == rc.ShownPeak && days == rc.ShownDays)
				return;
			rc.ShownPnl = pnl;
			rc.ShownBest = best;
			rc.ShownPeak = peak;
			rc.ShownDays = days;
			ShowError(router.EditBook(rc.Index, pnl, peak, best, (int)Math.Round(days)));
		}

		private void FillAccounts(ComboBox box)
		{
			string current = box.Text;
			refreshing = true;
			box.Items.Clear();
			foreach (string name in router.BrokerAccounts())
				box.Items.Add(name);
			box.Text = current;
			refreshing = false;
		}

		private void OnModeClick(object sender, RoutedEventArgs e)
		{
			if (router.Mode == RutaPropMode.Live)
			{
				ShowError(router.SetMode(RutaPropMode.DryRun));
				return;
			}
			List<RutaPropRow> data = router.Rows();
			string list = string.Join(Environment.NewLine, data.Where(r => r.Enabled && r.Account.Length > 0)
				.Select(r => "     " + r.Account + "  (" + r.PlanName + ")" + (r.Connected ? "" : "  - NOT CONNECTED")).ToArray());
			if (!Confirm("Go LIVE?" + Environment.NewLine + Environment.NewLine
				+ "Trades from strategies with 'Route to Prop Account Manager' = ON will be sent as REAL orders to:" + Environment.NewLine + Environment.NewLine
				+ list + Environment.NewLine + Environment.NewLine
				+ "Each trade gets a real stop + target order. The manager keeps working when this window is closed: use 'Back to DRY RUN', 'Paused' or FLATTEN ALL to stop it."
				+ Environment.NewLine + Environment.NewLine + "Test it on NinjaTrader Sim accounts first, and check that your firms allow automated trading."))
				return;
			ShowError(router.SetMode(RutaPropMode.Live));
		}

		private void OnFlattenClick(object sender, RoutedEventArgs e)
		{
			if (Confirm("FLATTEN ALL: pause routing and close every trade the manager opened?"))
			{
				router.FlattenAll();
				Refresh();
			}
		}

		private void OnAddClick(object sender, RoutedEventArgs e)
		{
			ShowError(router.AddSlot("", "Topstep50K"));
		}

		private void OnResetDryClick(object sender, RoutedEventArgs e)
		{
			if (Confirm("Set every account's DRY RUN numbers back to 0? (Live numbers are kept.)"))
			{
				router.ResetDryRun();
				Refresh();
			}
		}

		private void OnNewEvalClick(RowControls rc)
		{
			string name = rc.Account.Text.Length > 0 ? rc.Account.Text : "this row";
			if (Confirm("Start a new evaluation on " + name + "? Its " + (router.Mode == RutaPropMode.Live ? "live" : "dry-run") + " numbers go back to 0."))
				ShowError(router.NewEvaluation(rc.Index));
		}

		private void OnRemoveClick(RowControls rc)
		{
			string name = rc.Account.Text.Length > 0 ? rc.Account.Text : "this row";
			if (Confirm("Remove " + name + " from the list? Its numbers are deleted."))
				ShowError(router.RemoveSlot(rc.Index));
		}
		#endregion

		#region Numbers
		private static string Num(double v)
		{
			return v.ToString("0.##", Inv);
		}

		// "1500", "1,500", "$1,500.50", "1500,5" (decimal comma)
		private static bool TryNumber(string text, out double value)
		{
			string t = (text ?? "").Replace("$", "").Replace(" ", "").Trim();
			int comma = t.LastIndexOf(',');
			if (comma >= 0)
			{
				bool thousands = t.IndexOf('.') > comma || (t.IndexOf('.') < 0 && t.Length - comma - 1 == 3);
				t = thousands ? t.Replace(",", "") : t.Replace(".", "").Replace(',', '.');
			}
			return double.TryParse(t, NumberStyles.Float, Inv, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
		}
		#endregion
	}
}
