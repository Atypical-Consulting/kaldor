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

    /// <summary>The second lock on human-only commands: a non-interactive caller such as the agent cannot answer.</summary>
    static bool ConfirmByHuman(string question)
    {
        if (Console.IsInputRedirected)
            throw new KaldorException("This command needs a person at an interactive terminal.");
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
        Console.WriteLine($"Created {Path.GetRelativePath(cwd, repo.RulesPath)} (commit it with the repository).");

        var hook = Path.GetFullPath(Path.Combine(repo.Root, Proc.Git(repo.Root, "rev-parse", "--git-path", "hooks/pre-commit").Trim()));
        if (File.Exists(hook) && !File.ReadAllText(hook).Contains("Atypical.Kaldor"))
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
            return 0;
        }
        if (state.IntentHash is not null && repo.TaskInProgress())
            throw new KaldorException(
                "Another intent is frozen and the work tree has uncommitted changes. Commit or discard them before " +
                "starting a new task, or propose a change to the current intent with `kaldor intent amend \"<text>\"`.");

        repo.SaveState(state with { IntentHash = hash });
        repo.Log(new LogEntry(DateTimeOffset.Now, "freeze", null, null, hash, intent, null, null, null));
        Console.WriteLine($"Intent frozen ({hash[..19]}…). Commits are now reviewed against it.");
        return 0;
    }

    public static int ProposeAmendment(string cwd, string text)
    {
        var repo = RequireRepo(cwd);
        if (repo.LoadState().IntentHash is null) throw new KaldorException("No frozen intent to amend.");
        File.WriteAllText(repo.AmendmentPath, text.Trim() + "\n");
        repo.Log(new LogEntry(DateTimeOffset.Now, "amend-proposed", null, null, null, text, null, null, null));
        Console.WriteLine("Amendment proposed. Ask the user to run `kaldor intent amend --accept` in their own terminal.");
        return 0;
    }

    public static int AcceptAmendment(string cwd)
    {
        var repo = RequireRepo(cwd);
        if (!File.Exists(repo.AmendmentPath)) throw new KaldorException("No amendment is waiting.");
        var text = File.ReadAllText(repo.AmendmentPath).Trim();
        Console.WriteLine($"Proposed amendment:\n\n{text}\n");
        if (!ConfirmByHuman("Add it to the frozen intent?")) return 1;

        File.AppendAllText(repo.IntentPath, $"\n\n## Amendment ({DateTime.Now:yyyy-MM-dd HH:mm})\n{text}\n");
        var intent = repo.ReadIntent()!;
        var hash = Repo.Hash(intent);
        repo.SaveState(repo.LoadState() with { IntentHash = hash });
        File.Delete(repo.AmendmentPath);
        repo.Log(new LogEntry(DateTimeOffset.Now, "amend-accepted", null, null, hash, intent, null, null, null));
        Console.WriteLine("Amendment accepted; the intent is frozen again.");
        return 0;
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

    public static int Report(string cwd, int count)
    {
        var entries = RequireRepo(cwd).ReadLog().TakeLast(count).ToList();
        if (entries.Count == 0) Console.WriteLine("No review yet.");
        foreach (var e in entries) Print(e);
        return 0;
    }

    static void Print(LogEntry e)
    {
        var verdict = e.Verdict switch { "pass" => "ACCEPTED", "block" => "BLOCKED", "approve" => "FORCED", _ => "" };
        Console.WriteLine($"{e.At:yyyy-MM-dd HH:mm}  {e.Kind,-15} {verdict}");
        if (e.Kind != "review" && e.Kind != "dry-run" && e.Kind != "approve" && e.Intent is not null)
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
        var repo = RequireRepo(cwd);
        var intent = repo.ReadIntent() ?? throw new KaldorException("No intent to approve against.");
        var diff = repo.StagedDiff();
        if (diff.Length == 0) throw new KaldorException("Nothing is staged.");
        var (diffHash, intentHash) = (Repo.Hash(diff), Repo.Hash(intent));

        var last = repo.ReadLog().LastOrDefault(e => e.Kind == "review" && e.DiffHash == diffHash);
        if (last is not null) Print(last);
        if (!ConfirmByHuman("Force this staged diff through without a passing review?")) return 1;

        var state = repo.LoadState();
        state.Passes.Add(new Pass(diffHash, intentHash, "approve"));
        repo.SaveState(state with { IntentHash = state.IntentHash ?? intentHash });
        repo.Log(new LogEntry(DateTimeOffset.Now, "approve", "approve", diffHash, intentHash, intent, null, null,
            $"Forced by {Environment.UserName}"));
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

        Line(Try("git", "--version") is not null, "git is on PATH", "Install git.");
        var claude = Try("claude", "--version");
        var version = claude is null ? null : System.Version.TryParse(claude.Split(' ')[0], out var v) ? v : null;
        Line(version is not null && version >= new System.Version(2, 1, 295),
            $"Claude Code {version?.ToString() ?? "not found"} (2.1.295 or later, for onFailure)",
            "Install or update Claude Code: claude update");
        var plugins = claude is null ? null : Try("claude", "plugin", "list");
        Line(plugins?.Contains("kaldor") == true, "Kaldor plugin installed in Claude Code",
            "/plugin marketplace add Atypical-Consulting/kaldor, then /plugin install kaldor@kaldor");

        var repo = Repo.Find(cwd);
        if (repo is null)
        {
            Console.WriteLine("      (not in a git repository: repository checks skipped)");
            return ok ? 0 : 1;
        }
        Line(repo.Enabled, "Kaldor enabled in this repository (.kaldor/rules.json)", "Run `kaldor init`.");
        if (repo.Enabled)
        {
            try { repo.LoadRules(); Line(true, "rules.json is valid"); }
            catch (JsonException ex) { Line(false, "rules.json is valid", ex.Message); }
        }
        var hook = Path.Combine(repo.Root, Proc.Git(repo.Root, "rev-parse", "--git-path", "hooks/pre-commit").Trim());
        Line(File.Exists(hook) && File.ReadAllText(hook).Contains("Atypical.Kaldor"), "git pre-commit hook installed", "Run `kaldor init`.");
        Console.WriteLine($"      intent: {(repo.LoadState().IntentHash is null ? "none frozen" : "frozen")}");
        return ok ? 0 : 1;
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
