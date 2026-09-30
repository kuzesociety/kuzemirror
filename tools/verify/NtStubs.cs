// Minimal stand-ins for the NinjaTrader 8 / WPF types RutaCryptoMirror.cs uses, with the same
// signatures as the real API. They let the strategy compile (as C# 5, like NinjaTrader 8) and run
// on Linux for automated checks. They are NOT shipped to NinjaTrader.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;

namespace System.Windows
{
	public enum TextAlignment { Left, Right, Center, Justify }
}
namespace System.Windows.Input { internal class StubMarker { } }
namespace System.Windows.Media
{
	public class Brush { public string Name; public Brush(string name) { Name = name; } }
	public static class Brushes
	{
		public static readonly Brush Black = new Brush("Black"), White = new Brush("White"), Gray = new Brush("Gray"),
			Red = new Brush("Red"), LimeGreen = new Brush("LimeGreen"), DodgerBlue = new Brush("DodgerBlue"),
			Magenta = new Brush("Magenta"), OrangeRed = new Brush("OrangeRed"), Transparent = new Brush("Transparent");
	}
}
namespace NinjaTrader.Cbi
{
	public enum TimeInForce { Day, Gtc }
	public enum MarketPosition { Flat, Long, Short }
	public class MasterInstrument { public double PointValue = 20; public double TickSize = 0.25; }
	public class Instrument
	{
		private readonly MasterInstrument mi = new MasterInstrument();
		public MasterInstrument MasterInstrument { get { return mi; } }
		public string FullName { get { return "NQ 12-26"; } }
	}
}
namespace NinjaTrader.Gui
{
	public enum DashStyleHelper { Dash, DashDot, DashDotDot, Dot, Solid }
	public enum PlotStyle { Bar, Block, Cross, Dot, Hash, HLine, Line, Square, TriangleDown, TriangleUp }
	public class Stroke
	{
		public Stroke(Brush brush) { }
		public Stroke(Brush brush, float width) { }
		public Stroke(Brush brush, DashStyleHelper dashStyleHelper, float width) { }
	}
}
namespace NinjaTrader.Gui.Chart { internal class StubMarker { } }
namespace NinjaTrader.Gui.SuperDom { internal class StubMarker { } }
namespace NinjaTrader.Gui.Tools
{
	public class SimpleFont
	{
		public SimpleFont(string familySerialize, double size) { }
		public bool Bold { get; set; }
	}
}
namespace NinjaTrader.Data
{
	public enum BarsPeriodType { Tick, Volume, Range, Second, Minute, Day, Week, Month, Year }
	public class BarsPeriod { public BarsPeriodType BarsPeriodType { get; set; } public int Value { get; set; } }
	public class Bars { public bool IsTickReplay { get; set; } public int Count { get; set; } }
}
namespace NinjaTrader.Core
{
	public static class Globals { public static string UserDataDir { get { return System.IO.Path.GetTempPath(); } } }
}
namespace NinjaTrader.Core.FloatingPoint { internal class StubMarker { } }
namespace NinjaTrader.NinjaScript.Indicators { internal class StubMarker { } }
namespace NinjaTrader.NinjaScript
{
	public enum State { SetDefaults, Configure, Active, DataLoaded, Historical, Transition, Realtime, Terminated, Finalized }
	public enum Calculate { OnBarClose, OnEachTick, OnPriceChange }
	public enum EntryHandling { AllEntries, UniqueEntries }
	public enum MaximumBarsLookBack { TwoHundredFiftySix, Infinite }
	public enum OrderFillResolution { Standard, High }
	public enum StartBehavior { AdoptAccountPosition, ImmediatelySubmit, ImmediatelySubmitSynchronizeAccount, WaitUntilFlat, WaitUntilFlatSynchronizeAccount }
	public enum RealtimeErrorHandling { IgnoreAllErrors, StopCancelClose, StopCancelCloseIgnoreRejects }
	public enum StopTargetHandling { ByStrategyPosition, PerEntryExecution }

	[AttributeUsage(AttributeTargets.Property)]
	public class NinjaScriptPropertyAttribute : Attribute { }

	public interface ISeries<T> { T this[int barsAgo] { get; } }

	// Series indexed "bars ago" relative to the owner's CurrentBar
	public class Series<T> : ISeries<T>
	{
		private readonly NinjaScriptBase owner;
		public readonly Dictionary<int, T> Store = new Dictionary<int, T>();
		public Series(NinjaScriptBase owner) { this.owner = owner; }
		public T this[int barsAgo]
		{
			get { return Store[owner.CurrentBar - barsAgo]; }
			set { Store[owner.CurrentBar - barsAgo] = value; }
		}
		public void Reset(int barsAgo) { Store.Remove(owner.CurrentBar - barsAgo); }
	}

	public class ListSeries<T> : ISeries<T>
	{
		private readonly NinjaScriptBase owner;
		private readonly List<T> data;
		public ListSeries(NinjaScriptBase owner, List<T> data) { this.owner = owner; this.data = data; }
		public T this[int barsAgo] { get { return data[owner.CurrentBar - barsAgo]; } }
	}

