using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>Restricts guild-context /deploy commands to CommandChannelIds (or, if unset,
/// just DeployChannelId) — keeps every command and its result in one predictable, visible
/// channel for an audit trail. Only enforced inside the home guild (DeployerConfig.GuildId):
/// a DM has no "designated channel" to check against, so it's left alone there — RequireAdmin
/// is still the real gate. (A foreign guild isn't normally reachable at all — /deploy is
/// deliberately not user-installable, see DeployModule's doc comment — but the guild check
/// below leaves that path alone too rather than assuming it can never happen.)
///
/// Checks RequireAdmin.IsAuthorized first, and reports the exact same rejection if it fails,
/// rather than a channel-specific one. Both this and [RequireAdmin] are class-level
/// preconditions on DeployModule, and Discord.Net doesn't guarantee which of two class-level
/// attributes gets evaluated (and its ErrorReason surfaced) first — so an unauthorized user
/// must never be able to see the more specific "wrong channel" message (which would both leak
/// the designated channel to someone who has no business knowing it, and be a confusing/
/// inconsistent response depending on evaluation order). Only once authorization is confirmed
/// does the channel check itself run.</summary>
public class RequireCommandChannelAttribute : PreconditionAttribute {
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services) {
        var config = services.GetRequiredService<IOptions<DeployerConfig>>().Value;

        if (!RequireAdminAttribute.IsAuthorized(context, config))
            return Task.FromResult(PreconditionResult.FromError(RequireAdminAttribute.NotAuthorizedMessage));

        if (context.Guild?.Id != config.GuildId)
            return Task.FromResult(PreconditionResult.FromSuccess());

        var allowed = config.CommandChannelIds.Count > 0
            ? config.CommandChannelIds
            : [config.DeployChannelId.ToString()];

        if (allowed.Contains(context.Channel.Id.ToString()))
            return Task.FromResult(PreconditionResult.FromSuccess());

        var channels = string.Join(" or ", allowed.Select(id => $"<#{id}>"));
        return Task.FromResult(PreconditionResult.FromError($"D-Ploy commands can only be used in {channels}."));
    }
}
