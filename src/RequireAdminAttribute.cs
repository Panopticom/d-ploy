using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>Restricts commands to AdminUserIds, or anyone holding a role in AdminRoleIds
/// while in DeployerConfig's own guild. /deploy is a global command reachable from a DM (no
/// guild — context.User isn't an IGuildUser there, so only AdminUserIds applies); it's
/// deliberately NOT user-installable (see DeployModule's doc comment), so a "some other
/// server the invoker installed it in" context shouldn't normally be reachable at all — the
/// explicit context.Guild.Id check below is defense in depth for that anyway (keeps a
/// same-named/coincidentally-matching role in a foreign server from ever counting, in case
/// that ever changes or Discord routes something unexpected).</summary>
public class RequireAdminAttribute : PreconditionAttribute {
    public const string NotAuthorizedMessage = "You are not authorized to use D-Ploy.";

    /// <summary>Shared with RequireCommandChannelAttribute so an unauthorized user always sees
    /// the same rejection regardless of which precondition the framework happens to evaluate
    /// first — Discord.Net doesn't guarantee an order between two class-level attributes, so
    /// the two checks can't be allowed to disagree on what an unauthorized user sees.</summary>
    public static bool IsAuthorized(IInteractionContext context, DeployerConfig config) =>
        config.AdminUserIds.Contains(context.User.Id.ToString())
        || (context.Guild?.Id == config.GuildId
            && context.User is IGuildUser guildUser
            && guildUser.RoleIds.Any(r => config.AdminRoleIds.Contains(r.ToString())));

    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services) {
        var config = services.GetRequiredService<IOptions<DeployerConfig>>().Value;

        return Task.FromResult(IsAuthorized(context, config)
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError(NotAuthorizedMessage));
    }
}
