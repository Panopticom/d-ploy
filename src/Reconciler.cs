using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>
/// The heart of D-Ploy: a single-reader convergence loop. Commands, webhooks, and the
/// periodic timer only mutate desired state (StateStore) and kick the loop; the loop
/// compares desired vs deployed per project and converges. Single reader = concurrent
/// deploys serialize for free; durable state = a crash/restart resumes where it left off.
///
/// Converge order (git first — the infra repo is the source of truth):
///   clone infra → bump flake.lock → commit+push → switch → health soak
///   → on failure: rollback generation + revert the bump commit
/// </summary>
public class Reconciler : BackgroundService {

    private readonly DeployerConfig _config;
    private readonly StateStore _state;
    private readonly ProgressReporter _reporter;
    private readonly HealthChecker _health;
    private readonly ILogger<Reconciler> _logger;

    private readonly Channel<string> _kicks = Channel.CreateUnbounded<string>();

    public Reconciler(IOptions<DeployerConfig> config, StateStore state, ProgressReporter reporter,
                      HealthChecker health, ILogger<Reconciler> logger) {
        _config   = config.Value;
        _state    = state;
        _reporter = reporter;
        _health   = health;
        _logger   = logger;
    }

    /// <summary>Ask the loop to look at one project (or everything, when key is null) soon.</summary>
    public void Kick(string? projectKey = null) => _kicks.Writer.TryWrite(projectKey ?? "*");

