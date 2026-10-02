// Fake WPF types for Verify.csproj, so the strategy runs on Linux (the real WPF assemblies only load on Windows).
// CompileCheck.csproj compiles the same files against the real WPF reference assemblies instead.
using System.Windows.Media;

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
			Magenta = new Brush("Magenta"), OrangeRed = new Brush("OrangeRed"), Transparent = new Brush("Transparent"), Goldenrod = new Brush("Goldenrod");
	}
}
namespace NinjaTrader.NinjaScript.DrawingTools
{
	internal static class StubBrush { public static string NameOf(Brush b) { return b.Name; } }
}
