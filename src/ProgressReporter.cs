using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>
/// One Discord message per deployment in DeployChannelId, edited in place as the deploy
/// progresses. Replaces the old interaction-token followup mechanism entirely — channel
/// messages never expire, and the daemon survives the rebuild so it can keep editing.
/// </summary>
public class ProgressReporter {

    private static readonly TimeSpan EditThrottle = TimeSpan.FromSeconds(2);

    private readonly DiscordSocketClient _client;
    private readonly DeployerConfig _config;
    private readonly ILogger<ProgressReporter> _logger;

    public ProgressReporter(DiscordSocketClient client, IOptions<DeployerConfig> config, ILogger<ProgressReporter> logger) {
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    private IMessageChannel? Channel => _client.GetChannel(_config.DeployChannelId) as IMessageChannel;

    public async Task<Progress> StartAsync(string title) {
        IUserMessage? message = null;
        if (Channel is { } channel) {
            try {
                message = await channel.SendMessageAsync($"⏳ {title}");
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Could not post progress message — continuing without one");
            }
        }
        return new Progress(message, title, _logger);
    }

    /// <summary>Fire-and-forget standalone announcement (startup notices, auto-deploy triggers).</summary>
    public async Task AnnounceAsync(string text) {
        try { await (Channel?.SendMessageAsync(text) ?? Task.CompletedTask); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not post announcement"); }
    }

    /// <summary>
    /// AutoMode.Ask: post a release-ask prompt with Deploy/Skip buttons, pinging the admins.
    /// Only posts — ReleaseAskModule owns mutating state and editing the message once someone
    /// responds. Custom IDs are "dploy-ask-{deploy|skip}:{projectKey},{tag}".
    /// </summary>
    public async Task AskAsync(string projectKey, ProjectConfig project, string tag) {
        if (Channel is not { } channel) return;
        var mentions = string.Join(' ', _config.AdminUserIds.Select(id => $"<@{id}>"));
        var components = new ComponentBuilder()
            .WithButton("Deploy", $"dploy-ask-deploy:{projectKey},{tag}", ButtonStyle.Success, new Emoji("🚀"))
            .WithButton("Skip",   $"dploy-ask-skip:{projectKey},{tag}",   ButtonStyle.Secondary, new Emoji("⏭"))
            .Build();
        try {
            await channel.SendMessageAsync(
                $"{mentions}\n🔔 **{project.DisplayName}**: new release `{tag}` available. Deploy it?",
                components: components);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not post release-ask prompt");
        }
    }

    public class Progress {
        private readonly IUserMessage? _message;
        private readonly string _title;
        private readonly ILogger _logger;
        private readonly object _lock = new();
        private string _body = "";
        private DateTimeOffset _lastEdit = DateTimeOffset.MinValue;
        private bool _editQueued;

        internal Progress(IUserMessage? message, string title, ILogger logger) {
            _message = message;
            _title = title;
            _logger = logger;
        }

        /// <summary>Replace the body (a status line or log tail). Throttled to one edit per 2s.</summary>
        public void Set(string body) {
            lock (_lock) {
                _body = body;
                if (_editQueued) return;
                _editQueued = true;
            }
            _ = FlushAfterThrottleAsync($"⏳ {_title}");
        }

        public Task SucceedAsync(string summary) => FinalAsync($"✅ {_title}\n{summary}");
        public Task FailAsync(string summary)    => FinalAsync($"❌ {_title}\n{summary}");

        private async Task FlushAfterThrottleAsync(string header) {
            var wait = _lastEdit + EditThrottle - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            string body;
            lock (_lock) { body = _body; _editQueued = false; _lastEdit = DateTimeOffset.UtcNow; }
            await EditAsync($"{header}\n{body}");
        }

        private async Task FinalAsync(string content) {
            lock (_lock) _editQueued = false;
            await EditAsync(content);
        }

        private async Task EditAsync(string content) {
            if (_message is null) return;
            try {
                if (content.Length > 1950) content = content[..1950] + "…";
                await _message.ModifyAsync(m => m.Content = content);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Progress message edit failed");
            }
        }
    }
}
