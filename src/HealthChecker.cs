using Microsoft.Extensions.Logging;

namespace DPloy;

/// <summary>
/// Post-switch soak: polls the project's systemd units and fails fast if any unit reports
/// "failed", succeeds only if all units are "active" at the end of the soak window.
/// Catches the "deploy succeeded but shipped a broken service" case the old system couldn't.
/// </summary>
public class HealthChecker {

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<HealthChecker> _logger;

    public HealthChecker(ILogger<HealthChecker> logger) => _logger = logger;

    /// <returns>null when healthy, otherwise a human-readable failure reason.</returns>
    public async Task<string?> SoakAsync(IReadOnlyList<string> units, int soakSeconds,
                                         Action<string>? onStatus = null, CancellationToken ct = default) {
        if (units.Count == 0) return null;

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(soakSeconds);
        while (true) {
            foreach (var unit in units) {
                var state = await GetActiveStateAsync(unit, ct);
                if (state == "failed")
                    return $"unit `{unit}` entered **failed** state during soak";
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            onStatus?.Invoke($"soaking… {(int)remaining.TotalSeconds}s left, all units nominal");
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, ct);
        }

        // Final verdict: everything must be active (covers slow crash-loops still in "activating").
        foreach (var unit in units) {
            var state = await GetActiveStateAsync(unit, ct);
            if (state != "active")
                return $"unit `{unit}` is **{state ?? "unknown"}** after {soakSeconds}s soak (expected active)";
        }
        return null;
    }

    private async Task<string?> GetActiveStateAsync(string unit, CancellationToken ct) {
        var result = await ProcessRunner.RunAsync(
            "systemctl", ["show", "--property=ActiveState", "--value", unit],
            timeout: TimeSpan.FromSeconds(10), ct: ct);
        if (!result.Success) {
            _logger.LogWarning("systemctl show {Unit} failed: {Output}", unit, result.Output.Trim());
            return null;
        }
        return result.Output.Trim();
    }
}
