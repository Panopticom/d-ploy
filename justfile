default:
    @just --list

# Build locally with the dotnet SDK
build:
    dotnet build src/DPloy.csproj

# Reproducible Nix build (requires nuget-deps.json to be current)
build-nix:
    nix build

# Regenerate nuget-deps.json after adding/removing/updating NuGet packages (requires Nix)
update-deps:
    nix build .#d-ploy.passthru.fetch-deps
    ./result nuget-deps.json
