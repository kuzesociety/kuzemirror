// NinjaTrader types the Prop Account Manager window uses, for CompileCheck.csproj (real WPF, stubbed NinjaTrader).
using System.Windows.Media;

namespace NinjaTrader.Gui.Tools
{
	public class NTWindow : System.Windows.Window
	{
		public string Caption { get; set; }
	}
}
namespace NinjaTrader.NinjaScript
{
	public class AddOnBase : NinjaScriptBase
	{
		protected virtual void OnStateChange() { }
		protected virtual void OnWindowCreated(System.Windows.Window window) { }
		protected virtual void OnWindowDestroyed(System.Windows.Window window) { }
	}
}
namespace NinjaTrader.NinjaScript.DrawingTools
{
	internal static class StubBrush { public static string NameOf(Brush b) { return b.ToString(); } }
}
