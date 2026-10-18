using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

sealed record Rules(string[] Sensitive, string[] Trivial, string ReviewerModel, string[] BlockOn, string Confirm = "before")
{
    /// <summary>True when commits may be reviewed before the user confirms the intent (unattended sessions).</summary>
    public bool ConfirmAfter => Confirm == "after";

    public static readonly Rules Default = new(
        ["**/Auth/**", "**/Migrations/**", "**/appsettings*.json", ".github/**"],
        ["**/*.md"],
        "sonnet",
        ["scope_creep", "sensitive_change", "weakened_tests", "contradicts_intent"]);
}

sealed record Pass(string DiffHash, string IntentHash, string By);

sealed record State(string? IntentHash, List<Pass> Passes, string? ConfirmedHash = null)
{
    public static State Empty => new(null, []);
}

sealed record Hunk(string File, string Risk, bool MustRead);

sealed record ReviewResult(string Verdict, string[] Deviations, Hunk[] Hunks, string Summary)
{
    public bool Passed => Verdict == "pass";
}

sealed record LogEntry(
    DateTimeOffset At, string Kind, string? Verdict = null, string? DiffHash = null, string? IntentHash = null,
    string? Intent = null, string[]? Deviations = null, Hunk[]? Hunks = null, string? Summary = null,
    bool? Unconfirmed = null);

