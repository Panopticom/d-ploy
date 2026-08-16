using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>Single Discord client: connects, registers the /deploy module globally (guild install
/// only, but global registration so it also reaches DMs — see DeployModule's doc comment).</summary>
public class DeployBot : IHostedService {

    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IServiceProvider _services;
    private readonly DeployerConfig _config;
    private readonly ILogger<DeployBot> _logger;

    public DeployBot(DiscordSocketClient client, IServiceProvider services,
                     IOptions<DeployerConfig> config, ILogger<DeployBot> logger) {
        _client       = client;
        _services     = services;
        _config       = config.Value;
        _logger       = logger;
        _interactions = new InteractionService(_client);
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        _client.Log += msg => { _logger.Log(MapLevel(msg.Severity), msg.Exception, "{Source}: {Message}", msg.Source, msg.Message); return Task.CompletedTask; };
        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += async interaction => {
            var ctx = new SocketInteractionContext(_client, interaction);
            var result = await _interactions.ExecuteCommandAsync(ctx, _services);
            // Slash commands are hidden from non-admins by Discord itself, but release-ask
            // buttons are plain channel messages anyone can see — surface a rejection instead
            // of leaving the click looking like it silently failed. Kept ephemeral even though
            // DeployModule's own replies aren't (see its doc comment): a precondition failure —
            // wrong channel (RequireCommandChannel) or not authorized (RequireAdmin) — is a
            // rejected no-op, not an audited action, and RequireCommandChannel failures in
            // particular happen in whatever channel someone mistakenly tried, not the audit
            // channel, so there's no reason to broadcast it there.
            if (!result.IsSuccess && !interaction.HasResponded) {
                try { await interaction.RespondAsync(result.ErrorReason, ephemeral: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not report interaction failure"); }
            }
        };

        await _client.LoginAsync(TokenType.Bot, _config.BotToken);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    private async Task OnReadyAsync() {
        try {
            var deployModule = await _interactions.AddModuleAsync<DeployModule>(_services);
            await _interactions.AddModuleAsync<ReleaseAskModule>(_services);

            // DeployModule carries its own [IntegrationType]/[CommandContextType] now (guild
            // install + DMs — NOT user install, see its doc comment), which only takes effect
            // on globally-registered commands — guild-scoped commands never appear in a DM
            // regardless of install type, and DM availability has required global registration
            // since long before install types existed. Drop the old guild-scoped registration
            // first (no-op once it's actually gone; safe to run every Ready) so the command
            // doesn't show up twice in the home guild.
            await _interactions.RemoveModulesFromGuildAsync(_config.GuildId, deployModule);
            await _interactions.RegisterCommandsGloballyAsync();
            _logger.LogInformation("D-Ploy ready — commands registered globally (guild install + DMs, no user install)");
        } catch (Exception ex) {
            _logger.LogError(ex, "Command registration failed");
        }
    }

    private static LogLevel MapLevel(LogSeverity s) => s switch {
        LogSeverity.Critical => LogLevel.Critical,
        LogSeverity.Error    => LogLevel.Error,
        LogSeverity.Warning  => LogLevel.Warning,
        LogSeverity.Info     => LogLevel.Information,
        _                    => LogLevel.Debug,
    };
}
