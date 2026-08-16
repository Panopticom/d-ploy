using System.Text;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>
/// The Discord control surface. Commands only mutate desired state and kick the reconciler —
/// all real work (and progress reporting) happens in the loop. Replies are instant acks, and
/// intentionally not ephemeral inside the home guild (see the Ephemeral property): combined
/// with [RequireCommandChannel] confining them to CommandChannelIds there, the channel doubles
/// as an audit log of who ran what. In a DM — the only other surface this is usable from —
/// replies stay ephemeral, since a DM has no "channel" for an audit log to live in anyway.
///
/// [IntegrationType(GuildInstall)] only — deliberately NOT UserInstall: this app must never be
/// carried into some other server. [CommandContextType(Guild, BotDm)] — deliberately NOT
/// PrivateChannel (group DMs), which is meaningless without UserInstall anyway. Registered
/// globally regardless (see DeployBot.OnReadyAsync): DM availability has always required
/// global registration, going back to before integration types existed — guild-scoped
/// commands never appear in DMs, full stop — and a global GuildInstall command already
/// reaches DMs for anyone who shares a guild with the bot (i.e. GuildId's members), no
/// UserInstall needed. The admin gate is still [RequireAdmin] (AdminUserIds only in a DM —
/// roles don't exist there), not guild membership, so being a GuildId member who can see the
/// command in their DMs doesn't imply being able to use it.
/// </summary>
[RequireAdmin]
[RequireCommandChannel]
[Group("deploy", "D-Ploy: manage deployments")]
[DefaultMemberPermissions((GuildPermission)0)]
[CommandContextType(InteractionContextType.Guild, InteractionContextType.BotDm)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public class DeployModule : InteractionModuleBase<SocketInteractionContext> {

    private readonly DeployerConfig _config;
    private readonly StateStore _state;
    private readonly Reconciler _reconciler;

    public DeployModule(IOptions<DeployerConfig> config, StateStore state, Reconciler reconciler) {
        _config     = config.Value;
        _state      = state;
        _reconciler = reconciler;
    }

    /// <summary>Non-ephemeral only inside the home guild, where [RequireCommandChannel] has
    /// already confined us to a known audit channel — that's the one place "visible to
    /// everyone here" is the intended audience. In a DM (the only other reachable surface —
    /// see the class doc comment) ephemeral instead: there's no channel for an audit log to
    /// live in there, and it's a 1:1 with the bot anyway, so it changes nothing about who
    /// can see it.</summary>
    private bool Ephemeral => Context.Guild?.Id != _config.GuildId;

    // ── /deploy status ────────────────────────────────────────────────────────

    [SlashCommand("status", "Deployed / desired / latest versions for every project")]
    public async Task Status() {
        await DeferAsync(ephemeral: Ephemeral);
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
        await FollowupAsync(sb.ToString(), ephemeral: Ephemeral);
    }

    // ── /deploy update ────────────────────────────────────────────────────────

    [SlashCommand("update", "Deploy the latest semver tag (or an explicit tag)")]
    public async Task Update(
        [Summary("project"), Autocomplete(typeof(ProjectAutocompleteHandler))] string project,
        [Summary("ref", "Explicit tag to deploy (defaults to the latest)")] string? refName = null) {

        if (Resolve(project) is not { } cfg) { await RespondAsync("Unknown project.", ephemeral: Ephemeral); return; }
        await DeferAsync(ephemeral: Ephemeral);

        var target = refName ?? await GitRemote.GetLatestTagAsync(cfg.RepoUrl);
        if (target is null) { await FollowupAsync("No semver tags found on the project repo.", ephemeral: Ephemeral); return; }
        if (refName is not null) {
            var tags = await GitRemote.GetTagsAsync(cfg.RepoUrl);
            if (!tags.Contains(refName)) { await FollowupAsync($"`{refName}` is not a tag on the project repo.", ephemeral: Ephemeral); return; }
        }

        var s = SetDesired(project, target);
        await FollowupAsync(s.DeployedRef == target
            ? $"**{cfg.DisplayName}** is already on `{target}`."
            : $"**{cfg.DisplayName}**: desired set to `{target}` — follow progress in <#{_config.DeployChannelId}>.",
            ephemeral: Ephemeral);
    }

    // ── /deploy test ──────────────────────────────────────────────────────────

    [SlashCommand("test", "Deploy the latest HEAD commit (untagged)")]
    public async Task Test(
        [Summary("project"), Autocomplete(typeof(ProjectAutocompleteHandler))] string project) {
        if (Resolve(project) is not { } cfg) { await RespondAsync("Unknown project.", ephemeral: Ephemeral); return; }
        SetDesired(project, "HEAD");
        await RespondAsync($"**{cfg.DisplayName}**: deploying `HEAD` — follow progress in <#{_config.DeployChannelId}>.", ephemeral: Ephemeral);
    }

    // ── /deploy rollback ──────────────────────────────────────────────────────

    [SlashCommand("rollback", "Go back to the previously deployed ref")]
    public async Task Rollback(
        [Summary("project"), Autocomplete(typeof(ProjectAutocompleteHandler))] string project) {
        if (Resolve(project) is not { } cfg) { await RespondAsync("Unknown project.", ephemeral: Ephemeral); return; }

        var s = _state.Get(project);
        if (s.PreviousRef is null) {
            await RespondAsync($"No previous deployment recorded for **{cfg.DisplayName}** — nothing to roll back to.", ephemeral: Ephemeral);
            return;
        }
        SetDesired(project, s.PreviousRef);
        await RespondAsync($"**{cfg.DisplayName}**: rolling back to `{s.PreviousRef}` — follow progress in <#{_config.DeployChannelId}>.", ephemeral: Ephemeral);
    }

    // ── /deploy auto ──────────────────────────────────────────────────────────

    public enum AutoChoice { Off, Tags, Commits, Ask }

    [SlashCommand("auto", "Automatic deployment mode for a project")]
    public async Task Auto(
        [Summary("project"), Autocomplete(typeof(ProjectAutocompleteHandler))] string project,
        [Summary("mode", "off = manual, tags/commits = auto-deploy, ask = prompt with buttons")] AutoChoice mode) {
        if (Resolve(project) is not { } cfg) { await RespondAsync("Unknown project.", ephemeral: Ephemeral); return; }

        _state.Update(project, s => {
            s.AutoMode  = (AutoMode)mode;
            s.UpdatedBy = Context.User.Id.ToString();
            s.UpdatedAt = DateTimeOffset.UtcNow;
        });
        await RespondAsync($"**{cfg.DisplayName}**: auto mode set to `{mode.ToString().ToLowerInvariant()}`.", ephemeral: Ephemeral);
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
