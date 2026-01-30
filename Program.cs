using Biome2.App;

namespace Biome2;

internal static class Program {
	[STAThread]
	private static void Main() {
		// Ensure console output is unbuffered so early writes don't get coalesced.
		Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

		var config = AppConfig.CreateDefault();
		using var app = new BiomeApp(config);
		app.Run();
	}
}