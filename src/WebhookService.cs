using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DPloy;

/// <summary>
/// GitHub Actions trigger: POST /hook with `Authorization: Bearer {WebhookSecret}` and the
/// ref (HEAD or a tag name) as the body; optional `?project=key` targets one project,
/// otherwise every project is considered. Each project's AutoMode decides whether the ref
/// is acted on (tags → semver tags only; commits → tags and HEAD; off → ignored).
/// Compatible with discord-publisher's existing deploy-notify.yml workflow.
/// </summary>
public class WebhookService : IHostedService {

    private readonly DeployerConfig _config;
    private readonly StateStore _state;
    private readonly Reconciler _reconciler;
    private readonly ProgressReporter _reporter;
    private readonly ILogger<WebhookService> _logger;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public WebhookService(IOptions<DeployerConfig> config, StateStore state, Reconciler reconciler,
                          ProgressReporter reporter, ILogger<WebhookService> logger) {
        _config     = config.Value;
        _state      = state;
        _reconciler = reconciler;
        _reporter   = reporter;
        _logger     = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (_config.WebhookPort is null || string.IsNullOrWhiteSpace(_config.WebhookSecret)) {
            _logger.LogInformation("Webhook not configured — skipping");
            return Task.CompletedTask;
        }
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_config.WebhookPort}/hook/");
        _listener.Start();
        _logger.LogInformation("Webhook listening on port {Port}", _config.WebhookPort);
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _cts?.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Webhook listener error"); continue; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx) {
        var req = ctx.Request;
        var resp = ctx.Response;
        try {
            if (req.HttpMethod != "POST") { resp.StatusCode = 405; resp.Close(); return; }
            if ((req.Headers["Authorization"] ?? "") != $"Bearer {_config.WebhookSecret}") {
                _logger.LogInformation("Webhook: bad token from {Remote}", req.RemoteEndPoint);
                resp.StatusCode = 401; resp.Close(); return;
            }

            using var reader = new StreamReader(req.InputStream);
            var refName = (await reader.ReadToEndAsync()).Trim();
            if (string.IsNullOrEmpty(refName)) { resp.StatusCode = 400; resp.Close(); return; }

            var projectFilter = req.QueryString["project"];
            var isTag = refName.StartsWith('v') && refName.Length > 1 && char.IsDigit(refName[1]);
            var acted = new List<string>();

            foreach (var (key, project) in _config.Projects) {
                if (projectFilter is not null && key != projectFilter) continue;
                var s = _state.Get(key);
                var act = s.AutoMode switch {
                    AutoMode.Tags    => isTag,
                    AutoMode.Commits => true,
                    _                => false,
                };
                if (!act) continue;

                _state.Update(key, st => {
                    st.DesiredRef = isTag ? refName : "HEAD";
                    st.UpdatedBy  = "webhook";
                    st.UpdatedAt  = DateTimeOffset.UtcNow;
                });
                _reconciler.Kick(key);
                acted.Add(project.DisplayName);
            }

            if (acted.Count > 0)
                await _reporter.AnnounceAsync($"🔔 Webhook: `{refName}` pushed — deploying {string.Join(", ", acted.Select(n => $"**{n}**"))}.");
            _logger.LogInformation("Webhook: ref {Ref} (project={Filter}) → acted on [{Acted}]",
                refName, projectFilter ?? "*", string.Join(",", acted));

            resp.StatusCode = 202;
            resp.Close();
        } catch (Exception ex) {
            _logger.LogError(ex, "Webhook handler error");
            try { resp.StatusCode = 500; resp.Close(); } catch { /* closed */ }
        }
    }
}
