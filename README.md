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
2. **Discord application**: see [Discord bot setup](#discord-bot-setup) below.
3. **Secrets** (sops, in your infra repo): `d-ploy/bot-token`, and `d-ploy/webhook-secret` if
   you enable the webhook.
4. **SSH**: on the host, give the `d-ploy` user (home `/var/lib/d-ploy`) an SSH key with
   write access to the infra repo and read access to each project repo, and seed
   `~/.ssh/known_hosts` (e.g. `ssh-keyscan github.com`).
5. **Infra repo**: add this flake as an input and enable the module:

```nix
inputs.d-ploy.url = "git+ssh://git@github.com/Panopticom/d-ploy";
inputs.d-ploy.inputs.sops-nix.follows = "sops-nix"; # avoid evaluating sops-nix twice

# in your host config:
imports = [ d-ploy.nixosModules.default ];
services.d-ploy = {
  enable = true;
  guildId = "…"; adminUserIds = [ "…" ]; deployChannelId = "…";
  infraRepo = "git@github.com:Panopticom/infra";
  # webhook = { enable = true; port = 8767; }; # optional — add once the basics are trusted (see below)
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

## Discord bot setup

D-Ploy needs its own Discord application — one per deployment (it's a single-instance,
single-guild daemon; see `flake.nix`'s NixOS module comment).

1. **Create the application**: [Discord Developer Portal](https://discord.com/developers/applications)
   → **New Application**. Name it whatever you like (e.g. "D-Ploy").
2. **Bot tab**:
   - Toggle **Public Bot** off — this bot is meant for your guild only, not to be installed
     by anyone else.
   - Leave all three **Privileged Gateway Intents** off (Presence, Server Members, Message
     Content). D-Ploy only requests the unprivileged `Guilds` intent.
   - Copy the **token** — this is the value that goes into the `d-ploy/bot-token` sops secret
     (setup step 3).
3. **OAuth2 → URL Generator**:
   - **Scopes**: check both `bot` and `applications.commands`. `applications.commands` alone
     registers the `/deploy` slash command but never actually joins the bot to the guild —
     D-Ploy also posts and edits plain channel messages for deploy progress, which needs the
     `bot` scope too.
   - **Bot Permissions**: check `View Channel` and `Send Messages` only. D-Ploy never sends
     embeds, files, or reactions, and only ever edits messages it posted itself.
   - Copy the generated URL at the bottom of the page, open it, and select your guild to
     invite the bot.
4. **Collect the IDs** `services.d-ploy` needs. Turn on Discord's Developer Mode first
   (User Settings → Advanced → Developer Mode) so right-click menus offer a "Copy ID" option:
   - `guildId` — right-click your server's icon → **Copy Server ID**
   - `deployChannelId` — right-click the channel where you want live deploy progress
     messages → **Copy Channel ID**
   - `adminUserIds` — right-click each user who should be allowed to run `/deploy` →
     **Copy User ID**

Plug the token, IDs, and permissions above into setup steps 3 and 5.

## GitHub webhook

Point a workflow at `POST https://…/hook/` (note the trailing slash — `HttpListener` prefix
matching requires it) with `Authorization: Bearer {webhook-secret}` and the ref (`HEAD` or
`v1.2.3`) as the body — discord-publisher's existing `deploy-notify.yml` works as-is once its
URL secret is updated. Whether a push actually deploys is controlled per project with
`/deploy auto` (`off` / `tags` / `commits` / `ask`).

## Release-ask prompts

`/deploy auto <project> ask` is a middle ground between `off` (silent) and `tags`/`commits`
(fully automatic): when a new tag appears on the project's repo, D-Ploy posts a message in
`DeployChannelId` pinging every `adminUserIds` entry, with **Deploy** / **Skip** buttons
attached — nothing is deployed until an admin clicks one. Clicking Deploy sets that release as
desired (same as `/deploy update`) and edits the message to say who approved it; Skip just
dismisses it (`/deploy update <project>` is still there if you change your mind). Each release
is only prompted once; if a newer tag shows up before you've answered, it gets its own prompt
too — older unanswered ones are left in the channel rather than retracted, and their buttons
still work if you decide you want that exact version after all.

## Release-check schedule

By default the release-check tag poll (what drives `tags`/`ask`/`commits` auto mode, and the
webhook is a separate, event-driven path on top of it) runs every `reconcileIntervalMinutes` —
fine if you don't mind checking every few minutes, but that's also shared with the desired/
deployed reconcile pass, and some people would rather check on their own cadence than depend
on a GitHub webhook being wired up at all. Set `updateCheckSchedule` on the config to a
[systemd calendar expression](https://www.freedesktop.org/software/systemd/man/latest/systemd.time.html#Calendar%20Events)
— e.g. `"daily"`, `"*-*-* 03:00:00"`, `"Mon 09:00"` — and release checks move to that schedule
instead, independent of the reconcile pass:

```nix
services.d-ploy.updateCheckSchedule = "daily";
```

Leave it unset and nothing changes — checks keep riding the reconcile timer as before. An
invalid expression disables checks (with a loud Discord announcement, not a silent failure)
until it's fixed and D-Ploy restarts; validate one yourself first with
`systemd-analyze calendar "<expression>"`.
