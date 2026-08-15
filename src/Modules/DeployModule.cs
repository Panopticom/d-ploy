using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>
/// The Discord control surface. Commands only mutate desired state and kick the reconciler —
/// all real work (and progress reporting) happens in the loop, so replies here are instant
/// ephemeral acks and nothing depends on interaction-token lifetimes.
/// </summary>
[RequireAdmin]
[Group("deploy", "D-Ploy: manage deployments")]
[DefaultMemberPermissions((GuildPermission)0)]
public class DeployModule : InteractionModuleBase<SocketInteractionContext> {

    private readonly DeployerConfig _config;
    private readonly StateStore _state;
    private readonly Reconciler _reconciler;

    public DeployModule(IOptions<DeployerConfig> config, StateStore state, Reconciler reconciler) {
        _config     = config.Value;
        _state      = state;
        _reconciler = reconciler;
    }

    // ── /deploy status ────────────────────────────────────────────────────────

    [SlashCommand("status", "Deployed / desired / latest versions for every project")]
    public async Task Status() {
        await DeferAsync(ephemeral: true);
        var sb = new StringBuilder();
        foreach (var (key, project) in _config.Projects) {
            var s = _state.Get(key);
            var latest = await GitRemote.GetLatestTagAsync(project.RepoUrl);
            sb.Append($"**{project.DisplayName}** (`{key}`)\n");
            sb.Append($"  deployed: `{s.DeployedRef ?? "unknown"}`");
            if (s.DesiredRef is not null && s.DesiredRef != s.DeployedRef)
                sb.Append($" → converging to `{s.DesiredRef}`");
            sb.Append('\n');
            sb.Append($"  latest tag: `{latest ?? "none"}`");
            if (latest is not null && latest != s.DeployedRef) sb.Append("  ⬆ update available");
            sb.Append($"\n  auto: `{s.AutoMode.ToString().ToLowerInvariant()}`\n");
        }
        if (sb.Length == 0) sb.Append("No projects configured.");
        await FollowupAsync(sb.ToString(), ephemeral: true);
    }

    // ── /deploy update ────────────────────────────────────────────────────────

    [SlashCommand("update", "Deploy the latest semver tag (or an explicit tag)")]
    public async Task Update(
        [Summary("project"), Autocomplete(typeof(ProjectAutocompleteHandler))] string project,
        [Summary("ref", "Explicit tag to deploy (defaults to the latest)")] string? refName = null) {

        if (Resolve(project) is not { } cfg) { await RespondAsync("Unknown project.", ephemeral: true); return; }
        await DeferAsync(ephemeral: true);

        var target = refName ?? await GitRemote.GetLatestTagAsync(cfg.RepoUrl);
        if (target is null) { await FollowupAsync("No semver tags found on the project repo.", ephemeral: true); return; }
        if (refName is not null) {
            var tags = await GitRemote.GetTagsAsync(cfg.RepoUrl);
            if (!tags.Contains(refName)) { await FollowupAsync($"`{refName}` is not a tag on the project repo.", ephemeral: true); return; }
        }

        var s = SetDesired(project, target);
        await FollowupAsync(s.DeployedRef == target
            ? $"**{cfg.DisplayName}** is already on `{target}`."
            : $"**{cfg.DisplayName}**: desired set to `{target}` — follow progress in <#{_config.DeployChannelId}>.",
            ephemeral: true);
    }

    // ── /deploy test ──────────────────────────────────────────────────────────

    [SlashCommand("test", "Deploy the latest HEAD commit (untagged)")]
    public async Task Test(
        [Summary("project"), Autocomplete(typeof(ProjectAutocompleteHandler))] string project) {
        if (Resolve(project) is not { } cfg) { await RespondAsync("Unknown project.", ephemeral: true); return; }
        SetDesired(project, "HEAD");
        await RespondAsync($"**{cfg.DisplayName}**: deploying `HEAD` — follow progress in <#{_config.DeployChannelId}>.", ephemeral: true);
    }

    // ── /deploy rollback ──────────────────────────────────────────────────────

    [SlashCommand("rollback", "Go back to the previously deployed ref")]
    public async Task Rollback(
        [Summary("project"), Autocomplete(typeof(ProjectAutocompleteHandler))] string project) {
        if (Resolve(project) is not { } cfg) { await RespondAsync("Unknown project.", ephemeral: true); return; }

        var s = _state.Get(project);
        if (s.PreviousRef is null) {
            await RespondAsync($"No previous deployment recorded for **{cfg.DisplayName}** — nothing to roll back to.", ephemeral: true);
            return;
        }
        SetDesired(project, s.PreviousRef);
        await RespondAsync($"**{cfg.DisplayName}**: rolling back to `{s.PreviousRef}` — follow progress in <#{_config.DeployChannelId}>.", ephemeral: true);
    }

    // ── /deploy auto ──────────────────────────────────────────────────────────

    public enum AutoChoice { Off, Tags, Commits }

    [SlashCommand("auto", "Automatic deployment mode for a project")]
    public async Task Auto(
        [Summary("project"), Autocomplete(typeof(ProjectAutocompleteHandler))] string project,
        [Summary("mode", "off = manual only, tags = new releases, commits = every push")] AutoChoice mode) {
        if (Resolve(project) is not { } cfg) { await RespondAsync("Unknown project.", ephemeral: true); return; }

        _state.Update(project, s => {
            s.AutoMode  = (AutoMode)mode;
            s.UpdatedBy = Context.User.Id.ToString();
            s.UpdatedAt = DateTimeOffset.UtcNow;
        });
        await RespondAsync($"**{cfg.DisplayName}**: auto mode set to `{mode.ToString().ToLowerInvariant()}`.", ephemeral: true);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private ProjectConfig? Resolve(string key) => _config.Projects.GetValueOrDefault(key);

    private ProjectState SetDesired(string key, string target) {
        var s = _state.Update(key, st => {
            st.DesiredRef = target;
            st.UpdatedBy  = Context.User.Id.ToString();
            st.UpdatedAt  = DateTimeOffset.UtcNow;
        });
        _reconciler.Kick(key);
        return s;
    }
}