    protected override async Task ExecuteAsync(CancellationToken ct) {
        // Give the Discord client a moment to connect so startup announcements land.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        await FinalizePendingSelfSwitchAsync(ct);
        Kick();

        var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _config.ReconcileIntervalMinutes)));
        var timerTask = Task.Run(async () => {
            while (await timer.WaitForNextTickAsync(ct)) {
                await CheckAutoUpdatesAsync(ct);
                Kick();
            }
        }, ct);

        await foreach (var key in _kicks.Reader.ReadAllAsync(ct)) {
            var keys = key == "*" ? _config.Projects.Keys.ToList() : new List<string> { key };
            foreach (var k in keys) {
                if (!_config.Projects.TryGetValue(k, out var project)) continue;
                try {
                    await ConvergeAsync(k, project, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    _logger.LogError(ex, "Converge failed for {Project}", k);
                    await _reporter.AnnounceAsync($"❌ **{project.DisplayName}**: converge crashed — `{ex.Message}`");
                }
            }
        }
    }

    // ── Auto-update: poll tags for projects with autoMode != off ─────────────

    private async Task CheckAutoUpdatesAsync(CancellationToken ct) {
        foreach (var (key, project) in _config.Projects) {
            var state = _state.Get(key);
            if (state.AutoMode == AutoMode.Off) continue;
            try {
                var latest = await GitRemote.GetLatestTagAsync(project.RepoUrl, ct);
                if (latest is null || latest == state.DeployedRef || latest == state.DesiredRef) continue;
                _state.Update(key, s => { s.DesiredRef = latest; s.UpdatedBy = "auto"; s.UpdatedAt = DateTimeOffset.UtcNow; });
                await _reporter.AnnounceAsync($"🔔 **{project.DisplayName}**: new release `{latest}` detected — deploying.");
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Auto-update tag check failed for {Project}", key);
            }
        }
    }

    // ── Converge one project ──────────────────────────────────────────────────

    private async Task ConvergeAsync(string key, ProjectConfig project, CancellationToken ct) {
        var state = _state.Get(key);
        var target = state.DesiredRef;
        if (target is null || target == state.DeployedRef) return;

        _logger.LogInformation("Converging {Project}: {From} → {To}", key, state.DeployedRef ?? "(unknown)", target);
        var progress = await _reporter.StartAsync($"**{project.DisplayName}** → `{target}` (was `{state.DeployedRef ?? "unknown"}`)");

        // 1. Fresh infra clone
        var workDir = Path.Combine(_config.DataPath, "work", key);
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);

        progress.Set("cloning infra repo…");
        var clone = await ProcessRunner.RunAsync("git", ["clone", "--depth", "1", _config.InfraRepo, "."],
            workDir, TimeSpan.FromMinutes(5), ct: ct);
        if (!clone.Success) { await AbortAsync(progress, key, "infra clone failed", clone.Output); return; }
        await ProcessRunner.RunAsync("git", ["config", "user.email", "d-ploy@localhost"], workDir, ct: ct);
        await ProcessRunner.RunAsync("git", ["config", "user.name", "D-Ploy"], workDir, ct: ct);

        // 2. Bump flake.lock to the target ref
        progress.Set($"updating flake.lock → `{target}`…");
        var overrideUrl = target == "HEAD"
            ? $"git+ssh://{ToSshUri(project.RepoUrl)}"
            : $"git+ssh://{ToSshUri(project.RepoUrl)}?ref={target}";
        var update = await ProcessRunner.RunAsync("nix",
            ["flake", "update", project.InfraInputName, "--override-input", project.InfraInputName, overrideUrl],
            workDir, TimeSpan.FromMinutes(5), ct: ct);
        if (!update.Success) { await AbortAsync(progress, key, "flake.lock update failed", update.Output); return; }

        // 3. Commit + push BEFORE switching — git is the source of truth. If the push
        //    fails we stop here: the running system stays consistent with the repo.
        var dirty = !(await ProcessRunner.RunAsync("git", ["diff", "--quiet", "flake.lock"], workDir, ct: ct)).Success;
        string? bumpCommit = null;
        if (dirty) {
            progress.Set("pushing flake.lock bump to infra repo…");
            var committed =
                (await ProcessRunner.RunAsync("git", ["add", "flake.lock"], workDir, ct: ct)).Success &&
                (await ProcessRunner.RunAsync("git", ["commit", "-m", $"d-ploy: {key} → {target}"], workDir, ct: ct)).Success &&
                (await ProcessRunner.RunAsync("git", ["push"], workDir, TimeSpan.FromMinutes(2), ct: ct)).Success;
            if (!committed) { await AbortAsync(progress, key, "flake.lock push failed — system unchanged", ""); return; }
            bumpCommit = (await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], workDir, ct: ct)).Output.Trim();
        }

        // 4. Switch. Self-updates restart this daemon, so they run detached and are
        //    finalized by FinalizePendingSelfSwitchAsync on next startup.
        if (project.SelfUpdate) {
            await StartDetachedSelfSwitchAsync(key, project, target, workDir, progress, ct);
            return;
        }

        progress.Set("running nixos-rebuild switch…");
        var lastLine = "";
        var sw = await ProcessRunner.RunAsync("sudo", [project.SwitchScriptPath, "switch", workDir],
            workDir, TimeSpan.FromMinutes(45),
            onOutputLine: line => { lastLine = line; progress.Set($"`switch`: {Sanitize(line)}"); }, ct: ct);
        if (!sw.Success) {
            // Build/switch failed — NixOS left the old generation running; undo the bump.
            await RevertBumpAsync(workDir, bumpCommit, ct);
            await AbortAsync(progress, key, "nixos-rebuild failed (old generation still running; bump reverted)", sw.Output);
            return;
        }

        // 5. Health soak
        progress.Set("switch done — health soak…");
        var failure = await _health.SoakAsync(project.HealthUnits, project.SoakSeconds,
            s => progress.Set(s), ct);

        if (failure is not null) {
            progress.Set("unhealthy — rolling back…");
            var rb = await ProcessRunner.RunAsync("sudo", [project.SwitchScriptPath, "rollback"],
                workDir, TimeSpan.FromMinutes(15), ct: ct);
            await RevertBumpAsync(workDir, bumpCommit, ct);
            _state.Update(key, s => s.DesiredRef = s.DeployedRef); // stop retrying a bad ref
            await progress.FailAsync(
                $"{failure}\nRolled back to previous generation ({(rb.Success ? "ok" : "**rollback also failed — check the host!**")}); flake.lock bump reverted.");
            return;
        }

        // 6. Success
        _state.Update(key, s => {
            s.PreviousRef = s.DeployedRef;
            s.DeployedRef = target;
            s.UpdatedAt   = DateTimeOffset.UtcNow;
        });
        TryCleanup(workDir);
        await progress.SucceedAsync($"Now running `{target}`." + (dirty ? " flake.lock bump pushed." : ""));
    }

    private async Task AbortAsync(ProgressReporter.Progress progress, string key,
                                  string reason, string output) {
        _state.Update(key, s => s.DesiredRef = s.DeployedRef); // require an explicit retry
        var tail = string.IsNullOrWhiteSpace(output) ? "" : $"\n```\n{ProcessRunner.Tail(output, 1200)}\n```";
        await progress.FailAsync($"{reason}{tail}");
    }

    private async Task RevertBumpAsync(string workDir, string? bumpCommit, CancellationToken ct) {
        if (bumpCommit is null) return;
        var ok =
            (await ProcessRunner.RunAsync("git", ["revert", "--no-edit", bumpCommit], workDir, ct: ct)).Success &&
            (await ProcessRunner.RunAsync("git", ["push"], workDir, TimeSpan.FromMinutes(2), ct: ct)).Success;
        if (!ok) _logger.LogWarning("Could not revert flake.lock bump {Commit} — repo is ahead of the running system", bumpCommit);
    }

    // ── Self-update: detached switch + marker finalized on next startup ──────

    private async Task StartDetachedSelfSwitchAsync(string key, ProjectConfig project, string target,
                                                    string workDir, ProgressReporter.Progress progress, CancellationToken ct) {
        File.WriteAllText(PendingMarkerPath(key), target);
        var run = await ProcessRunner.RunAsync("sudo",
            ["systemd-run", "--no-block", "--unit", $"d-ploy-self-switch",
             "--property=SyslogIdentifier=d-ploy-self-switch",
             project.SwitchScriptPath, "switch", workDir],
            workDir, TimeSpan.FromMinutes(2), ct: ct);
        if (!run.Success) {
            File.Delete(PendingMarkerPath(key));
            await AbortAsync(progress, key, "could not schedule detached self-switch", run.Output);
            return;
        }
        progress.Set("switch running detached — D-Ploy will restart if its own unit changed…");

        // Watch the transient unit from THIS process. Three outcomes:
        //  - our unit changed → we get restarted; the startup marker path reports success.
        //  - switch finished but our unit didn't change (bump touched nothing of ours) →
        //    promote here, since no restart will come.
        //  - switch failed → we are still alive to report it; without this, the marker
        //    would sit silent until some unrelated restart and then wrongly promote.
        _ = Task.Run(() => MonitorDetachedSelfSwitchAsync(key, target, progress), CancellationToken.None);
    }

    private async Task MonitorDetachedSelfSwitchAsync(string key, string target, ProgressReporter.Progress progress) {
        await Task.Delay(TimeSpan.FromSeconds(10));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(45);

        while (DateTimeOffset.UtcNow < deadline) {
            var show = await ProcessRunner.RunAsync("systemctl",
                ["show", "--property=ActiveState,Result", "d-ploy-self-switch.service"],
                timeout: TimeSpan.FromSeconds(10));
            var props = show.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1]);
            var active = props.GetValueOrDefault("ActiveState", "");
            var result = props.GetValueOrDefault("Result", "");

            if (active == "failed") {
                File.Delete(PendingMarkerPath(key));
                _state.Update(key, s => s.DesiredRef = s.DeployedRef);
                await progress.FailAsync("self-switch unit **failed** — old version still running. See `journalctl -u d-ploy-self-switch`.");
                return;
            }
            if (active == "inactive" && result == "success") {
                // Switch completed without restarting us (our own unit was unchanged).
                File.Delete(PendingMarkerPath(key));
                _state.Update(key, s => {
                    s.PreviousRef = s.DeployedRef;
                    s.DeployedRef = target;
                    s.UpdatedAt   = DateTimeOffset.UtcNow;
                });
                await progress.SucceedAsync($"Switch complete (no restart needed). Now running `{target}`.");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        await progress.FailAsync("timed out watching the detached self-switch — check `journalctl -u d-ploy-self-switch`.");
    }

    private async Task FinalizePendingSelfSwitchAsync(CancellationToken ct) {
        foreach (var (key, project) in _config.Projects) {
            var marker = PendingMarkerPath(key);
            if (!File.Exists(marker)) continue;
            var target = (await File.ReadAllTextAsync(marker, ct)).Trim();
            File.Delete(marker);

            // We're running again — the switch finished (or was rolled back by hand).
            // Soak our own health units (if any) and promote state.
            var failure = await _health.SoakAsync(project.HealthUnits, Math.Min(project.SoakSeconds, 30), null, ct);
            if (failure is null) {
                _state.Update(key, s => {
                    s.PreviousRef = s.DeployedRef;
                    s.DeployedRef = target;
                    s.DesiredRef  = target;
                    s.UpdatedAt   = DateTimeOffset.UtcNow;
                });
                await _reporter.AnnounceAsync($"✅ **{project.DisplayName}** self-update complete — back up, now running `{target}`.");
            } else {
                _state.Update(key, s => s.DesiredRef = s.DeployedRef);
                await _reporter.AnnounceAsync($"⚠️ **{project.DisplayName}** restarted after self-update to `{target}`, but: {failure}");
            }
        }
    }

    private string PendingMarkerPath(string key) => Path.Combine(_config.DataPath, $".pending-switch-{key}");

    // ── Small helpers ─────────────────────────────────────────────────────────

    /// <summary>git@github.com:Org/repo → git@github.com/Org/repo (flake URL form).</summary>
    private static string ToSshUri(string sshUrl) {
        var s = sshUrl;
        if (s.StartsWith("git+ssh://")) s = s["git+ssh://".Length..];
        if (s.StartsWith("ssh://")) s = s["ssh://".Length..];
        var colon = s.IndexOf(':');
        if (colon > 0 && !s[..colon].Contains('/')) s = s[..colon] + "/" + s[(colon + 1)..];
        if (s.EndsWith(".git")) s = s[..^4];
        return s;
    }

    private static string Sanitize(string line) => line.Length > 160 ? line[..160] + "…" : line;

    private void TryCleanup(string workDir) {
        try { Directory.Delete(workDir, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "work dir cleanup failed"); }
    }
}
