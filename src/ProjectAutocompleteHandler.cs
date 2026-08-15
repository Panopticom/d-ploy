using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>Autocompletes the `project` parameter from the configured project keys.</summary>
public class ProjectAutocompleteHandler : AutocompleteHandler {
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction interaction,
        IParameterInfo parameter, IServiceProvider services) {

        var config = services.GetRequiredService<IOptions<DeployerConfig>>().Value;
        var typed = interaction.Data.Current.Value?.ToString() ?? "";

        var results = config.Projects
            .Where(p => p.Key.Contains(typed, StringComparison.OrdinalIgnoreCase)
                     || p.Value.DisplayName.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => new AutocompleteResult(p.Value.DisplayName, p.Key));

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}
