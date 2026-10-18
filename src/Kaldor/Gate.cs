using System.Text.Json.Nodes;

namespace Kaldor;

/// <summary>
/// The <c>PreToolUse</c> hook. Exit 0 lets the tool call through, exit 2 blocks it and shows stderr to Claude.
/// Any other failure exits 1, and <c>onFailure: "block"</c> turns that into a block as well.
/// </summary>
static partial class Gate
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
        // Start from the file's own nearest existing directory, and let git say where that is in the repository:
        // comparing paths ourselves breaks on symlinks such as macOS's /var -> /private/var.
        var full = Path.GetFullPath(Path.Combine(cwd, filePath));
        if (InGitKaldor(full)) return StateIsKaldors;
        var dir = Path.GetDirectoryName(full);
        while (dir is not null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
        var repo = dir is null ? null : Repo.Find(dir);
        if (repo is not { Enabled: true }) return null;

        var rel = (repo.Prefix + Path.GetRelativePath(dir!, full)).Replace('\\', '/');

        var frozen = repo.LoadState().IntentHash is not null;
        if (rel == ".kaldor/intent.md")
            return frozen && repo.TaskInProgress()
                ? "The intent is frozen and the task is in progress. Propose the change with `kaldor intent amend \"<text>\"`; only the user can accept it."
                : null;
        // Drafts for `kaldor intent confirm --all`: harmless until the user accepts them. Not through a link, which could point at state.
        // Only through the Edit and Write tools, never Bash: a link or hard link planted by a shell command could lead into the state.
        if (QueueDraft().IsMatch(rel) && new DirectoryInfo(repo.QueueDir).LinkTarget is null && new FileInfo(full).LinkTarget is null) return null;
        if (rel.StartsWith(".kaldor/", StringComparison.Ordinal))
            return "Files in .kaldor/ other than intent.md are written by the kaldor tool only.";
        return frozen ? null : NoIntent;
    }

    const string StateIsKaldors =
        "Kaldor's state (.git/kaldor/) and the files in .kaldor/ other than intent.md are written by the kaldor tool only.";

    [System.Text.RegularExpressions.GeneratedRegex(@"^\.kaldor/queue/[^/]+\.md$")]
    private static partial System.Text.RegularExpressions.Regex QueueDraft();

    static string[] Parts(string path) => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True for <c>.git/kaldor</c> and anything under it.</summary>
    static bool InGitKaldor(string path)
    {
        var parts = Parts(path);
        for (var k = 0; k + 1 < parts.Length; k++)
            if (parts[k].Equals(".git", StringComparison.OrdinalIgnoreCase) && parts[k + 1].Equals("kaldor", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>True for Kaldor's state, for <c>.kaldor</c> itself and for anything in it but <c>intent.md</c>.</summary>
    static bool IsKaldorState(string path)
    {
        if (InGitKaldor(path)) return true;
        var parts = Parts(path);
        var k = Array.FindIndex(parts, p => p.Equals(".kaldor", StringComparison.OrdinalIgnoreCase));
        return k >= 0 && !(k == parts.Length - 2 && parts[^1].Equals("intent.md", StringComparison.OrdinalIgnoreCase));
    }

    static string? GuardCommand(string cwd, string command, Reviewer review)
    {
        var found = CommandParser.Analyze(command, cwd);
        if (found.HumanOnly)
            return "`kaldor approve`, `kaldor intent confirm` (also `--all`) and `kaldor intent amend --accept` are reserved for the user. " +
                   $"Ask them to run it themselves as {Commands.ForUser("<command>")}.";
        if (found.Writes.Any(IsKaldorState)) return StateIsKaldors;

        foreach (var commit in found.Commits)
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
        var kaldorChange = repo.StagedKaldorChange();
        if (diff.Length == 0 && !kaldorChange) return null;
        var diffHash = Repo.Hash(diff);
        if (state.Passes.Any(p => p.DiffHash == diffHash && p.IntentHash == intentHash)) return null;
        if (kaldorChange)
            return "This commit changes Kaldor's own files in .kaldor/. Only the user can allow that: ask them to review " +
                   $"the staged diff and run {Commands.ForUser("approve")}.";
        var rules = repo.LoadRules();
        var unconfirmed = state.ConfirmedHash != intentHash;
        if (unconfirmed && !rules.ConfirmAfter)
            return "The user has not confirmed the frozen intent yet. Show it to them and ask them to run " +
                   $"{Commands.ForUser("intent confirm")}. Run `kaldor intent wait` (in the background) to know when they have, " +
                   "then commit again.";
        if (diff.Length > Claude.MaxDiffChars)
            return $"The staged diff is too large for one review ({diff.Length:N0} characters). Split it into smaller commits.";

        // The same diff against the same intent was already rejected: same answer, no second model call.
        var deviations = repo.LatestReview(e => e.Verdict == "block" && e.DiffHash == diffHash && e.IntentHash == intentHash)?.Deviations;
        if (deviations is null)
        {
            var result = review(rules, intent, diff);
            repo.Log(new LogEntry(DateTimeOffset.Now, "review", result.Verdict, diffHash, intentHash, intent,
                result.Deviations, result.Hunks, result.Summary, unconfirmed ? true : null));
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
