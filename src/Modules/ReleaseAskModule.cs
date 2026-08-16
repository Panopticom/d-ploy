using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>
/// Handles the Deploy/Skip buttons on release-ask prompts (Reconciler.CheckAutoUpdatesAsync,
/// AutoMode.Ask; posted by ProgressReporter.AskAsync). Separate from DeployModule because
/// component interactions route on the button's custom ID, not a command group.
///
/// [RequireAdmin] matters here specifically because — unlike /deploy, which Discord hides
/// from non-admins via DefaultMemberPermissions — the prompt is a plain channel message, so
/// its buttons are visible (and clickable) by anyone who can see the deploy channel.
/// </summary>
[RequireAdmin]
public class ReleaseAskModule : InteractionModuleBase<SocketInteractionContext> {

    private readonly DeployerConfig _config;
    private readonly StateStore _state;
    private readonly Reconciler _reconciler;

    public ReleaseAskModule(IOptions<DeployerConfig> config, StateStore state, Reconciler reconciler) {
        _config     = config.Value;
        _state      = state;
        _reconciler = reconciler;
    }

    [ComponentInteraction("dploy-ask-deploy:*,*")]
    public async Task Deploy(string projectKey, string tag) {
        if (!_config.Projects.TryGetValue(projectKey, out var project)) { await StaleAsync(); return; }

        _state.Update(projectKey, s => {
            s.DesiredRef = tag;
            s.UpdatedBy  = Context.User.Id.ToString();
            s.UpdatedAt  = DateTimeOffset.UtcNow;
        });
        _reconciler.Kick(projectKey);

        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m => {
            m.Content = $"🚀 **{project.DisplayName}**: deploying `{tag}` (approved by {Context.User.Mention}) — "
                      + $"follow progress in <#{_config.DeployChannelId}>.";
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ComponentInteraction("dploy-ask-skip:*,*")]
    public async Task Skip(string projectKey, string tag) {
        var displayName = _config.Projects.TryGetValue(projectKey, out var project) ? project.DisplayName : projectKey;

        // AskedRef (set when the prompt was posted) already covers not re-nagging about this
        // tag — nothing else to update. `/deploy update` remains available if they change their mind.
        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m => {
            m.Content = $"⏭ **{displayName}**: skipped `{tag}` (dismissed by {Context.User.Mention}). "
                      + $"Run `/deploy update {projectKey}` any time to deploy it later.";
            m.Components = new ComponentBuilder().Build();
        });
    }

    private async Task StaleAsync() =>
        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m => {
            m.Content = "This project no longer exists in D-Ploy's config.";
            m.Components = new ComponentBuilder().Build();
        });
}
