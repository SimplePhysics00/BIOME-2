using System;
using System.IO;
using System.Globalization;

namespace Biome2.Diagnostics;

/// <summary>
/// Lightweight perf tracking hooks.
/// Later, feed these into an ImGui overlay.
/// </summary>
public sealed class Performance {
	public double LastUpdateSeconds { get; private set; }
	public double LastRenderSeconds { get; private set; }

    // Tick-per-second tracking
    public double CurrentTicksPerSecond { get; private set; }
    public double MaxTicksPerSecond { get; private set; }

    private long _tickWindowStart;
    private int _ticksThisWindow;

	private long _updateStart;
	private long _renderStart;

	public void BeginUpdate(double dt) => _updateStart = StopwatchTicks();
	public void EndUpdate() => LastUpdateSeconds = TicksToSeconds(StopwatchTicks() - _updateStart);

	public void BeginRender(double dt) => _renderStart = StopwatchTicks();
	public void EndRender() => LastRenderSeconds = TicksToSeconds(StopwatchTicks() - _renderStart);

    /// <summary>
    /// Record that the simulation performed one tick. Call this from the simulation stepping code.
    /// Performance will aggregate ticks per second and maintain a max value across the run.
    /// </summary>
    public void RecordTick()
    {
        var now = StopwatchTicks();
        if (_tickWindowStart == 0) {
            _tickWindowStart = now;
            _ticksThisWindow = 0;
        }

        _ticksThisWindow++;

        var elapsed = TicksToSeconds(now - _tickWindowStart);
        if (elapsed >= 1.0) {
            CurrentTicksPerSecond = _ticksThisWindow / elapsed;
            if (CurrentTicksPerSecond > MaxTicksPerSecond) MaxTicksPerSecond = CurrentTicksPerSecond;
            // reset window
            Logger.Info($"TPS: {CurrentTicksPerSecond:F2}");
			_tickWindowStart = now;
            _ticksThisWindow = 0;
        }
    }

    /// <summary>
    /// Persist the max TPS to a simple log file. Appends a line with: rulesFileName, timestamp, maxTPS.
    /// If rulesFileName is null or empty, writes "<none>".
    /// </summary>
    public void SaveMaxTps(string? rulesFilePath)
    {
        try {
            string fileName = string.IsNullOrEmpty(rulesFilePath) ? "<none>" : Path.GetFileName(rulesFilePath);
            string logLine = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}\t {1:yyyy-MM-dd HH:mm:ss}\t TPS: {2:F2}", fileName, DateTime.Now, MaxTicksPerSecond);
            string outPath = Path.Combine(AppContext.BaseDirectory ?? ".", "tps_stats.txt");
            File.AppendAllText(outPath, logLine + Environment.NewLine);
            Logger.Info($"Saved TPS stats to '{outPath}'.");
        } catch (Exception ex) {
            Logger.Error($"Failed to save TPS stats: {ex.Message}");
        }
    }

	private static long StopwatchTicks() => System.Diagnostics.Stopwatch.GetTimestamp();

	private static double TicksToSeconds(long ticks) {
		return ticks / (double) System.Diagnostics.Stopwatch.Frequency;
	}
}