/// <summary>
/// A git repository: <c>.kaldor/</c> in the work tree holds the rules and the intent; the state and the history live
/// in the git common directory, out of reach of <c>git clean</c> and <c>git worktree remove</c>.
/// </summary>
sealed partial class Repo(string root, string commonStateDir, string stateDir, string worktree, string hookPath, string prefix)
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
    /// <summary><c>&lt;git-common-dir&gt;/kaldor/</c>, or <c>.../kaldor/worktrees/&lt;id&gt;/</c> for a linked worktree.</summary>
    public string StateDir { get; } = stateDir;
    /// <summary>The worktree id git uses under <c>worktrees/</c>, or <c>main</c>.</summary>
    public string Worktree { get; } = worktree;
    public string AmendmentPath => Path.Combine(StateDir, "amendment.md");
    public string StatePath => Path.Combine(StateDir, "state.json");
    public string LogDir => Path.Combine(StateDir, "log");
    /// <summary>The absolute path of the pre-commit hook, wherever <c>core.hooksPath</c> puts it.</summary>
    public string HookPath { get; } = hookPath;
    /// <summary>The directory git was asked from, relative to <see cref="Root"/>, with a trailing slash (empty at the top).</summary>
    public string Prefix { get; } = prefix;
    public bool HookInstalled => File.Exists(HookPath) && File.ReadAllText(HookPath).Contains("Atypical.Kaldor");

    /// <summary>Kaldor only acts on repositories where <c>kaldor init</c> was run.</summary>
    public bool Enabled => File.Exists(RulesPath) || CommittedRules() is not null;

    public static Repo? Find(string cwd)
    {
        if (!Directory.Exists(cwd)) return null;
        var (code, output, _) = Proc.Run(
            "git", ["rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir", "--git-path", "hooks/pre-commit", "--show-prefix"], cwd);
        if (code != 0) return null;
        var lines = output.Split('\n');
        if (lines.Length < 5 || lines[0].Length == 0) return null; // inside .git itself: no work tree
        var (gitDir, commonDir) = (Path.GetFullPath(lines[1]), Path.GetFullPath(lines[2]));
        var common = Path.Combine(commonDir, "kaldor");
        var linked = gitDir != commonDir ? Path.GetFileName(gitDir) : null;
        var repo = new Repo(Path.GetFullPath(lines[0]), common, linked is null ? common : Path.Combine(common, "worktrees", linked),
            linked ?? "main", lines[3], lines[4]);
        if (repo.Enabled)
        {
            repo.MoveLegacyState();
            RefreshHook(repo.HookPath);
        }
        return repo;
    }

    /// <summary>
    /// The pre-commit hook pins the version that installed it. After an upgrade, an old checker would read a state
    /// layout it does not know, so the running version rewrites the pin.
    /// </summary>
    static void RefreshHook(string hook)
    {
        if (!File.Exists(hook)) return;
        var text = File.ReadAllText(hook);
        var updated = HookPin().Replace(text, $"${{1}}{Commands.Version}${{2}}");
        if (updated != text) File.WriteAllText(hook, updated);
    }

    [GeneratedRegex(@"(--version )[^\s]+( Atypical\.Kaldor check)")]
    private static partial Regex HookPin();

    /// <summary>Kaldor 1.0 kept the state and the history in <c>.kaldor/</c>: move them once.</summary>
    void MoveLegacyState()
    {
        if (!File.Exists(Path.Combine(Dir, "state.json")) && !Directory.Exists(Path.Combine(Dir, "log")) &&
            !File.Exists(Path.Combine(Dir, "amendment.md"))) return;
        Directory.CreateDirectory(LogDir);
        foreach (var name in new[] { "state.json", "amendment.md" })
        {
            var old = Path.Combine(Dir, name);
            if (File.Exists(old) && !File.Exists(Path.Combine(StateDir, name))) File.Move(old, Path.Combine(StateDir, name));
        }
        var oldLog = Path.Combine(Dir, "log");
        if (!Directory.Exists(oldLog)) return;
        foreach (var file in Directory.GetFiles(oldLog))
        {
            var target = Path.Combine(LogDir, Path.GetFileName(file));
            if (!File.Exists(target)) File.Move(file, target);
        }
    }

    public static string Hash(string text) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The staged diff, exactly as the reviewer sees it. Inside a git hook this honours GIT_INDEX_FILE.</summary>
    public string StagedDiff() => Proc.Git(Root, "--no-replace-objects", "diff", "--cached", "--no-color", "--no-ext-diff", "--no-textconv", "-U5");

    /// <summary>True when the work tree has changes outside <c>.kaldor/</c>: a task is in progress.</summary>
    public bool TaskInProgress() =>
        Proc.Git(Root, "status", "--porcelain", "-uall")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Length > 3 && !line[3..].Trim('"').StartsWith(".kaldor/", StringComparison.Ordinal));

    /// <summary><c>.kaldor/rules.json</c> as committed at HEAD, or null when HEAD has none.</summary>
    public string? CommittedRules()
    {
        var (code, output, _) = Proc.Run("git", ["--no-replace-objects", "show", "HEAD:.kaldor/rules.json"], Root);
        return code == 0 ? output : null;
    }

    /// <summary>
    /// The rules the gate applies: those committed at HEAD, so no command can change them without a commit. Until the
    /// setup is first committed, the work-tree file.
    /// </summary>
    public Rules LoadRules() =>
        JsonSerializer.Deserialize<Rules>(CommittedRules() ?? File.ReadAllText(RulesPath), Json) ?? Rules.Default;

    /// <summary>True when the staged changes touch anything in <c>.kaldor/</c>.</summary>
    public bool StagedKaldorChange() =>
        Proc.Git(Root, "--no-replace-objects", "diff", "--cached", "--name-only", "--", ":(top).kaldor/").Length > 0;

    public State LoadState() =>
        File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) ?? State.Empty : State.Empty;

    public void SaveState(State state)
    {
        // ponytail: keeps the last 100 passes; old ones only matter for commits nobody will make again.
        if (state.Passes.Count > 100) state.Passes.RemoveRange(0, state.Passes.Count - 100);
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
    }

    public void SaveAmendment(string text)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(AmendmentPath, text);
    }

    public string? ReadIntent() => File.Exists(IntentPath) ? File.ReadAllText(IntentPath) : null;

    public void Log(LogEntry entry)
    {
        Directory.CreateDirectory(LogDir);
        var name = $"{entry.At:yyyyMMdd'T'HHmmss.fffffff}-{entry.Kind}.json";
        File.WriteAllText(Path.Combine(LogDir, name), JsonSerializer.Serialize(entry, Json));
    }

    public List<LogEntry> ReadLog() => ReadLog(LogDir).Select(e => e.Entry).ToList();

    /// <summary>The newest review of this worktree that matches, reading only the review entries, newest first.</summary>
    public LogEntry? LatestReview(Func<LogEntry, bool> match) =>
        !Directory.Exists(LogDir) ? null :
        Directory.GetFiles(LogDir, "*-review.json").OrderDescending(StringComparer.Ordinal)
            .Select(f => JsonSerializer.Deserialize<LogEntry>(File.ReadAllText(f), Json)!)
            .FirstOrDefault(match);

    /// <summary>The history of every worktree, oldest first, each entry with the id of the worktree it came from.</summary>
    public List<(string Worktree, LogEntry Entry)> ReadAllLogs()
    {
        var main = Path.Combine(CommonStateDir, "log");
        var linked = Path.Combine(CommonStateDir, "worktrees");
        var dirs = new List<(string, string)> { ("main", main) };
        if (Directory.Exists(linked))
            dirs.AddRange(Directory.GetDirectories(linked).Select(d => (Path.GetFileName(d), Path.Combine(d, "log"))));
        return dirs.SelectMany(d => ReadLog(d.Item2).Select(e => (d.Item1, e.Entry, e.Name)))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => (e.Item1, e.Entry))
            .ToList();
    }

    /// <summary><c>&lt;git-common-dir&gt;/kaldor/</c>, shared by every worktree.</summary>
    string CommonStateDir { get; } = commonStateDir;

    static IEnumerable<(string Name, LogEntry Entry)> ReadLog(string dir) =>
        !Directory.Exists(dir) ? [] :
        Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), JsonSerializer.Deserialize<LogEntry>(File.ReadAllText(f), Json)!));
}
