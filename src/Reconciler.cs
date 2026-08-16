using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>
/// The heart of D-Ploy: a single-reader convergence loop. Commands, webhooks, and the
/// periodic timer only mutate desired state (StateStore) and kick the loop; the loop
/// compares desired vs deployed per project and converges. Single reader = concurrent
/// deploys serialize for free — no two `nixos-rebuild switch` invocations ever run at the
/// same time, even when kicked simultaneously (including a "*" kick touching every project
/// at once) — as long as ConvergeBatchAsync always fully awaits its own switch before
/// returning. The self-update path (StartDetachedSelfSwitchAsync) is the one place this took
/// real care to get right: the switch itself runs in a detached systemd unit so it survives
/// D-Ploy's own restart, but this loop still AWAITS its resolution
/// (MonitorDetachedSelfSwitchAsync) rather than treating "detached from our process" as
/// "detached from the loop" — otherwise the very next queued project would run its own
/// switch concurrently with it. Durable state = a crash/restart resumes where it left off.
///
/// Batching: when more than one project is due for convergence at once (a "*" kick, or
/// several commands/webhooks queued close together), projects that share a NixosAttr — i.e.
/// switching the same host config — are converged together: one clone, one set of flake.lock
/// bumps, one commit, one nixos-rebuild switch, one health soak over the union of their
/// HealthUnits. This trades per-project failure isolation for fewer, faster convergence
/// passes: a NixOS generation switch is atomic, so if the soak fails, every project in that
/// batch rolls back together, even ones that were individually healthy — there's no such
/// thing as a partial rollback of one project's contribution to a shared generation. See
/// ConvergeBatchAsync.
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

    /// <summary>A project that's due for convergence: the ref it should move to, and its
    /// currently-deployed ref (captured once, up front, so a batch's progress title and
    /// success/failure messages are consistent even though StateStore is mutated as each
    /// project promotes).</summary>
    private sealed record PendingProject(string Key, ProjectConfig Project, string Target, string? DeployedRef);

    protected override async Task ExecuteAsync(CancellationToken ct) {
        // Give the Discord client a moment to connect so startup announcements land.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        await FinalizePendingSelfSwitchAsync(ct);
        Kick();

        var hasSchedule = !string.IsNullOrWhiteSpace(_config.UpdateCheckSchedule);

        // The reconcile timer is a cheap safety net (compares desired vs deployed; a no-op
        // when nothing's pending) and, when no UpdateCheckSchedule is set, also drives the
        // release-check tag poll — same as before this existed.
        var reconcileTimer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _config.ReconcileIntervalMinutes)));
        var reconcileTask = Task.Run(async () => {
            while (await reconcileTimer.WaitForNextTickAsync(ct)) {
                if (!hasSchedule) await CheckAutoUpdatesAsync(ct);
                Kick();
            }
        }, ct);

        // UpdateCheckSchedule set: release checks run on their own systemd-calendar schedule
        // instead — e.g. so it doesn't depend on a GitHub webhook being wired up.
        var scheduleTask = hasSchedule
            ? Task.Run(() => RunScheduledUpdateChecksAsync(_config.UpdateCheckSchedule!, ct), ct)
            : Task.CompletedTask;

        await foreach (var key in _kicks.Reader.ReadAllAsync(ct)) {
            var keys = key == "*" ? _config.Projects.Keys.ToList() : new List<string> { key };

            var pending = new List<PendingProject>();
            foreach (var k in keys) {
                if (!_config.Projects.TryGetValue(k, out var project)) continue;
                var state = _state.Get(k);
                if (state.DesiredRef is null || state.DesiredRef == state.DeployedRef) continue;
                pending.Add(new PendingProject(k, project, state.DesiredRef, state.DeployedRef));
            }

            // Batch same-NixosAttr projects (same host config) into one converge pass each;
            // different attrs can never share a switch, so they always get their own batch.
            foreach (var batch in pending.GroupBy(p => p.Project.NixosAttr).Select(g => g.ToList())) {
                try {
                    await ConvergeBatchAsync(batch, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    var names = string.Join(", ", batch.Select(p => $"**{p.Project.DisplayName}**"));
                    _logger.LogError(ex, "Converge batch failed for {Projects}", string.Join(",", batch.Select(p => p.Key)));
                    await _reporter.AnnounceAsync($"❌ {names}: converge crashed — `{ex.Message}`");
                }
            }
        }
    }

    // ── Update-check schedule: systemd-calendar-driven alternative to the reconcile
    //    timer, for release checks that shouldn't depend on a GitHub webhook ─────────

    private async Task RunScheduledUpdateChecksAsync(string schedule, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            var next = await GetNextCalendarElapseAsync(schedule, ct);
            if (next is null) {
                _logger.LogError("Invalid Deployer:UpdateCheckSchedule {Schedule} — release checks disabled until fixed", schedule);
                await _reporter.AnnounceAsync(
                    $"⚠️ `Deployer:UpdateCheckSchedule` (`{schedule}`) isn't a valid systemd calendar " +
                    "expression — release checks are disabled until this is fixed and D-Ploy restarts.");
                return; // don't spin-loop on a permanently broken expression
            }

            var delay = next.Value - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
            }
            await CheckAutoUpdatesAsync(ct);
        }
    }

    /// <summary>Next occurrence of a systemd OnCalendar expression, via `systemd-analyze
    /// calendar`. Runs with the host's ambient timezone (no TZ override) so a schedule like
    /// "Mon 12:30" means 12:30 in the host's configured local time (NixOS `time.timeZone`),
    /// not UTC — then reads the "(in UTC):" line systemd-analyze always prints alongside
    /// "Next elapse:", which is unambiguous regardless of what timezone that elapse is in.
    /// Null = the expression is invalid or systemd-analyze isn't available.</summary>
    private async Task<DateTimeOffset?> GetNextCalendarElapseAsync(string schedule, CancellationToken ct) {
        var result = await ProcessRunner.RunAsync("systemd-analyze", ["calendar", schedule],
            timeout: TimeSpan.FromSeconds(10), ct: ct);
        if (!result.Success) {
            _logger.LogWarning("systemd-analyze calendar {Schedule} failed: {Output}", schedule, result.Output.Trim());
            return null;
        }

        foreach (var rawLine in result.Output.Split('\n')) {
            var line = rawLine.Trim();
            if (!line.StartsWith("(in UTC):")) continue;

            var value = line["(in UTC):".Length..].Trim(); // "Sat 2026-08-16 03:00:00 UTC"
            if (value.EndsWith(" UTC")) value = value[..^" UTC".Length];
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            var dateTimePart = $"{parts[^2]} {parts[^1]}"; // drop the leading day-of-week token

            return DateTime.TryParseExact(dateTimePart, "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt)
                ? new DateTimeOffset(dt, TimeSpan.Zero)
                : null;
        }
        return null;
    }

    // ── Auto-update: poll tags for projects with autoMode != off ─────────────

    private async Task CheckAutoUpdatesAsync(CancellationToken ct) {
        foreach (var (key, project) in _config.Projects) {
            var state = _state.Get(key);
            if (state.AutoMode == AutoMode.Off) continue;
            try {
                var latest = await GitRemote.GetLatestTagAsync(project.RepoUrl, ct);
                if (latest is null || latest == state.DeployedRef || latest == state.DesiredRef) continue;

                // Ask mode: prompt instead of deploying, and only once per release —
                // AskedRef tracks the last one we already posted buttons for.
                if (state.AutoMode == AutoMode.Ask) {
                    if (latest == state.AskedRef) continue;
                    _state.Update(key, s => s.AskedRef = latest);
                    await _reporter.AskAsync(key, project, latest);
                    continue;
                }

                _state.Update(key, s => { s.DesiredRef = latest; s.UpdatedBy = "auto"; s.UpdatedAt = DateTimeOffset.UtcNow; });
                await _reporter.AnnounceAsync($"🔔 **{project.DisplayName}**: new release `{latest}` detected — deploying.");
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Auto-update tag check failed for {Project}", key);
            }
        }
    }

    // ── Converge a batch of projects (1 or more, always the same NixosAttr) ───

    private async Task ConvergeBatchAsync(List<PendingProject> batch, CancellationToken ct) {
        var batchId = string.Join("+", batch.Select(p => p.Key));
        _logger.LogInformation("Converging batch [{Batch}]: {Details}", batchId,
            string.Join(", ", batch.Select(p => $"{p.Key} {p.DeployedRef ?? "(unknown)"} → {p.Target}")));

        var title = string.Join(", ", batch.Select(p => $"**{p.Project.DisplayName}** → `{p.Target}` (was `{p.DeployedRef ?? "unknown"}`)"));
        var progress = await _reporter.StartAsync(title);

        // 1. Fresh infra clone — one clone shared by every project in the batch.
        var workDir = Path.Combine(_config.DataPath, "work", batchId);
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);

        progress.Set("cloning infra repo…");
        var clone = await ProcessRunner.RunAsync("git", ["clone", "--depth", "1", _config.InfraRepo, "."],
            workDir, TimeSpan.FromMinutes(5), ct: ct);
        if (!clone.Success) { await AbortBatchAsync(progress, batch, "infra clone failed", clone.Output); return; }
        await ProcessRunner.RunAsync("git", ["config", "user.email", "d-ploy@localhost"], workDir, ct: ct);
        await ProcessRunner.RunAsync("git", ["config", "user.name", "D-Ploy"], workDir, ct: ct);

        // 2. Bump each project's flake input — one `nix flake update` call per project (same
        //    proven single-input form as before batching existed), all landing in the same
        //    flake.lock ahead of a single combined commit below.
        foreach (var p in batch) {
            progress.Set($"updating flake.lock → `{p.Key}` = `{p.Target}`…");
            var overrideUrl = p.Target == "HEAD"
                ? $"git+ssh://{ToSshUri(p.Project.RepoUrl)}"
                : $"git+ssh://{ToSshUri(p.Project.RepoUrl)}?ref={p.Target}";
            var update = await ProcessRunner.RunAsync("nix",
                ["flake", "update", p.Project.InfraInputName, "--override-input", p.Project.InfraInputName, overrideUrl],
                workDir, TimeSpan.FromMinutes(5), ct: ct);
            if (!update.Success) { await AbortBatchAsync(progress, batch, $"flake.lock update failed for `{p.Key}`", update.Output); return; }
        }

        // 3. Commit + push BEFORE switching — git is the source of truth. If the push
        //    fails we stop here: the running system stays consistent with the repo.
        var dirty = !(await ProcessRunner.RunAsync("git", ["diff", "--quiet", "flake.lock"], workDir, ct: ct)).Success;
        string? bumpCommit = null;
        if (dirty) {
            progress.Set("pushing flake.lock bump to infra repo…");
            var message = batch.Count == 1
                ? $"d-ploy: {batch[0].Key} → {batch[0].Target}"
                : $"d-ploy: batch update ({string.Join(", ", batch.Select(p => $"{p.Key} → {p.Target}"))})";
            var committed =
                (await ProcessRunner.RunAsync("git", ["add", "flake.lock"], workDir, ct: ct)).Success &&
                (await ProcessRunner.RunAsync("git", ["commit", "-m", message], workDir, ct: ct)).Success &&
                (await ProcessRunner.RunAsync("git", ["push"], workDir, TimeSpan.FromMinutes(2), ct: ct)).Success;
            if (!committed) { await AbortBatchAsync(progress, batch, "flake.lock push failed — system unchanged", ""); return; }
            bumpCommit = (await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], workDir, ct: ct)).Output.Trim();
        }

        // 4. Switch. If ANY project in the batch is the self-update project, the switch might
        //    restart d-ploy's own unit, so the WHOLE batch runs detached — every project in
        //    it is finalized together, on next startup or by the monitor below.
        var selfUpdate = batch.FirstOrDefault(p => p.Project.SelfUpdate);
        if (selfUpdate is not null) {
            await StartDetachedSelfSwitchAsync(batch, selfUpdate.Project.SwitchScriptPath, workDir, progress, ct);
            return;
        }

        progress.Set("running nixos-rebuild switch…");
        var switchScript = batch[0].Project.SwitchScriptPath; // same NixosAttr ⇒ same effective switch
        var sw = await ProcessRunner.RunAsync("sudo", [switchScript, "switch", workDir],
            workDir, TimeSpan.FromMinutes(45),
            onOutputLine: line => progress.Set($"`switch`: {Sanitize(line)}"), ct: ct);
        if (!sw.Success) {
            // Build/switch failed — NixOS left the old generation running; undo the bump.
            await RevertBumpAsync(workDir, bumpCommit, ct);
            await AbortBatchAsync(progress, batch, "nixos-rebuild failed (old generation still running; bump reverted)", sw.Output);
            return;
        }

        // 5. Health soak — union of every batched project's health units; the longest
        //    requested soak wins, since they're all watching the same activation now.
        progress.Set("switch done — health soak…");
        var healthUnits = batch.SelectMany(p => p.Project.HealthUnits).Distinct().ToList();
        var soakSeconds = batch.Max(p => p.Project.SoakSeconds);
        var failure = await _health.SoakAsync(healthUnits, soakSeconds, s => progress.Set(s), ct);

        if (failure is not null) {
            // A shared generation can't be partially rolled back — every project in the
            // batch reverts together, even ones whose own health units were fine.
            progress.Set("unhealthy — rolling back…");
            var rb = await ProcessRunner.RunAsync("sudo", [switchScript, "rollback"],
                workDir, TimeSpan.FromMinutes(15), ct: ct);
            await RevertBumpAsync(workDir, bumpCommit, ct);
            foreach (var p in batch) _state.Update(p.Key, s => s.DesiredRef = s.DeployedRef); // stop retrying a bad ref
            await progress.FailAsync(
                $"{failure}\nRolled back to previous generation ({(rb.Success ? "ok" : "**rollback also failed — check the host!**")}); flake.lock bump reverted.");
            return;
        }

        // 6. Success — promote every batched project.
        foreach (var p in batch) {
            _state.Update(p.Key, s => {
                s.PreviousRef = s.DeployedRef;
                s.DeployedRef = p.Target;
                s.UpdatedAt   = DateTimeOffset.UtcNow;
            });
        }
        TryCleanup(workDir);
        var doneText = string.Join(", ", batch.Select(p => $"**{p.Project.DisplayName}** `{p.Target}`"));
        await progress.SucceedAsync($"Now running {doneText}." + (dirty ? " flake.lock bump pushed." : ""));
    }

    private async Task AbortBatchAsync(ProgressReporter.Progress progress, List<PendingProject> batch,
                                       string reason, string output) {
        foreach (var p in batch) _state.Update(p.Key, s => s.DesiredRef = s.DeployedRef); // require an explicit retry
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
    //
    // A single pending-switch marker file (not one per project) is enough: converges are
    // fully serialized, so there's never more than one switch in flight at a time — the
    // marker just needs to remember which project(s) were part of whichever one was.

    private sealed record PendingSwitchMarker(Dictionary<string, string> Targets);

    private async Task StartDetachedSelfSwitchAsync(List<PendingProject> batch, string switchScript,
                                                    string workDir, ProgressReporter.Progress progress, CancellationToken ct) {
        WritePendingMarker(batch);
        var run = await ProcessRunner.RunAsync("sudo",
            ["systemd-run", "--no-block", "--unit", "d-ploy-self-switch",
             "--property=SyslogIdentifier=d-ploy-self-switch",
             switchScript, "switch", workDir],
            workDir, TimeSpan.FromMinutes(2), ct: ct);
        if (!run.Success) {
            DeletePendingMarker();
            await AbortBatchAsync(progress, batch, "could not schedule detached self-switch", run.Output);
            return;
        }
        progress.Set("switch running detached — D-Ploy will restart if its own unit changed…");

        // Watch the transient unit from THIS process, and — critically — AWAITED, not
        // fire-and-forget: "detached" here only means the switch survives OUR OWN process
        // restart, not that the reconciler loop should move on to another batch while it's
        // still running. Three outcomes:
        //  - our unit changed → we get restarted (this await never returns; that's fine —
        //    FinalizePendingSelfSwitchAsync on the next startup finalizes from the marker,
        //    exactly as if this had been fire-and-forget).
        //  - switch finished but our unit didn't change (bump touched nothing of ours) →
        //    promote here (after a real health soak — see PromoteAfterSelfSwitchAsync),
        //    since no restart will come.
        //  - switch failed → we are still alive to report it; without this, the marker
        //    would sit silent until some unrelated restart and then wrongly promote.
        // ct is honored throughout so a genuine shutdown (not caused by the switch itself)
        // doesn't block for up to the full 45-minute deadline below.
        await MonitorDetachedSelfSwitchAsync(batch, progress, ct);
    }

    private async Task MonitorDetachedSelfSwitchAsync(List<PendingProject> batch, ProgressReporter.Progress progress, CancellationToken ct) {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(45);

        while (DateTimeOffset.UtcNow < deadline) {
            var show = await ProcessRunner.RunAsync("systemctl",
                ["show", "--property=ActiveState,Result", "d-ploy-self-switch.service"],
                timeout: TimeSpan.FromSeconds(10), ct: ct);
            var props = show.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1]);
            var active = props.GetValueOrDefault("ActiveState", "");
            var result = props.GetValueOrDefault("Result", "");

            if (active == "failed") {
                DeletePendingMarker();
                foreach (var p in batch) _state.Update(p.Key, s => s.DesiredRef = s.DeployedRef);
                await progress.FailAsync("self-switch unit **failed** — old version still running. See `journalctl -u d-ploy-self-switch`.");
                return;
            }
            if (active == "inactive" && result == "success") {
                // Switch completed without restarting us (our own unit was unchanged).
                await PromoteAfterSelfSwitchAsync(batch, progress, ct);
                DeletePendingMarker();
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        await progress.FailAsync("timed out watching the detached self-switch — check `journalctl -u d-ploy-self-switch`.");
    }

    /// <summary>Shared by MonitorDetachedSelfSwitchAsync's "no restart needed" case and
    /// FinalizePendingSelfSwitchAsync's "we got restarted" case: soak the union of the
    /// batch's health units before promoting, same as a normal (non-self) batch would.</summary>
    private async Task PromoteAfterSelfSwitchAsync(List<PendingProject> batch, ProgressReporter.Progress? progress, CancellationToken ct, int? soakCap = null) {
        var healthUnits = batch.SelectMany(p => p.Project.HealthUnits).Distinct().ToList();
        var soakSeconds = batch.Max(p => p.Project.SoakSeconds);
        if (soakCap is not null) soakSeconds = Math.Min(soakSeconds, soakCap.Value);

        var failure = await _health.SoakAsync(healthUnits, soakSeconds, s => progress?.Set(s), ct);
        var names = string.Join(", ", batch.Select(p => $"**{p.Project.DisplayName}**"));
        if (failure is not null) {
            foreach (var p in batch) _state.Update(p.Key, s => s.DesiredRef = s.DeployedRef);
            var message = $"⚠️ {names} restarted after self-update, but: {failure}";
            if (progress is not null) await progress.FailAsync($"Switch completed but {failure}");
            else await _reporter.AnnounceAsync(message);
            return;
        }

        foreach (var p in batch) {
            _state.Update(p.Key, s => {
                s.PreviousRef = s.DeployedRef;
                s.DeployedRef = p.Target;
                s.DesiredRef  = p.Target;
                s.UpdatedAt   = DateTimeOffset.UtcNow;
            });
        }
        var doneText = string.Join(", ", batch.Select(p => $"{p.Key} `{p.Target}`"));
        if (progress is not null) await progress.SucceedAsync($"Switch complete (no restart needed). Now running {doneText}.");
        else await _reporter.AnnounceAsync($"✅ {names} self-update complete — back up, now running {doneText}.");
    }

    private async Task FinalizePendingSelfSwitchAsync(CancellationToken ct) {
        var path = PendingMarkerPath();
        if (!File.Exists(path)) return;

        PendingSwitchMarker? marker;
        try {
            marker = JsonSerializer.Deserialize<PendingSwitchMarker>(await File.ReadAllTextAsync(path, ct));
        } catch (Exception ex) {
            _logger.LogError(ex, "Could not read pending-switch marker {Path} — leaving state as-is", path);
            File.Delete(path);
            return;
        }
        File.Delete(path);
        if (marker is null || marker.Targets.Count == 0) return;

        var batch = marker.Targets
            .Where(t => _config.Projects.ContainsKey(t.Key))
            .Select(t => new PendingProject(t.Key, _config.Projects[t.Key], t.Value, _state.Get(t.Key).DeployedRef))
            .ToList();
        if (batch.Count == 0) return;

        // We're running again — the switch finished (or was rolled back by hand). Soak is
        // capped at 30s here (unlike a live switch's full SoakSeconds): this runs at our own
        // startup, so it shouldn't block readiness for arbitrarily long.
        await PromoteAfterSelfSwitchAsync(batch, null, ct, soakCap: 30);
    }

    private string PendingMarkerPath() => Path.Combine(_config.DataPath, ".pending-switch");

    private void WritePendingMarker(List<PendingProject> batch) {
        var marker = new PendingSwitchMarker(batch.ToDictionary(p => p.Key, p => p.Target));
        File.WriteAllText(PendingMarkerPath(), JsonSerializer.Serialize(marker));
    }

    private void DeletePendingMarker() {
        var path = PendingMarkerPath();
        if (File.Exists(path)) File.Delete(path);
    }

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
