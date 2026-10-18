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

    /// <param name="output">Where an <c>ask</c> decision goes (stdout); without it the gate never asks, it refuses.</param>
    public static int Run(string input, TextWriter err, Reviewer review, IReadOnlyDictionary<string, string?>? env = null,
        TextWriter? output = null)
    {
        string? Env(string name) => env is null ? Environment.GetEnvironmentVariable(name) : env.GetValueOrDefault(name);
        if (Env("KALDOR_REVIEWING") == "1") return 0;

        var hook = JsonNode.Parse(input) ?? throw new KaldorException("Empty hook input.");
        var cwd = hook["cwd"]?.GetValue<string>() ?? Environment.CurrentDirectory;
        var toolInput = hook["tool_input"];
        var tool = hook["tool_name"]?.GetValue<string>();
        if (tool is "Bash" or "PowerShell" && output is not null &&
            Ask(hook, cwd, toolInput?["command"]?.GetValue<string>() ?? "", tool == "PowerShell") is { } ask)
        {
            output.WriteLine(ask);
            return 0;
        }
        var reason = tool switch
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
        // Seeds the shared reviewed set (see Repo.LoadReviewed) before this command can add refs the set would then trust.
        _ = Repo.Find(cwd);
        var found = CommandParser.Analyze(command, cwd);
        if (found.HumanOnly)
            return "`kaldor approve`, `kaldor intent confirm` (also `--all`) and `kaldor intent amend --accept` are reserved for the user, " +
                   "and `kaldor gate` runs only as Claude Code's own hook. " +
                   $"Ask them to run it themselves as {Commands.ForUser("<command>")}. Run alone and exactly as `kaldor <command>`, " +
                   "in a permission mode that asks, it shows the user a permission prompt instead, as long as there is something " +
                   "to vouch for: a frozen and unchanged, unconfirmed intent, a staged diff or unreviewed commits, a proposed amendment.";
        if (found.ClearsClaudeCode)
            return "Clearing CLAUDECODE is not allowed in a Kaldor repository: Kaldor uses it to tell Claude's commands from yours.";
        if (found.Writes.Any(IsKaldorState)) return StateIsKaldors;

        foreach (var commit in found.Commits)
        {
            var repo = Repo.Find(commit.Dir);
            if (repo is not { Enabled: true }) continue;
            if (commit.SkipsHooks)
                return "Skipping git hooks or changing what git commits (--no-verify, -n, core.hooksPath, GIT_* variables, --git-dir, --work-tree, --index-file) is not allowed in a Kaldor repository.";
            if (Evaluate(repo, review) is { } reason) return reason;
        }
        foreach (var dir in found.Pushes)
        {
            // The check runs before the command does: commits it adds before pushing would go unseen.
            if (found.AddsCommits && Repo.Find(dir) is { Enabled: true })
                return "Run the push on its own: this command also changes HEAD (cherry-pick, rebase, merge, reset...), " +
                       "and Kaldor checks the commits before the command runs.";
            var repo = Repo.Find(dir);
            if (repo is not { Enabled: true }) continue;
            if (found.PushesRawCommit)
                return "Push branches or tags by name: a push of a raw commit id cannot be checked against the review history.";
            if (CheckHistory(repo, Repo.PushRoots) is { } reason) return reason;
        }
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:kaldor|dnx (?:--yes )?Atypical\.Kaldor) (intent confirm|approve|intent amend --accept)\z")]
    private static partial System.Text.RegularExpressions.Regex HumanCommand();

    /// <summary>
    /// The prompt route for a human-only command: when the whole command is exactly one of them and Claude Code will show
    /// the user a permission prompt, the hook JSON of an <c>ask</c> decision that shows what the user vouches for and hands
    /// the command a one-time ticket. Null in every other case, which the gate then refuses as before.
    /// </summary>
    static string? Ask(JsonNode hook, string cwd, string command, bool powerShell)
    {
        // bypassPermissions and dontAsk would not reach a person; an unknown or missing mode might not either.
        if (hook["permission_mode"] is not JsonValue mode || !mode.TryGetValue<string>(out var m) || m is not ("default" or "acceptEdits" or "plan"))
            return null;
        command = command.Trim();
        if (HumanCommand().Match(command) is not { Success: true } match) return null;
        if (Repo.Find(cwd) is not { Enabled: true } repo) return null;
        var name = match.Groups[1].Value;
        if (Vouched(repo, name) is not var (subject, shown)) return null;

        var ticket = Commands.IssueTicket(repo, name, subject);
        // The whole tool input, so its other fields (timeout, description...) survive whether Claude Code merges or replaces it.
        var updated = hook["tool_input"]!.DeepClone();
        updated["command"] = powerShell ? $"$env:KALDOR_TICKET='{ticket}'; {command}" : $"KALDOR_TICKET={ticket} {command}";
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "ask",
                ["permissionDecisionReason"] = shown,
                ["updatedInput"] = updated,
            },
        }.ToJsonString();
    }

    /// <summary>What a ticket for <paramref name="command"/> vouches for, and the text the prompt shows; null when there is nothing to vouch for.</summary>
    static (string Subject, string Shown)? Vouched(Repo repo, string command)
    {
        switch (command)
        {
            case "intent confirm":
            {
                var state = repo.LoadState();
                var intent = repo.ReadIntent();
                if (intent is null || state.IntentHash != Repo.Hash(intent) || state.ConfirmedHash == state.IntentHash) return null;
                return (Repo.Hash(intent), "Kaldor: confirm this frozen intent? Claude's commits will be reviewed against it.\n\n" +
                                           Commands.Printable(intent.Trim()));
            }
            case "intent amend --accept":
            {
                if (!File.Exists(repo.AmendmentPath)) return null;
                var text = File.ReadAllText(repo.AmendmentPath).Trim();
                return (Repo.Hash(text), "Kaldor: add this amendment to the frozen intent?\n\n" + Commands.Printable(text));
            }
            default: // approve
            {
                var unreviewed = repo.UnreviewedCommits(Repo.PushRoots);
                if (unreviewed.Count > 0)
                    return (Commands.CommitsSubject(unreviewed.Select(c => c.Sha)),
                            "Kaldor: approve these commits without a review?\n\n" +
                            string.Join('\n', unreviewed.Select(c => "  " + Commands.Printable(c.Subject))));
                var intent = repo.ReadIntent();
                var diff = repo.StagedDiff();
                if (intent is null || diff.Length == 0) return null;
                var diffHash = Repo.Hash(diff);
                var stat = Proc.Git(repo.Root, "--no-replace-objects", "diff", "--cached", "--stat", "--no-color", "--no-ext-diff");
                var deviations = repo.LatestReview(e => e.DiffHash == diffHash)?.Deviations ?? [];
                return (Commands.DiffSubject(Repo.Hash(intent), diffHash),
                        "Kaldor: force this staged diff through without a passing review?\n\n" + Commands.Printable(stat.TrimEnd()) +
                        (deviations.Length == 0 ? "" : "\n\nLatest review:\n" + string.Join('\n', deviations.Select(d => "- " + Commands.Printable(d)))));
            }
        }
    }

    /// <summary>
    /// Refuses when a commit reachable from <paramref name="roots"/> went in without <c>git commit</c> and was never reviewed
    /// or approved (cherry-pick, commit-tree, revert, am, a hand-resolved rebase...). Only the history that existed when
    /// Kaldor first ran on the repository is trusted, in every worktree.
    /// </summary>
    static string? CheckHistory(Repo repo, string[] roots)
    {
        var unreviewed = repo.UnreviewedCommits(roots);
        if (unreviewed.Count == 0) return null;
        return "These commits were not reviewed by Kaldor: " +
               string.Join(", ", unreviewed.Take(10).Select(c => $"`{c.Subject}`{OnRefs(repo, c.Sha)}")) + (unreviewed.Count > 10 ? ", ..." : "") +
               ". Commits must go through `git commit`. Ask the user to look at them and run " +
               $"{Commands.ForUser("approve")}, or remove them (`git reset --keep <last reviewed commit>`, or delete the branch).";
    }

    static string OnRefs(Repo repo, string sha) =>
        repo.RefsContaining(sha) is { Length: > 0 } refs ? $" (on {string.Join(", ", refs.Take(3))})" : "";

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

        if (!repo.HookRuns)
            return "Git would not run Kaldor's pre-commit hook here (core.hooksPath, a replaced or non-executable hook). Ask the user to run " +
                   $"{Commands.ForUser("init")}, or to add Kaldor's line to their own hook.";

        if (CheckHistory(repo, Repo.CommitRoots) is { } unreviewed) return unreviewed;

        var diff = repo.StagedDiff();
        var kaldorChange = repo.StagedKaldorChange();
        if (diff.Length == 0 && !kaldorChange) return null;
        var diffHash = Repo.Hash(diff);
        if (state.Passes.FindIndex(p => p.DiffHash == diffHash && p.IntentHash == intentHash) is var known and >= 0)
        {
            // A pass recorded before Kaldor tracked patch-ids: add it, or the commit it allows would look unreviewed.
            if (state.Passes[known].PatchId is null)
            {
                state.Passes[known] = repo.NewPass(diffHash, intentHash, state.Passes[known].By);
                repo.SaveState(state);
            }
            return null;
        }
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
                state.Passes.Add(repo.NewPass(diffHash, intentHash, "review"));
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
