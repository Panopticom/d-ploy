using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DPloy;

public enum AutoMode { Off, Tags, Commits }

public class ProjectState {
    /// <summary>Ref we want running: a tag name or "HEAD". Null = whatever is deployed.</summary>
    public string? DesiredRef { get; set; }

    /// <summary>Ref confirmed running after a healthy switch.</summary>
    public string? DeployedRef { get; set; }

    /// <summary>What was deployed before DeployedRef — the /deploy rollback target.</summary>
    public string? PreviousRef { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AutoMode AutoMode { get; set; } = AutoMode.Off;

    public string? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Durable desired/deployed state for every project, JSON-serialized to {DataPath}/state.json.
/// All mutations go through Update() which persists atomically (temp file + rename) under a lock,
/// so the reconciler can crash at any point and resume from disk.
/// </summary>
public class StateStore {

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<StateStore> _logger;
    private readonly object _lock = new();
    private Dictionary<string, ProjectState> _projects = [];

    public StateStore(IOptions<DeployerConfig> config, ILogger<StateStore> logger) {
        _logger = logger;
        Directory.CreateDirectory(config.Value.DataPath);
        _path = Path.Combine(config.Value.DataPath, "state.json");
        Load();
    }

    private void Load() {
        try {
            if (File.Exists(_path))
                _projects = JsonSerializer.Deserialize<Dictionary<string, ProjectState>>(
                    File.ReadAllText(_path), Json) ?? [];
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to load {Path} — starting with empty state", _path);
            _projects = [];
        }
    }

    public ProjectState Get(string key) {
        lock (_lock)
            return _projects.TryGetValue(key, out var s) ? Clone(s) : new ProjectState();
    }

    public Dictionary<string, ProjectState> Snapshot() {
        lock (_lock)
            return _projects.ToDictionary(kv => kv.Key, kv => Clone(kv.Value));
    }

    /// <summary>Mutates a project's state under the store lock and persists atomically.</summary>
    public ProjectState Update(string key, Action<ProjectState> mutate) {
        lock (_lock) {
            if (!_projects.TryGetValue(key, out var state)) {
                state = new ProjectState();
                _projects[key] = state;
            }
            mutate(state);
            Persist();
            return Clone(state);
        }
    }

    private void Persist() {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_projects, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static ProjectState Clone(ProjectState s) => new() {
        DesiredRef  = s.DesiredRef,
        DeployedRef = s.DeployedRef,
        PreviousRef = s.PreviousRef,
        AutoMode    = s.AutoMode,
        UpdatedBy   = s.UpdatedBy,
        UpdatedAt   = s.UpdatedAt,
    };
}
