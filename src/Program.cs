using Discord;
using Discord.WebSocket;
using DPloy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Config layout matches discord-publisher: the NixOS module symlinks the generated
// appsettings.json and the sops-assembled secrets.json into ./deploy-settings/.
// These two files are the only config sources — no environment-variable overrides,
// so config is fully reproducible from the flake (+ sops-nix for secret values).
static string DeploySetting(string file) =>
    Path.Combine(AppContext.BaseDirectory, "deploy-settings", file) is var p && File.Exists(p)
        ? p
        : Path.Combine(Environment.CurrentDirectory, "deploy-settings", file);

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, config) => {
        config.Sources.Clear();
        config.AddJsonFile(DeploySetting("appsettings.json"), optional: true);
        config.AddJsonFile(DeploySetting("secrets.json"),     optional: true);
    })
    .ConfigureLogging(logging => {
        logging.ClearProviders();
        logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
    })
    .ConfigureServices((ctx, services) => {
        services.Configure<DeployerConfig>(ctx.Configuration.GetSection("Deployer"));

        services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig {
            GatewayIntents = GatewayIntents.Guilds,
        }));
        services.AddSingleton<StateStore>();
        services.AddSingleton<HealthChecker>();
        services.AddSingleton<ProgressReporter>();
        services.AddSingleton<Reconciler>();
        services.AddHostedService(p => p.GetRequiredService<Reconciler>());
        services.AddHostedService<DeployBot>();
        services.AddHostedService<WebhookService>();
    })
    .Build();

await host.RunAsync();
