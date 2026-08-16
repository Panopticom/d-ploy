using System.Diagnostics;
using System.Text;

namespace DPloy;

public record ProcessResult(int ExitCode, string Output) {
    public bool Success => ExitCode == 0;
}

/// <summary>Runs external processes (git, nix, sudo, systemctl) with merged output and a timeout.</summary>
public static class ProcessRunner {

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        Action<string>? onOutputLine = null,
        IReadOnlyDictionary<string, string>? env = null,
        CancellationToken ct = default) {

        var psi = new ProcessStartInfo {
            FileName               = fileName,
            WorkingDirectory       = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.EnvironmentVariables[k] = v;

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var sync = new object();

        void Collect(string? line) {
            if (line is null) return;
            lock (sync) output.AppendLine(line);
            onOutputLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived  += (_, e) => Collect(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));

        try {
            await process.WaitForExitAsync(cts.Token);
        } catch (OperationCanceledException) {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            lock (sync) output.AppendLine($"[d-ploy] process '{fileName}' timed out or was cancelled");
            return new ProcessResult(-1, output.ToString());
        }

        return new ProcessResult(process.ExitCode, output.ToString());
    }

    /// <summary>Keeps the last <paramref name="max"/> characters — rebuild logs only matter at the tail.</summary>
    public static string Tail(string text, int max = 1600) {
        text = text.TrimEnd();
        return text.Length <= max ? text : "…" + text[^max..];
    }
}
