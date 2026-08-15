namespace DPloy;

/// <summary>Tag lookups against project repos — no clones, just git ls-remote.</summary>
public static class GitRemote {

    /// <summary>Semver-ish tags (v1, v1.2, v1.2.3 …), newest first. Empty list on failure.</summary>
    public static async Task<List<string>> GetTagsAsync(string repoUrl, CancellationToken ct = default) {
        var result = await ProcessRunner.RunAsync(
            "git", ["ls-remote", "--tags", repoUrl, "refs/tags/v[0-9]*"],
            timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (!result.Success) return [];

        return result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', ' ').Last())
            .Where(r => r.StartsWith("refs/tags/") && !r.EndsWith("^{}"))
            .Select(r => r["refs/tags/".Length..])
            .Distinct()
            .OrderByDescending(ParseVersion)
            .ToList();
    }

    public static async Task<string?> GetLatestTagAsync(string repoUrl, CancellationToken ct = default)
        => (await GetTagsAsync(repoUrl, ct)).FirstOrDefault();

    /// <summary>Parses "v1.2.3" → Version(1,2,3); malformed tags sort last.</summary>
    private static Version ParseVersion(string tag) {
        var s = tag.TrimStart('v', 'V');
        // Version.TryParse needs at least major.minor
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out var v) ? v : new Version(0, 0);
    }
}
