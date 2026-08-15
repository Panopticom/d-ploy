# D-Ploy

Continuous delivery for NixOS hosts, driven from Discord.

D-Ploy is a small daemon that keeps your machines running the version you asked for. You say
`/deploy update discord-publisher` in Discord (or push a tag, with auto mode on); D-Ploy bumps
your infra repo's `flake.lock`, pushes that commit, runs `nixos-rebuild switch`, watches the
service's systemd units for a soak period, and rolls back to the previous NixOS generation if
anything looks unhealthy — narrating the whole thing in a live-edited Discord message.

Design notes and the full architecture rationale live in [`D-PLOY-PLAN.md`](D-PLOY-PLAN.md).
For the code layout, see [`AGENTS.md`](AGENTS.md).

## First-time setup

1. **Build**: `just build` (needs .NET 10 SDK), then on a Nix machine `just update-deps` to
   generate `nuget-deps.json`, then `just build-nix`. Commit `nuget-deps.json`.
2. **Discord application**: create one, grab the bot token, invite it to your guild with the
   `applications.commands` scope.
3. **Secrets** (sops, in your infra repo): `d-ploy/bot-token`, and `d-ploy/webhook-secret` if
   you enable the webhook.
4. **SSH**: on the host, give the `d-ploy` user (home `/var/lib/d-ploy`) an SSH key with
   write access to the infra repo and read access to each project repo, and seed
   `~/.ssh/known_hosts` (e.g. `ssh-keyscan github.com`).
5. **Infra repo**: add this flake as an input and enable the module:

```nix
inputs.d-ploy.url = "github:Panopticom/d-ploy";

# in your host config:
imports = [ d-ploy.nixosModules.default ];
services.d-ploy = {
  enable = true;
  guildId = "…"; adminUserIds = [ "…" ]; deployChannelId = "…";
  infraRepo = "git@github.com:Panopticom/infra";
  webhook = { enable = true; port = 8767; };
  projects.discord-publisher = {
    displayName    = "Discord Publisher";
    repoUrl        = "git@github.com:Panopticom/discord-publisher";
    infraInputName = "discord-publisher";
    nixosAttr      = "nox";
    healthUnits    = [ "discord-publisher-main.service" ];
    soakSeconds    = 60;
  };
};
```

6. Rebuild the host once by hand. From then on, D-Ploy deploys itself and everything else.

## GitHub webhook

Point a workflow at `POST https://…/hook/` (note the trailing slash — `HttpListener` prefix
matching requires it) with `Authorization: Bearer {webhook-secret}` and the ref (`HEAD` or
`v1.2.3`) as the body — discord-publisher's existing `deploy-notify.yml` works as-is once its
URL secret is updated. Whether a push actually deploys is controlled per project with
`/deploy auto` (`off` / `tags` / `commits`).
