# D-Ploy

Continuous delivery for NixOS hosts, driven from Discord.

D-Ploy is a small daemon that keeps your machines running the version you asked for. You say
`/deploy update discord-publisher` in Discord (or push a tag, with auto mode on); D-Ploy bumps
your infra repo's `flake.lock`, pushes that commit, runs `nixos-rebuild switch`, watches the
service's systemd units for a soak period, and rolls back to the previous NixOS generation if
anything looks unhealthy — narrating the whole thing in a live-edited Discord message.

Design notes and the full architecture rationale live in [`D-PLOY-PLAN.md`](D-PLOY-PLAN.md).
For the code layout, see [`AGENTS.md`](AGENTS.md).

## Security model

D-Ploy has two tiers of trust, deliberately kept separate:

- **The NixOS sysadmin** — whoever can edit `services.d-ploy` in the infra repo and run
  `nixos-rebuild switch`. This is full trust: they decide which Discord guild, users, roles,
  and channels are trusted at all, which repos get deployed where, and they hold the
  `bot-token`/`webhook-secret` sops secrets. Changing any of it requires actual
  infrastructure access, not just a Discord permission.
- **Discord mods** — anyone holding the role named in `adminRoleIds` (or listed directly in
  `adminUserIds`). They can run `/deploy` day to day, but only within the boundaries the
  sysadmin already set: which guild, which channel(s), which projects exist at all.

**Nothing set explicitly in Nix is overridable from Discord.** Concretely:

- `[RequireAdmin]`/`[RequireCommandChannel]` (`RequireAdminAttribute.cs`,
  `RequireCommandChannelAttribute.cs`) check `adminUserIds`/`adminRoleIds`/`commandChannelIds`
  in D-Ploy's own code, independent of Discord's permission system. A guild `Administrator` —
  even the server owner — gets exactly the same "You are not authorized to use D-Ploy."
  rejection as anyone else if their ID/role isn't in the Nix config. There's no Discord-side
  toggle that grants real `/deploy` access.
- Discord's own command-visibility controls (**Server Settings → Integrations → D-Ploy**) sit
  entirely on top of this and can only ever be *more* restrictive in effect than
  `adminUserIds`/`adminRoleIds` — granting a role visibility there makes the command
  *appear* for them, but `[RequireAdmin]` still rejects them if they're not actually
  authorized. It can hide the command from an authorized admin; it can't grant access to an
  unauthorized one.
- The one deliberate exception, by design: **role membership itself** (who currently holds the
  role named in `adminRoleIds`) is managed in Discord (**Server Settings → Roles**), not Nix.
  The sysadmin decides *which role ID* is trusted (a Nix change, needs a redeploy); day-to-day
  membership in that role — onboarding/offboarding mods — doesn't. That's the intended
  delegation point, not a gap.

## Configuration reference

| Setting | Nix option | Purpose |
|---|---|---|
| Home guild | `guildId` | The one guild `/deploy` and role checks are scoped to |
| Trusted users | `adminUserIds` | User IDs allowed to run `/deploy` — in `guildId` or via DM |
| Trusted role | `adminRoleIds` | Role ID(s) allowed to run `/deploy`, only inside `guildId` |
| Progress channel | `deployChannelId` | Where live deploy progress + release-ask prompts post |
| Command channel(s) | `commandChannelIds` | Where `/deploy` may be invoked inside `guildId` (defaults to `deployChannelId`) |
| Infra repo | `infraRepo` | SSH URL of the NixOS infra repo D-Ploy bumps + pushes to |
| Secrets | `sopsSecretPrefix` (sops) | `{prefix}/bot-token`, `{prefix}/webhook-secret` |
| Reconcile interval | `reconcileIntervalMinutes` | Safety-net desired/deployed comparison cadence |
| Release-check schedule | `updateCheckSchedule` | When the tag poll runs, in host local time |
| Webhook | `webhook.enable` / `webhook.port` | GitHub Actions trigger listener (`POST /hook`) |
| Per-project config | `projects.<key>.*` | `repoUrl`, `infraInputName`, `nixosAttr`, `healthUnits`, `soakSeconds`, `selfUpdate` |

All of the above requires a Nix change + `nixos-rebuild switch` — nothing in this table can be
changed from Discord. `nixosAttr` also controls **batching** (see below): projects sharing the
same value there get switched together when more than one is due at once.