	public class NinjaScriptBase
	{
		public State State { get; set; }
		public Calculate Calculate { get; set; }
		public string Name { get; set; }
		public string Description { get; set; }
		public int CurrentBar { get; set; }
		public int BarsInProgress { get; set; }
		public bool IsFirstTickOfBar { get; set; }
		public double TickSize { get { return Instrument.MasterInstrument.TickSize; } }

		private readonly Instrument instrument = new Instrument();
		public Instrument Instrument { get { return instrument; } }
		private readonly Bars bars = new Bars();
		public Bars Bars { get { return bars; } }
		private readonly BarsPeriod barsPeriod = new BarsPeriod();
		public BarsPeriod BarsPeriod { get { return barsPeriod; } }

		public readonly List<DateTime> TimeData = new List<DateTime>();
		public readonly List<double> OpenData = new List<double>(), HighData = new List<double>(), LowData = new List<double>(),
			CloseData = new List<double>(), VolumeData = new List<double>();
		public ISeries<DateTime> Time { get { return new ListSeries<DateTime>(this, TimeData); } }
		public ISeries<double> Open { get { return new ListSeries<double>(this, OpenData); } }
		public ISeries<double> High { get { return new ListSeries<double>(this, HighData); } }
		public ISeries<double> Low { get { return new ListSeries<double>(this, LowData); } }
		public ISeries<double> Close { get { return new ListSeries<double>(this, CloseData); } }
		public ISeries<double> Volume { get { return new ListSeries<double>(this, VolumeData); } }

		private readonly List<Series<double>> values = new List<Series<double>>();
		public Series<double>[] Values { get { return values.ToArray(); } }
		public readonly List<string> PlotNames = new List<string>();
		public void AddPlot(Stroke stroke, PlotStyle plotStyle, string name) { values.Add(new Series<double>(this)); PlotNames.Add(name); }

		public readonly List<string> Printed = new List<string>();
		public void Print(string value) { Printed.Add(value); }
		public void Print(object value) { Printed.Add(Convert.ToString(value)); }
	}

	public class DrawingLog
	{
		public static readonly List<string> Calls = new List<string>();
	}
}
namespace NinjaTrader.NinjaScript.Strategies
{
	public class Strategy : NinjaScriptBase
	{
		public int EntriesPerDirection { get; set; }
		public EntryHandling EntryHandling { get; set; }
		public bool IsExitOnSessionCloseStrategy { get; set; }
		public int ExitOnSessionCloseSeconds { get; set; }
		public bool IsFillLimitOnTouch { get; set; }
		public MaximumBarsLookBack MaximumBarsLookBack { get; set; }
		public OrderFillResolution OrderFillResolution { get; set; }
		public int Slippage { get; set; }
		public StartBehavior StartBehavior { get; set; }
		public TimeInForce TimeInForce { get; set; }
		public bool TraceOrders { get; set; }
		public RealtimeErrorHandling RealtimeErrorHandling { get; set; }
		public StopTargetHandling StopTargetHandling { get; set; }
		public int BarsRequiredToTrade { get; set; }
		public bool IsInstantiatedOnEachOptimizationIteration { get; set; }

		public readonly List<string> Orders = new List<string>();
		public void EnterLong(int quantity, string signalName) { Orders.Add(CurrentBar + " EnterLong " + quantity + " " + signalName); }
		public void EnterShort(int quantity, string signalName) { Orders.Add(CurrentBar + " EnterShort " + quantity + " " + signalName); }
		public void ExitLong(string signalName, string fromEntrySignal) { Orders.Add(CurrentBar + " ExitLong " + signalName + " " + fromEntrySignal); }
		public void ExitShort(string signalName, string fromEntrySignal) { Orders.Add(CurrentBar + " ExitShort " + signalName + " " + fromEntrySignal); }

		protected virtual void OnStateChange() { }
		protected virtual void OnBarUpdate() { }
	}
}
namespace NinjaTrader.NinjaScript.DrawingTools
{
	public enum TextPosition { BottomLeft, BottomRight, Center, TopLeft, TopRight }
	public class Text { }
	public class TextFixed { }
	public class Diamond { }
	public class Line { }

	public static class Draw
	{
		public static Text Text(NinjaScriptBase owner, string tag, bool isAutoScale, string text, int barsAgo, double y, int yPixelOffset,
			Brush textBrush, SimpleFont font, TextAlignment alignment, Brush outlineBrush, Brush areaBrush, int areaOpacity)
		{ DrawingLog.Calls.Add("Text " + tag + " " + text.Replace("\n", "|")); return null; }

		public static TextFixed TextFixed(NinjaScriptBase owner, string tag, string text, TextPosition textPosition, Brush textBrush,
			SimpleFont font, Brush outlineBrush, Brush areaBrush, int areaOpacity)
		{ DrawingLog.Calls.Add("TextFixed " + tag + " " + text.Replace("\n", "|")); return null; }

		public static Diamond Diamond(NinjaScriptBase owner, string tag, bool isAutoScale, int barsAgo, double y, Brush brush)
		{ DrawingLog.Calls.Add("Diamond " + tag + " " + y); return null; }

		public static Line Line(NinjaScriptBase owner, string tag, bool isAutoScale, DateTime startTime, double startY, DateTime endTime,
			double endY, Brush brush, DashStyleHelper dashStyle, int width)
		{ DrawingLog.Calls.Add("Line " + tag); return null; }
	}
}
