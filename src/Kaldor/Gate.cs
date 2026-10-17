using System.Text.Json.Nodes;

namespace Kaldor;

/// <summary>
/// The <c>PreToolUse</c> hook. Exit 0 lets the tool call through, exit 2 blocks it and shows stderr to Claude.
/// Any other failure exits 1, and <c>onFailure: "block"</c> turns that into a block as well.
/// </summary>
static class Gate
{
    public const string NoIntent =
        "No frozen intent for this task. Write .kaldor/intent.md (goal, expected behavior, must not touch, expected " +
        "files), show it to the user and get their agreement, then run `kaldor intent freeze`.";

    public static int Run(string input, TextWriter err, Reviewer review, IReadOnlyDictionary<string, string?>? env = null)
    {
        string? Env(string name) => env is null ? Environment.GetEnvironmentVariable(name) : env.GetValueOrDefault(name);
        if (Env("KALDOR_REVIEWING") == "1") return 0;

        var hook = JsonNode.Parse(input) ?? throw new KaldorException("Empty hook input.");
        var cwd = hook["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory;
        var toolInput = hook["tool_input"];
        var reason = hook["tool_name"]?.GetValue<string>() switch
        {
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" =>
                GuardEdit(cwd, toolInput?["file_path"]?.GetValue<string>() ?? toolInput?["notebook_path"]?.GetValue<string>()),
            "Bash" or "PowerShell" => GuardCommand(cwd, toolInput?["command"]?.GetValue<string>() ?? "", review),
            _ => null,
        };
        if (reason is null) return 0;
        err.WriteLine($"Kaldor: {reason}");
        return 2;
    }

    static string? GuardEdit(string cwd, string? filePath)
    {
        if (filePath is null) return null;
        var full = Path.GetFullPath(Path.Combine(cwd, filePath));
        var repo = Repo.Find(Directory.Exists(cwd) ? cwd : Path.GetDirectoryName(full)!);
        if (repo is not { Enabled: true }) return null;

        var rel = Path.GetRelativePath(repo.Root, full).Replace('\\', '/');
        if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;

        var frozen = repo.LoadState().IntentHash is not null;
        if (rel == ".kaldor/intent.md")
            return frozen && repo.TaskInProgress()
                ? "The intent is frozen and the task is in progress. Propose the change with `kaldor intent amend \"<text>\"`; only the user can accept it."
                : null;
        if (rel.StartsWith(".kaldor/", StringComparison.Ordinal))
            return "Files in .kaldor/ other than intent.md are written by the kaldor tool only.";
        return frozen ? null : NoIntent;
    }

    static string? GuardCommand(string cwd, string command, Reviewer review)
    {
        if (CommandParser.RunsHumanOnlyCommand(command))
            return "`kaldor approve` and `kaldor intent amend --accept` are reserved for the user, in their own terminal. Ask them.";

        foreach (var commit in CommandParser.FindCommits(command, cwd))
        {
            var repo = Repo.Find(commit.Dir);
            if (repo is not { Enabled: true }) continue;
            if (commit.SkipsHooks)
                return "Skipping git hooks (--no-verify, -n, core.hooksPath) is not allowed in a Kaldor repository.";
            if (Evaluate(repo, review) is { } reason) return reason;
        }
        return null;
    }

    /// <summary>Checks the staged diff of <paramref name="repo"/>. Returns null when the commit may go through.</summary>
    public static string? Evaluate(Repo repo, Reviewer review)
    {
        var state = repo.LoadState();
        var intent = repo.ReadIntent();
        if (state.IntentHash is null || intent is null) return NoIntent;

        var intentHash = Repo.Hash(intent);
        if (intentHash != state.IntentHash)
            return "The intent changed after it was frozen. Restore it, or propose the change with " +
                   "`kaldor intent amend \"<text>\"` and ask the user to accept it.";

        var diff = repo.StagedDiff();
        if (diff.Length == 0) return null;
        var diffHash = Repo.Hash(diff);
        if (state.Passes.Any(p => p.DiffHash == diffHash && p.IntentHash == intentHash)) return null;
        if (diff.Length > Claude.MaxDiffChars)
            return $"The staged diff is too large for one review ({diff.Length:N0} characters). Split it into smaller commits.";

        // The same diff against the same intent was already rejected: same answer, no second model call.
        var deviations = repo.ReadLog().LastOrDefault(e => e is { Kind: "review", Verdict: "block" } &&
                                                          e.DiffHash == diffHash && e.IntentHash == intentHash)?.Deviations;
        if (deviations is null)
        {
            var result = review(repo.LoadRules(), intent, diff);
            repo.Log(new LogEntry(DateTimeOffset.Now, "review", result.Verdict, diffHash, intentHash, intent,
                result.Deviations, result.Hunks, result.Summary));
            if (result.Passed)
            {
                state.Passes.Add(new Pass(diffHash, intentHash, "review"));
                repo.SaveState(state);
                return null;
            }
            deviations = result.Deviations;
        }

        return "The independent reviewer rejected this commit:\n" +
               string.Join('\n', deviations.Select(d => $"- {d}")) +
               "\nFix the deviations and commit again. If the intent itself must change, propose it with " +
               "`kaldor intent amend \"<text>\"` and ask the user to accept it.";
    }
}