| Setting | Set via (Discord) | Notes |
|---|---|---|
| Bot token, Public Bot toggle, privileged intents | Developer Portal → **Bot** tab | Public Bot stays off — see [Discord bot setup](#discord-bot-setup) |
| Installation contexts + default install scopes | Developer Portal → **Installation** tab | Guild Install only — User Install is deliberately never enabled, see [Command availability](#command-availability) |
| Guild invite scopes/permissions | **OAuth2 → URL Generator** | One-time, to add the bot to `guildId` |
| Command visibility | **Server Settings → Integrations → D-Ploy** | Cosmetic only — see [Security model](#security-model) |
| **Role membership** | **Server Settings → Roles** | Who currently holds the `adminRoleIds` role — the intended delegation lever |

| Setting | Set via (`/deploy`) | Notes |
|---|---|---|
| Desired ref | `/deploy update` / `test` / `rollback` | Per project; drives the reconciler |
| Auto mode | `/deploy auto` | `off` / `tags` / `commits` / `ask`, per project |

These last two are runtime state (`state.json`), not configuration — deliberately mutable by
any authorized mod, scoped per-project, and never touch the Nix config above.

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
  # adminRoleIds = [ "…" ]; # optional — anyone holding one of these roles can deploy too
  # commandChannelIds = [ "…" ]; # optional — where /deploy can be used; defaults to [deployChannelId]
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

D-Ploy needs its own Discord application — one per deployment (it's a single-instance
daemon: one guild for deploy progress messages and webhook triggers; see `flake.nix`'s
NixOS module comment). `/deploy` is guild-installed only — it's usable in `guildId`'s
designated channel(s), and via DM for anyone in `adminUserIds` — never in any other server.
See [Command availability](#command-availability) below for why that's still true even
though the command is registered globally.

1. **Create the application**: [Discord Developer Portal](https://discord.com/developers/applications)
   → **New Application**. Name it whatever you like (e.g. "D-Ploy").
2. **Bot tab**:
   - Toggle **Public Bot** off — this keeps the application installable only by you/your
     team, not by anyone else who stumbles on it.
   - Leave all three **Privileged Gateway Intents** off (Presence, Server Members, Message
     Content). D-Ploy only requests the unprivileged `Guilds` intent.
   - Copy the **token** — this is the value that goes into the `d-ploy/bot-token` sops secret
     (setup step 3).
3. **Installation tab**:
   - **Installation Contexts**: check **Guild Install** only. Leave **User Install**
     unchecked — D-Ploy must never be installable to a personal account and carried into
     some other server (see [Command availability](#command-availability)).
   - **Guild Install → Default Install Settings**: scopes `bot` + `applications.commands`,
     permissions `View Channel` + `Send Messages`. D-Ploy never sends embeds, files, or
     reactions, and only ever edits messages it posted itself.
4. **OAuth2 → URL Generator**:
   - **Scopes**: check both `bot` and `applications.commands`. `applications.commands` alone
     registers the `/deploy` slash command but never actually joins the bot to the guild —
     D-Ploy also posts and edits plain channel messages for deploy progress, which needs the
     `bot` scope too.
   - **Bot Permissions**: check `View Channel` and `Send Messages` only.
   - Copy the generated URL at the bottom of the page, open it, and select your guild to
     invite the bot.
5. **Collect the IDs** `services.d-ploy` needs. Turn on Discord's Developer Mode first
   (User Settings → Advanced → Developer Mode) so right-click menus offer a "Copy ID" option:
   - `guildId` — right-click your server's icon → **Copy Server ID**
   - `deployChannelId` — right-click the channel where you want live deploy progress
     messages → **Copy Channel ID**
   - `adminUserIds` — right-click each user who should be allowed to run `/deploy`
     (including via DM) → **Copy User ID**
   - `adminRoleIds` (optional) — right-click a role in **Server Settings → Roles** (or a
     member's role pill) → **Copy Role ID**, for roles that should also be allowed to
     deploy. Only takes effect in the guild — a role means nothing in a DM, so members who
     should be able to deploy from a DM still need their user ID in `adminUserIds` too.
   - `commandChannelIds` (optional) — right-click each channel `/deploy` should be usable in
     → **Copy Channel ID**. Leave unset to just reuse `deployChannelId`.

Plug the token, IDs, and permissions above into setup steps 3 and 5.

## Command availability

`/deploy` is usable in exactly two places: `guildId`'s designated channel(s)
(`commandChannelIds`), and via DM with any user in `adminUserIds`. Nowhere else — it's
deliberately **not** installable to a personal account and carried into other servers.

`DeployModule` sets `[IntegrationType(GuildInstall)]` only (no `UserInstall`) and
`[CommandContextType(Guild, BotDm)]` (no `PrivateChannel`, which is meaningless without
`UserInstall` anyway). It's still registered as a **global** command
(`DeployBot.OnReadyAsync`) rather than guild-scoped, but only because DM availability has
always required global registration — a guild-scoped command has never appeared in a DM,
regardless of install type — not because of `UserInstall`. A global command with
`GuildInstall` already reaches DMs for anyone sharing a guild with the bot (i.e. any member
of `guildId`), the same way DM-usable bot commands have worked since before "install types"
existed at all.

Sharing a guild with the bot only affects *visibility* — being a `guildId` member who sees
`/deploy` in their DM list doesn't mean they can use it. Authorization is still
`[RequireAdmin]`: the invoking user's ID must be in `adminUserIds`, or (guild context only —
DMs have no roles) they must hold a role listed in `adminRoleIds`. A non-`adminUserIds`
member gets the same "You are not authorized to use D-Ploy." in a DM as anywhere else. Deploy
progress and release-ask prompts always post to `DeployChannelId` in the guild, regardless of
where `/deploy` itself was run from.

## Audit trail: command channel + non-ephemeral replies

Inside `guildId`, `[RequireCommandChannel]` additionally confines `/deploy` to the channel(s)
in `commandChannelIds` (defaults to just `deployChannelId` if unset) — running it anywhere
else in the guild gets a private "commands can only be used in #channel" reply and nothing
happens. This restriction doesn't apply in a DM (no "designated channel" concept there);
`[RequireAdmin]` is still the real gate there.

Every reply a command actually produces (status output, "desired set to `vX.Y.Z`", "no
previous deployment recorded", etc.) is deliberately **not ephemeral inside `guildId`** —
visible to the whole channel, so the channel itself is a plain-text audit log of who ran what
and what happened. In a DM, replies stay ephemeral instead: there's no channel for an audit
log to live in there, and it's a 1:1 with the bot anyway, so it changes nothing about who can
see it. Rejections (wrong channel, not authorized) are always ephemeral/private regardless of
context, since they're a no-op, not an audited action, and a wrong-channel rejection in
particular happens in whatever channel someone mistakenly tried, not the audit channel — no
reason to broadcast it there.

```nix
services.d-ploy.commandChannelIds = [ "…" ]; # optional — one or more channel IDs; defaults to [deployChannelId]
```

By default `/deploy` is also hidden from non-admins in the guild's command list
(`DefaultMemberPermissions` in `DeployModule`, set to require the `Administrator` guild
permission) — this is a separate, Discord-side visibility gate, independent of
`adminUserIds`/`adminRoleIds`. A user or role that's authorized via `adminRoleIds` but isn't
a guild `Administrator` won't see the command by default either, until a guild admin grants
it explicitly via **Server Settings → Integrations → D-Ploy → `/deploy`**.

Global commands take up to an hour to propagate after D-Ploy first registers them (guild
commands, used before this, are near-instant) — expect a delay the first time, not on every
restart.

## GitHub webhook

Point a workflow at `POST https://…/hook/` (note the trailing slash — `HttpListener` prefix
matching requires it) with `Authorization: Bearer {webhook-secret}` and the ref (`HEAD` or
`v1.2.3`) as the body — discord-publisher's existing `deploy-notify.yml` works as-is once its
URL secret is updated. Whether a push actually deploys is controlled per project with
`/deploy auto` (`off` / `tags` / `commits` / `ask`).

## Batched deploys

When more than one project is due for convergence at once — several `/deploy update`s fired
close together, a webhook and a scheduled release check landing around the same time, or a
release-check pass that finds new tags for multiple projects — D-Ploy doesn't switch for them
one at a time. Projects that share the same `nixosAttr` (i.e. they're all switching the same
host config, which is normal for a single-host D-Ploy instance) are combined into **one**
clone, one `flake.lock` commit, one `nixos-rebuild switch`, and one health soak over the union
of everyone's `healthUnits`. Projects with a different `nixosAttr` are never combined — there's
no way to point one `nixos-rebuild switch` at two different targets, so that's always its own
separate pass.

This is faster and less disruptive than switching repeatedly, but it means **failure is
shared within a batch**: a NixOS generation switch is atomic, so if the soak fails, every
project in that batch rolls back together — including ones whose own health units were
perfectly fine, because there's no such thing as a partial rollback of a shared generation.
If your projects deploy to a single host (the common case), expect batching whenever several
things are due at the same time; there's currently no way to opt a project out of it short of
giving it a different `nixosAttr`.

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

The release-check tag poll (what drives `tags`/`ask`/`commits` auto mode — the webhook is a
separate, event-driven path on top of it) runs on its own schedule rather than depending on
`reconcileIntervalMinutes` or a GitHub webhook being wired up. `updateCheckSchedule` defaults
to **Monday at 12:30pm, in the host's local timezone** (`time.timeZone`, not UTC). Override it
with any [systemd calendar expression](https://www.freedesktop.org/software/systemd/man/latest/systemd.time.html#Calendar%20Events)
— e.g. `"daily"`, `"*-*-* 03:00:00"`, `"Mon 09:00"`:

```nix
services.d-ploy.updateCheckSchedule = "daily"; # or "Mon 12:30" (the default), etc.
```

Set it to `null` to go back to the old behavior — checks riding the `reconcileIntervalMinutes`
reconcile timer instead of a fixed schedule. An invalid expression disables checks (with a
loud Discord announcement, not a silent failure) until it's fixed and D-Ploy restarts;
validate one yourself first with `systemd-analyze calendar "<expression>"` (run without a `TZ`
override, to match how D-Ploy itself evaluates it — against the host's local time).
