{
  description = "D-Ploy — NixOS continuous-delivery daemon with a Discord control surface";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    sops-nix.url = "github:Mic92/sops-nix";
    sops-nix.inputs.nixpkgs.follows = "nixpkgs";
  };

  outputs = { self, nixpkgs, sops-nix }:
    let
      system = "x86_64-linux";
      pkgs = nixpkgs.legacyPackages.${system};
    in {

      packages.${system} = rec {
        d-ploy = pkgs.buildDotnetModule {
          pname = "d-ploy";
          version = "0.1.0";
          src = pkgs.lib.cleanSourceWith {
            src = ./.;
            filter = path: type:
              let base = baseNameOf path;
              in !(base == "bin" || base == "obj" || base == "result");
          };
          projectFile = "src/DPloy.csproj";
          nugetDeps = ./nuget-deps.json; # regenerate with `just update-deps` after package changes
          dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
          dotnet-runtime = pkgs.dotnetCorePackages.runtime_10_0;
          selfContainedBuild = false;
        };
        default = d-ploy;
      };

      # Build/package tooling only. D-Ploy has no local-run path: it reads all config
      # (secrets included) from ./deploy-settings/*.json, which only the deployed NixOS
      # module + sops-nix ever produce — no environment variables involved anywhere.
      devShells.${system}.default = pkgs.mkShell {
        packages = [ pkgs.dotnetCorePackages.sdk_10_0 pkgs.just ];
      };

      # -----------------------------------------------------------------------
      # NixOS module (single instance — this is a per-host deploy daemon)
      # -----------------------------------------------------------------------
      nixosModules.default = { config, lib, pkgs, ... }:
        let
          inherit (lib) mkOption mkEnableOption types;
          cfg = config.services.d-ploy;
          pkg = self.packages.${pkgs.system}.d-ploy;
          stateDir = "/var/lib/d-ploy";

          projectOpts = { name, ... }: {
            options = {
              displayName = mkOption {
                type = types.str;
                default = name;
                description = "Human-readable name shown in Discord.";
              };
              repoUrl = mkOption {
                type = types.str;
                description = "SSH URL of the project repo (tag lookups + flake input override).";
              };
              infraInputName = mkOption {
                type = types.str;
                default = name;
                description = "Input name in the infra flake that deploys bump.";
              };
              nixosAttr = mkOption {
                type = types.str;
                description = "nixosConfigurations attribute in the infra flake to switch to.";
              };
              healthUnits = mkOption {
                type = types.listOf types.str;
                default = [];
                description = "systemd units that must be active after a switch (health soak).";
              };
              soakSeconds = mkOption {
                type = types.int;
                default = 60;
                description = "How long to watch healthUnits before declaring a deploy healthy.";
              };
              selfUpdate = mkOption {
                type = types.bool;
                default = false;
                description = "True for the project that IS d-ploy (switch runs detached).";
              };
            };
          };

          # One narrowly-scoped wrapper per project; the ONLY thing d-ploy may sudo.
          #   d-ploy-switch-{key} switch {flakeDir} — nixos-rebuild switch --flake {flakeDir}#{attr}
          #   d-ploy-switch-{key} rollback         — nixos-rebuild switch --rollback
          mkSwitchScript = key: proj: pkgs.writeShellApplication {
            name = "d-ploy-switch-${key}";
            runtimeInputs = with pkgs; [ nixos-rebuild nix git coreutils ];
            text = ''
              case "''${1:-}" in
                switch)
                  flakeDir="''${2:?usage: d-ploy-switch-${key} switch <flakeDir>}"
                  exec nixos-rebuild switch --flake "$flakeDir#${proj.nixosAttr}"
                  ;;
                rollback)
                  exec nixos-rebuild switch --rollback
                  ;;
                *)
                  echo "usage: d-ploy-switch-${key} {switch <flakeDir>|rollback}" >&2
                  exit 1
                  ;;
              esac
            '';
          };

          switchScripts = lib.mapAttrs mkSwitchScript cfg.projects;
          anySelfUpdate = lib.any (p: p.selfUpdate) (lib.attrValues cfg.projects);

          appSettings = {
            Deployer = {
              GuildId         = cfg.guildId;
              AdminUserIds    = cfg.adminUserIds;
              DeployChannelId = cfg.deployChannelId;
              DataPath        = stateDir;
              InfraRepo       = cfg.infraRepo;
              ReconcileIntervalMinutes = cfg.reconcileIntervalMinutes;
              Projects = lib.mapAttrs (key: proj: {
                DisplayName      = proj.displayName;
                RepoUrl          = proj.repoUrl;
                InfraInputName   = proj.infraInputName;
                SwitchScriptPath = "${switchScripts.${key}}/bin/d-ploy-switch-${key}";
                HealthUnits      = proj.healthUnits;
                SoakSeconds      = proj.soakSeconds;
                SelfUpdate       = proj.selfUpdate;
              }) cfg.projects;
            } // lib.optionalAttrs cfg.webhook.enable { WebhookPort = cfg.webhook.port; };
          };
          appSettingsFile = (pkgs.formats.json {}).generate "d-ploy-appsettings.json" appSettings;

          preStartScript = pkgs.writeShellScript "d-ploy-prestart" ''
            mkdir -p ${stateDir}/deploy-settings
            ln -sf ${appSettingsFile} ${stateDir}/deploy-settings/appsettings.json
            ln -sf ${config.sops.templates."d-ploy-secrets.json".path} ${stateDir}/deploy-settings/secrets.json
          '';
        in {
          imports = [ sops-nix.nixosModules.sops ];

          options.services.d-ploy = {
            enable = mkEnableOption "D-Ploy continuous-delivery daemon";

            guildId = mkOption { type = types.str; description = "Guild where /deploy commands are registered."; };
            adminUserIds = mkOption { type = types.listOf types.str; description = "Discord user IDs allowed to deploy."; };
            deployChannelId = mkOption { type = types.str; description = "Channel for live deploy progress messages."; };

            infraRepo = mkOption { type = types.str; description = "SSH URL of the NixOS infra repo (source of truth)."; };

            sopsSecretPrefix = mkOption {
              type = types.str;
              default = "d-ploy";
              description = "sops secret names: {prefix}/bot-token and, with webhook.enable, {prefix}/webhook-secret.";
            };

            reconcileIntervalMinutes = mkOption { type = types.int; default = 5; };

            webhook = {
              enable = mkEnableOption "GitHub webhook trigger (POST /hook)";
              port = mkOption { type = types.port; default = 8767; };
            };

            projects = mkOption {
              type = types.attrsOf (types.submodule projectOpts);
              default = {};
              description = "Deployable projects, keyed by slash-command project key.";
            };
          };

          config = lib.mkIf cfg.enable {
            users.users.d-ploy = {
              isSystemUser = true;
              group = "d-ploy";
              home = stateDir;
              createHome = true;
              description = "D-Ploy deploy daemon";
            };
            users.groups.d-ploy = {};

            sops.secrets = {
              "${cfg.sopsSecretPrefix}/bot-token" = { };
            } // lib.optionalAttrs cfg.webhook.enable {
              "${cfg.sopsSecretPrefix}/webhook-secret" = { };
            };

            sops.templates."d-ploy-secrets.json" = {
              owner = "d-ploy";
              content = builtins.toJSON ({
                Deployer = {
                  BotToken = config.sops.placeholder."${cfg.sopsSecretPrefix}/bot-token";
                } // lib.optionalAttrs cfg.webhook.enable {
                  WebhookSecret = config.sops.placeholder."${cfg.sopsSecretPrefix}/webhook-secret";
                };
              });
            };

            # The daemon runs unprivileged; these wrappers are the entire root surface.
            security.sudo.extraRules = [{
              users = [ "d-ploy" ];
              commands =
                (lib.mapAttrsToList (key: _: {
                  command = "${switchScripts.${key}}/bin/d-ploy-switch-${key}";
                  options = [ "NOPASSWD" "SETENV" ];
                }) cfg.projects)
                # Self-updates schedule the switch as a detached transient unit so it
                # survives this daemon's own restart.
                ++ lib.optional anySelfUpdate {
                  command = "${pkgs.systemd}/bin/systemd-run";
                  options = [ "NOPASSWD" "SETENV" ];
                };
            }];

            systemd.services.d-ploy = {
              description = "D-Ploy continuous-delivery daemon";
              after    = [ "network-online.target" ];
              wants    = [ "network-online.target" ];
              wantedBy = [ "multi-user.target" ];
              # /run/wrappers first: sudo must be the setuid wrapper, not pkgs.sudo.
              # systemd provides systemctl (health soak) and systemd-run (self-update).
              path = [ "/run/wrappers" ] ++ (with pkgs; [ git nix openssh systemd ]);
              environment = {
                HOME = stateDir; # git/ssh read ~/.ssh and ~/.gitconfig from the state dir
                DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION = "1";
              };
              serviceConfig = {
                User  = "d-ploy";
                Group = "d-ploy";
                ExecStartPre     = "+${preStartScript}";
                ExecStart        = "${pkg}/bin/DPloy";
                WorkingDirectory = stateDir;
                Restart    = "on-failure";
                RestartSec = "10s";
                NoNewPrivileges = false; # sudo wrappers
                PrivateTmp      = true;
                ProtectSystem   = "strict";
                ReadWritePaths  = [ stateDir ];
              };
            };
          };
        };
    };
}
