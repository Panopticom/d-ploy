using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>Restricts commands to the configured AdminUserIds.</summary>
public class RequireAdminAttribute : PreconditionAttribute {
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services) {
        var config = services.GetRequiredService<IOptions<DeployerConfig>>().Value;
        return Task.FromResult(config.AdminUserIds.Contains(context.User.Id.ToString())
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError("You are not authorized to use D-Ploy."));
    }
}
