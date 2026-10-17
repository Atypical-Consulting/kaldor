using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kaldor;

/// <summary>An error shown to the user as-is, without a stack trace.</summary>
sealed class KaldorException(string message) : Exception(message);

static class Proc
{
    /// <summary>Runs a process with no shell and returns its exit code and output.</summary>
    public static (int Code, string Out, string Err) Run(
        string file, IEnumerable<string> args, string cwd, string? stdin = null,
        IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new KaldorException($"Could not start {file}.");
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        if (!p.WaitForExit(timeout ?? Timeout.InfiniteTimeSpan))
        {
            p.Kill(entireProcessTree: true);
            throw new KaldorException($"{file} did not finish within {timeout}.");
        }
        return (p.ExitCode, output.Result, err.Result);
    }

    public static string Git(string cwd, params string[] args)
    {
        var (code, output, err) = Run("git", args, cwd);
        if (code != 0) throw new KaldorException($"git {string.Join(' ', args)} failed: {err.Trim()}");
        return output;
    }
}

sealed record Rules(string[] Sensitive, string[] Trivial, string ReviewerModel, string[] BlockOn)
{
    public static readonly Rules Default = new(
        ["**/Auth/**", "**/Migrations/**", "**/appsettings*.json", ".github/**"],
        ["**/*.md"],
        "sonnet",
        ["scope_creep", "sensitive_change", "weakened_tests", "contradicts_intent"]);
}

sealed record Pass(string DiffHash, string IntentHash, string By);

sealed record State(string? IntentHash, List<Pass> Passes)
{
    public static State Empty => new(null, []);
}

sealed record Hunk(string File, string Risk, bool MustRead);

sealed record ReviewResult(string Verdict, string[] Deviations, Hunk[] Hunks, string Summary)
{
    public bool Passed => Verdict == "pass";
}

sealed record LogEntry(
    DateTimeOffset At, string Kind, string? Verdict, string? DiffHash, string? IntentHash,
    string? Intent, string[]? Deviations, Hunk[]? Hunks, string? Summary);

/// <summary>A git repository and its <c>.kaldor/</c> folder.</summary>
sealed class Repo(string root)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public string Root { get; } = root;
    public string Dir => Path.Combine(Root, ".kaldor");
    public string RulesPath => Path.Combine(Dir, "rules.json");
    public string IntentPath => Path.Combine(Dir, "intent.md");
    public string AmendmentPath => Path.Combine(Dir, "amendment.md");
    public string StatePath => Path.Combine(Dir, "state.json");
    public string LogDir => Path.Combine(Dir, "log");

    /// <summary>Kaldor only acts on repositories where <c>kaldor init</c> was run.</summary>
    public bool Enabled => File.Exists(RulesPath);

    public static Repo? Find(string cwd)
    {
        if (!Directory.Exists(cwd)) return null;
        var (code, output, _) = Proc.Run("git", ["rev-parse", "--show-toplevel"], cwd);
        return code == 0 ? new Repo(Path.GetFullPath(output.Trim())) : null;
    }

    public static string Hash(string text) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The staged diff, exactly as the reviewer sees it. Inside a git hook this honours GIT_INDEX_FILE.</summary>
    public string StagedDiff() => Proc.Git(Root, "diff", "--cached", "--no-color", "--no-ext-diff", "-U5");

    /// <summary>True when the work tree has changes outside <c>.kaldor/</c>: a task is in progress.</summary>
    public bool TaskInProgress() =>
        Proc.Git(Root, "status", "--porcelain", "-uall")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Length > 3 && !line[3..].Trim('"').StartsWith(".kaldor/", StringComparison.Ordinal));

    public Rules LoadRules() =>
        JsonSerializer.Deserialize<Rules>(File.ReadAllText(RulesPath), Json) ?? Rules.Default;

    public State LoadState() =>
        File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) ?? State.Empty : State.Empty;

    public void SaveState(State state)
    {
        // ponytail: keeps the last 100 passes; old ones only matter for commits nobody will make again.
        if (state.Passes.Count > 100) state.Passes.RemoveRange(0, state.Passes.Count - 100);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
    }

    public string? ReadIntent() => File.Exists(IntentPath) ? File.ReadAllText(IntentPath) : null;

    public void Log(LogEntry entry)
    {
        Directory.CreateDirectory(LogDir);
        var name = $"{entry.At:yyyyMMdd'T'HHmmss.fffffff}-{entry.Kind}.json";
        File.WriteAllText(Path.Combine(LogDir, name), JsonSerializer.Serialize(entry, Json));
    }

    public List<LogEntry> ReadLog() =>
        !Directory.Exists(LogDir) ? [] :
        Directory.GetFiles(LogDir, "*.json").Order(StringComparer.Ordinal)
            .Select(f => JsonSerializer.Deserialize<LogEntry>(File.ReadAllText(f), Json)!)
            .ToList();
}
