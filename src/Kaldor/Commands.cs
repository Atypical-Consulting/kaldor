using System.Reflection;
using System.Text.Json;

namespace Kaldor;

static class Commands
{
    public static string Version =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    static Repo RequireRepo(string cwd, bool enabled = true)
    {
        var repo = Repo.Find(cwd) ?? throw new KaldorException("Not inside a git repository.");
        if (enabled && !repo.Enabled) throw new KaldorException("Kaldor is not enabled in this repository. Run `kaldor init`.");
        return repo;
    }

    /// <summary>The form of a human-only command the user can paste: <c>kaldor</c> itself is on PATH only inside Claude Code.</summary>
    public static string ForUser(string command) =>
        $"`dnx Atypical.Kaldor {command}` in a terminal outside Claude Code";

    /// <summary>
    /// The second lock on human-only commands: a non-interactive caller such as the agent cannot answer.
    /// Checked before the command prints anything.
    /// </summary>
    static void RequireTerminal(string command)
    {
        if (Console.IsInputRedirected)
            throw new KaldorException(
                $"This command needs a person at an interactive terminal. Run {ForUser(command)} " +
                "(the `!` prefix in Claude Code has no terminal).");
    }

    static bool ConfirmByHuman(string question)
    {
        Console.Write($"{question} [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    public static int Init(string cwd)
    {
        var repo = RequireRepo(cwd, enabled: false);
        Directory.CreateDirectory(repo.Dir);
        if (!File.Exists(repo.RulesPath))
            File.WriteAllText(repo.RulesPath, JsonSerializer.Serialize(Rules.Default, Repo.Json));
        // Everything but the rules is per-machine state: intent, verdict cache and review history.
        File.WriteAllText(Path.Combine(repo.Dir, ".gitignore"), "*\n!.gitignore\n!rules.json\n");
        Console.WriteLine($"Created {Path.GetRelativePath(cwd, repo.RulesPath)}. Commit .kaldor/ yourself, from your own " +
                          "terminal: Kaldor refuses Claude's commits until a task intent is frozen and confirmed.");

        var hook = repo.HookPath;
        if (File.Exists(hook) && !repo.HookInstalled)
        {
            Console.WriteLine($"A pre-commit hook already exists at {hook}. Add this line to it so commits made from scripts are reviewed too:");
            Console.WriteLine($"  [ \"$CLAUDECODE\" = \"1\" ] && dnx --yes --version {Version} Atypical.Kaldor check || true");
            return 0;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, $"""
            #!/bin/sh
            # Installed by `kaldor init`. Reviews commits made by Claude Code that the PreToolUse hook could not see,
            # such as a commit run from a script. Commits made by a person are not affected.
            [ "$CLAUDECODE" = "1" ] || exit 0
            exec dnx --yes --version {Version} Atypical.Kaldor check

            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, File.GetUnixFileMode(hook) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        Console.WriteLine($"Installed the git pre-commit hook at {hook}.");
        return 0;
    }

    public static int Freeze(string cwd)
    {
        var repo = RequireRepo(cwd);
        var intent = repo.ReadIntent();
        if (string.IsNullOrWhiteSpace(intent))
            throw new KaldorException("Write .kaldor/intent.md first: goal, expected behavior, must not touch, expected files.");

        var state = repo.LoadState();
        var hash = Repo.Hash(intent);
        if (state.IntentHash == hash)
        {
            Console.WriteLine($"Intent already frozen ({hash[..19]}…).");
            UsePreconfirmation(repo, state, hash, intent);
            return 0;
        }
        if (state.IntentHash is not null && repo.TaskInProgress())
            throw new KaldorException(
                "Another intent is frozen and the work tree has uncommitted changes. Commit or discard them before " +
                "starting a new task, or propose a change to the current intent with `kaldor intent amend \"<text>\"`.");

        repo.SaveState(state with { IntentHash = hash, ConfirmedHash = null }); // a new freeze is confirmed afresh, even for text confirmed before
        repo.Log(new LogEntry(DateTimeOffset.Now, "freeze", IntentHash: hash, Intent: intent));
        if (UsePreconfirmation(repo, repo.LoadState(), hash, intent)) return 0;
        if (repo.LoadRules().ConfirmAfter)
            Console.WriteLine($"Intent frozen ({hash[..19]}…). Commits will not wait for it (confirm: after in rules.json); " +
                              $"the user can still confirm it with {ForUser("intent confirm")}.");
        else
            Console.WriteLine($"Intent frozen ({hash[..19]}…). Ask the user to run {ForUser("intent confirm")}: " +
                              "commits wait for it, edits do not. Run `kaldor intent wait` in the background to continue as soon as they do.");
        return 0;
    }

    /// <summary>
    /// A hash the user accepted with <c>intent confirm --all</c> confirms the freeze of exactly that text, once.
    /// Only the human-only command writes <c>Preconfirmed</c>; the agent can only offer text that was shown to the user.
    /// </summary>
    static bool UsePreconfirmation(Repo repo, State state, string hash, string intent)
    {
        if (state.Preconfirmed?.Contains(hash) != true) return false;
        if (state.ConfirmedHash == hash) // already confirmed: nothing to confirm, but the acceptance is spent
        {
            repo.SaveState(state with { Preconfirmed = state.Preconfirmed.Where(h => h != hash).ToList() });
            return false;
        }
        repo.SaveState(state with { ConfirmedHash = hash, Preconfirmed = state.Preconfirmed.Where(h => h != hash).ToList() });
        repo.Log(new LogEntry(DateTimeOffset.Now, "confirm", IntentHash: hash, Intent: intent, Summary: "Confirmed up front with intent confirm --all"));
        Console.WriteLine("Intent frozen and already confirmed by the user.");
        return true;
    }

    /// <summary>
    /// For the agent: returns as soon as the user has confirmed the frozen intent, so nobody has to report it.
    /// Read-only; exit 1 when there is nothing to wait for, or on timeout.
    /// </summary>
    public static int WaitForConfirmation(string cwd, TimeSpan timeout, TimeSpan? poll = null)
    {
        var repo = RequireRepo(cwd);
        var deadline = DateTime.UtcNow + timeout;
        var delay = poll ?? TimeSpan.FromSeconds(1);
        while (true)
        {
            State state;
            try { state = repo.LoadState(); }
            catch (Exception e) when (e is JsonException or IOException)
            {
                Thread.Sleep(delay); // caught the user's terminal mid-write: read again
                continue;
            }
            FrozenIntent(repo, state, "wait for");
            if (state.ConfirmedHash == state.IntentHash)
            {
                Console.WriteLine("Intent confirmed.");
                return 0;
            }
            if (DateTime.UtcNow >= deadline)
            {
                Console.Error.WriteLine($"Kaldor: the intent is still waiting for the user's confirmation " +
                                        $"({ForUser("intent confirm")}). Run `kaldor intent wait` again, or check in with them.");
                return 1;
            }
            Thread.Sleep(delay);
        }
    }

    /// <summary>The frozen intent, unchanged since it was frozen; throws otherwise.</summary>
    static string FrozenIntent(Repo repo, State state, string verb)
    {
        var intent = repo.ReadIntent();
        if (state.IntentHash is null || intent is null) throw new KaldorException($"No frozen intent to {verb}.");
        if (Repo.Hash(intent) != state.IntentHash)
            throw new KaldorException("The intent changed after it was frozen; restore it or freeze it again first.");
        return intent;
    }

    /// <summary>The user confirms the frozen intent: the agent froze it, only a person can vouch for it.</summary>
    public static int ConfirmIntent(string cwd)
    {
        RequireTerminal("intent confirm");
        var repo = RequireRepo(cwd);
        var state = repo.LoadState();
        var intent = FrozenIntent(repo, state, "confirm");
        if (state.ConfirmedHash == state.IntentHash)
        {
            Console.WriteLine("This intent is already confirmed.");
            return 0;
        }
        Console.WriteLine($"Frozen intent:\n\n{intent.Trim()}\n");
        if (!ConfirmByHuman("Confirm it? Claude's commits will be reviewed against it.")) return 1;

        MarkConfirmed(repo, intent);
        Console.WriteLine("Intent confirmed. Claude's commits are now reviewed against it.");
        return 0;
    }

    /// <summary>The user reviews every draft in .kaldor/queue/ and accepts the ones their agent may later freeze.</summary>
    public static int ConfirmAll(string cwd)
    {
        RequireTerminal("intent confirm --all");
        return ConfirmDrafts(RequireRepo(cwd), ConfirmByHuman);
    }

    /// <summary>Control characters other than newline and tab shown as escapes, so a draft cannot hide text from the person reading it.</summary>
    static string Printable(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) && c is not ('\n' or '\t') ? $"\\u{(int)c:x4}" : c.ToString()));

    internal static int ConfirmDrafts(Repo repo, Func<string, bool> ask)
    {
        var drafts = Directory.Exists(repo.QueueDir)
            ? Directory.GetFiles(repo.QueueDir, "*.md").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
            : [];
        drafts = drafts.Where(f => new FileInfo(f).LinkTarget is null).ToList(); // a link could point at a FIFO or at state
        if (drafts.Count == 0)
        {
            repo.SaveState(repo.LoadState() with { Preconfirmed = [] });
            Console.Error.WriteLine("No draft intents in .kaldor/queue/.");
            return 1;
        }
        var accepted = new List<string>();
        foreach (var file in drafts)
        {
            var text = File.ReadAllText(file); // the hash is of what is shown here, not of a later read
            Console.WriteLine($"{Path.GetFileName(file)}:\n\n{Printable(text.Trim())}\n");
            if (!ask("Confirm this intent? Claude's commits will be reviewed against it once a task freezes it.")) continue;
            var hash = Repo.Hash(text);
            accepted.Add(hash);
            repo.Log(new LogEntry(DateTimeOffset.Now, "confirm", IntentHash: hash, Intent: text,
                Summary: $"Pre-confirmed by {Environment.UserName} ({Path.GetFileName(file)})"));
        }
        // A new run replaces the set.
        repo.SaveState(repo.LoadState() with { Preconfirmed = accepted.Distinct().ToList() });
        Console.WriteLine($"{accepted.Count} of {drafts.Count} intent(s) pre-confirmed. Each is used once, when a task freezes exactly that text.");
        return 0;
    }

    internal static void MarkConfirmed(Repo repo, string intent)
    {
        var hash = Repo.Hash(intent);
        repo.SaveState(repo.LoadState() with { ConfirmedHash = hash });
        repo.Log(new LogEntry(DateTimeOffset.Now, "confirm", IntentHash: hash, Intent: intent, Summary: $"Confirmed by {Environment.UserName}"));
    }

    public static int ProposeAmendment(string cwd, string text)
    {
        var repo = RequireRepo(cwd);
        if (repo.LoadState().IntentHash is null) throw new KaldorException("No frozen intent to amend.");
        repo.SaveAmendment(text.Trim() + "\n");
        repo.Log(new LogEntry(DateTimeOffset.Now, "amend-proposed", Intent: text));
        Console.WriteLine($"Amendment proposed. Ask the user to run {ForUser("intent amend --accept")}.");
        return 0;
    }

    public static int AcceptAmendment(string cwd)
    {
        RequireTerminal("intent amend --accept");
        var repo = RequireRepo(cwd);
        if (!File.Exists(repo.AmendmentPath)) throw new KaldorException("No amendment is waiting.");
        var text = File.ReadAllText(repo.AmendmentPath).Trim();
        Console.WriteLine($"Proposed amendment:\n\n{text}\n");
        if (!ConfirmByHuman("Add it to the frozen intent?")) return 1;

        ApplyAmendment(repo, text);
        Console.WriteLine("Amendment accepted; the intent is frozen and confirmed again.");
        return 0;
    }

    /// <summary>The user accepted the amendment, so the amended intent is confirmed as well as frozen.</summary>
    internal static void ApplyAmendment(Repo repo, string text)
    {
        File.AppendAllText(repo.IntentPath, $"\n\n## Amendment ({DateTime.Now:yyyy-MM-dd HH:mm})\n{text}\n");
        var intent = repo.ReadIntent()!;
        var hash = Repo.Hash(intent);
        repo.SaveState(repo.LoadState() with { IntentHash = hash, ConfirmedHash = hash });
        File.Delete(repo.AmendmentPath);
        repo.Log(new LogEntry(DateTimeOffset.Now, "amend-accepted", IntentHash: hash, Intent: intent));
    }

    /// <summary>The git pre-commit hook: same check as the gate, for commits the gate could not see.</summary>
    public static int Check(string cwd, Reviewer review, bool isAgent, TextWriter err)
    {
        var repo = Repo.Find(cwd);
        if (!isAgent || repo is not { Enabled: true }) return 0;
        if (Gate.Evaluate(repo, review) is not { } reason) return 0;
        err.WriteLine($"Kaldor: {reason}");
        return 1;
    }

    public static int Review(string cwd, Reviewer review)
    {
        var repo = RequireRepo(cwd);
        var intent = repo.ReadIntent() ?? throw new KaldorException("No .kaldor/intent.md to review against.");
        var diff = repo.StagedDiff();
        if (diff.Length == 0) throw new KaldorException("Nothing is staged.");
        var r = review(repo.LoadRules(), intent, diff);
        Print(new LogEntry(DateTimeOffset.Now, "dry-run", r.Verdict, Repo.Hash(diff), Repo.Hash(intent), intent, r.Deviations, r.Hunks, r.Summary));
        return 0;
    }

    public static int Report(string cwd, int count, bool unconfirmedOnly = false)
    {
        var repo = RequireRepo(cwd);
        var entries = repo.ReadAllLogs().Where(x => !unconfirmedOnly || x.Entry.Unconfirmed == true).TakeLast(count).ToList();
        if (entries.Count == 0) Console.WriteLine("No review yet.");
        foreach (var (worktree, e) in entries) Print(e, worktree == repo.Worktree ? null : worktree);
        return 0;
    }

    static void Print(LogEntry e, string? otherWorktree = null)
    {
        var verdict = e.Verdict switch { "pass" => "ACCEPTED", "block" => "BLOCKED", "approve" => "FORCED", _ => "" };
        var where = otherWorktree is null ? "" : $"  [worktree {otherWorktree}]";
        Console.WriteLine($"{e.At:yyyy-MM-dd HH:mm}  {e.Kind,-15} {verdict}{(e.Unconfirmed == true ? " UNCONFIRMED" : "")}{where}");
        if (e.Kind is not ("review" or "dry-run" or "approve") && e.Intent is not null)
            Console.WriteLine($"  intent     {FirstLine(e.Intent)}");
        foreach (var d in e.Deviations ?? []) Console.WriteLine($"  deviation  {d}");
        var hunks = e.Hunks ?? [];
        foreach (var h in hunks.Where(h => h.MustRead)) Console.WriteLine($"  to read    {h.File} ({h.Risk})");
        if (hunks.Count(h => !h.MustRead) is var n and > 0) Console.WriteLine($"  validated  {n} trivial or mechanical file(s)");
        if (e.Summary is not null) Console.WriteLine($"  summary    {e.Summary}");
        Console.WriteLine();
    }

    static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";

    public static int Approve(string cwd)
    {
        RequireTerminal("approve");
        var repo = RequireRepo(cwd);
        var pending = repo.LoadState();
        var unreviewed = pending.ReviewedTip is null ? [] : repo.UnreviewedCommits(pending);
        if (unreviewed.Count > 0)
        {
            Console.WriteLine("These commits reached HEAD without a Kaldor review:");
            foreach (var c in unreviewed) Console.WriteLine($"  {c.Subject}");
            Console.WriteLine("Look at them first, for example with `git show <sha>`.");
            if (!ConfirmByHuman("Approve these commits without a review?")) return 1;
            repo.ApproveCommits(pending, unreviewed);
            repo.Log(new LogEntry(DateTimeOffset.Now, "approve", "approve",
                Summary: $"Approved {unreviewed.Count} unreviewed commit(s) by {Environment.UserName}: {string.Join("; ", unreviewed.Select(c => c.Subject))}"));
            Console.WriteLine("Approved. The agent can commit and push again.");
            return 0;
        }

        var intent = repo.ReadIntent() ?? throw new KaldorException("No intent to approve against.");
        var diff = repo.StagedDiff();
        if (diff.Length == 0) throw new KaldorException("Nothing is staged.");
        var (diffHash, intentHash) = (Repo.Hash(diff), Repo.Hash(intent));

        var last = repo.LatestReview(e => e.DiffHash == diffHash);
        if (last is not null) Print(last);
        if (!ConfirmByHuman("Force this staged diff through without a passing review?")) return 1;

        var state = repo.LoadState();
        state.Passes.Add(repo.NewPass(diffHash, intentHash, "approve"));
        repo.SaveState(state with { IntentHash = state.IntentHash ?? intentHash });
        repo.Log(new LogEntry(DateTimeOffset.Now, "approve", "approve", diffHash, intentHash, intent,
            Summary: $"Forced by {Environment.UserName}"));
        Console.WriteLine("Approved. The agent can now commit this exact staged diff.");
        return 0;
    }

    public static int Doctor(string cwd)
    {
        var ok = true;
        void Line(bool pass, string text, string? fix = null)
        {
            ok &= pass;
            Console.WriteLine($"{(pass ? "ok  " : "FAIL")}  {text}{(pass || fix is null ? "" : $"\n      {fix}")}");
        }

        // Started first, read below: the request runs while the local checks do.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var index = http.GetStringAsync("https://api.nuget.org/v3-flatcontainer/atypical.kaldor/index.json");

        Line(Try("git", "--version") is not null, "git is on PATH", "Install git.");
        var claude = Try("claude", "--version");
        var version = claude is null ? null : System.Version.TryParse(claude.Split(' ')[0], out var v) ? v : null;
        Line(version is not null && version >= new System.Version(2, 1, 295),
            $"Claude Code {version?.ToString() ?? "not found"} (2.1.295 or later, for onFailure)",
            "Install or update Claude Code: claude update");
        var plugins = claude is null ? null : Try("claude", "plugin", "list");
        Line(plugins?.Contains("kaldor") == true, "Kaldor plugin installed in Claude Code",
            "In Claude Code: /plugin install kaldor --marketplace Atypical-Consulting/kaldor");
        // A warning only: being offline or one release behind must not fail the doctor.
        try
        {
            Console.WriteLine(NewerPublished(Version, index.Result) is { } latest
                ? $"warn  Kaldor {Version} is running; {latest} is published\n      In Claude Code: /plugin marketplace update kaldor, then /reload-plugins"
                : $"ok    Kaldor {Version} is the latest published version");
        }
        catch (Exception ex) when (ex is AggregateException or HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine("warn  Could not check nuget.org for a newer Kaldor");
        }

        var repo = Repo.Find(cwd);
        if (repo is null)
        {
            Console.WriteLine("      (not in a git repository: repository checks skipped)");
            return ok ? 0 : 1;
        }
        Line(repo.Enabled, "Kaldor enabled in this repository (.kaldor/rules.json)", "Run `kaldor init`.");
        if (repo.Enabled)
        {
            void Validate(string label, Func<Rules> load)
            {
                try
                {
                    var confirm = load().Confirm;
                    if (confirm is not ("before" or "after")) Line(false, label, $"confirm must be \"before\" or \"after\", not \"{confirm}\".");
                    else Line(true, label);
                }
                catch (JsonException ex) { Line(false, label, ex.Message); }
            }
            Rules Parse(string json) => JsonSerializer.Deserialize<Rules>(json, Repo.Json) ?? Rules.Default;
            Validate("rules.json is valid", repo.LoadRules);
            if (repo.CommittedRules() is { } committed && File.Exists(repo.RulesPath))
            {
                var local = File.ReadAllText(repo.RulesPath);
                // Compared as rules, not as text: core.autocrlf makes the work-tree file CRLF and the blob LF.
                string? Norm(string json) { try { return JsonSerializer.Serialize(Parse(json), Repo.Json); } catch (JsonException) { return null; } }
                if (Norm(local) != Norm(committed) || Norm(local) is null)
                {
                    Validate("rules.json in the work tree is valid", () => Parse(local));
                    Console.WriteLine("warn  rules.json has uncommitted changes; they take effect once you commit them");
                }
            }
        }
        Line(repo.HookInstalled, "git pre-commit hook installed", "Run `kaldor init`.");
        Console.WriteLine($"      intent: {(repo.LoadState().IntentHash is null ? "none frozen" : "frozen")}");
        return ok ? 0 : 1;
    }

    /// <summary>The newest stable version in a NuGet flat-container index, when it is newer than <paramref name="running"/>.</summary>
    internal static string? NewerPublished(string running, string indexJson)
    {
        var versions = JsonDocument.Parse(indexJson).RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => System.Version.TryParse(v.GetString(), out var parsed) ? parsed : null) // pre-releases do not parse
            .OfType<System.Version>();
        var latest = versions.Max();
        return latest is not null && System.Version.TryParse(running, out var current) && latest > current
            ? latest.ToString()
            : null;
    }

    static string? Try(string file, params string[] args)
    {
        try
        {
            var (code, output, _) = Proc.Run(file, args, Environment.CurrentDirectory, timeout: TimeSpan.FromSeconds(30));
            return code == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or KaldorException)
        {
            return null;
        }
    }
}
