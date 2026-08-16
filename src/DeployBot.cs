using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>Single Discord client: connects, registers the /deploy module in the configured guild.</summary>
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
            // of leaving the click looking like it silently failed.
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
            await _interactions.AddModuleAsync<DeployModule>(_services);
            await _interactions.AddModuleAsync<ReleaseAskModule>(_services);
            await _interactions.RegisterCommandsToGuildAsync(_config.GuildId);
            _logger.LogInformation("D-Ploy ready — commands registered in guild {GuildId}", _config.GuildId);
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
