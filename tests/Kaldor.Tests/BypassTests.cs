using System.Text.Json;

namespace Kaldor.Tests;

/// <summary>
/// The shortcuts from the threat model, each replayed against a real git repository with a fake reviewer.
/// A single one getting through fails the build.
/// </summary>
public sealed class BypassTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kaldor-test-" + Guid.NewGuid().ToString("N"));
    readonly Repo _repo;
    int _reviews;
    ReviewResult _next = Pass;

    static readonly ReviewResult Pass = new("pass", [], [new Hunk("a.txt", "logic", true)], "As intended.");
    static readonly ReviewResult Block = new("block", ["Unannounced cache added in a.txt"], [], "Out of scope.");

    public BypassTests()
    {
        TestRepo.Create(_root);
        _repo = Repo.Find(_root)!;
    }

    public void Dispose()
    {
        foreach (var w in _worktrees) TestRepo.Delete(w);
        TestRepo.Delete(_root);
    }

    ReviewResult FakeReview(Rules rules, string intent, string diff)
    {
        _reviews++;
        _seen = rules;
        _diff = diff;
        return _next;
    }

    Rules? _seen;
    string? _diff;

    void Attributes(string text) => File.WriteAllText(Path.Combine(_root, ".git", "info", "attributes"), text);

    [Fact]
    public void Textconv_cannot_empty_the_staged_diff()
    {
        Proc.Git(_root, "config", "diff.x.textconv", "true");
        Attributes("* diff=x\n");
        FreezeIntent();
        Stage("secret line");
        Assert.Equal(0, Commit().Code);
        Assert.Equal(1, _reviews);
        Assert.Contains("+secret line", _diff);
    }

    [Fact]
    public void A_diff_attribute_cannot_hide_a_text_change()
    {
        Attributes("* -diff\n");
        FreezeIntent();
        Stage("secret line");
        Assert.Equal(0, Commit().Code);
        Assert.Contains("+secret line", _diff);
        Assert.DoesNotContain("Binary files", _diff);
    }

    [Fact]
    public void Real_binary_files_stay_binary()
    {
        FreezeIntent();
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), [1, 0, 2, 0, 3]);
        Proc.Git(_root, "add", "a.bin");
        Assert.Equal(0, Commit().Code);
        Assert.Contains("Binary files", _diff);
    }

    [Fact]
    public void Fsmonitor_does_not_run_inside_the_gate()
    {
        if (OperatingSystem.IsWindows()) return; // the fixture's monitor is a shell script
        var marker = Path.Combine(_root, "..", "ran-" + Guid.NewGuid().ToString("N"));
        var script = Path.Combine(_root, "..", "fsm-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(script, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FreezeIntent();
        Stage();
        Proc.Git(_root, "config", "core.fsmonitor", script); // after the fixture's own git calls
        Hook("Write", new { file_path = _repo.IntentPath, content = "x" }); // reads `git status`
        Assert.Equal(0, Commit().Code);
        var ran = File.Exists(marker);
        File.Delete(script);
        Assert.False(ran);
    }

    [Fact]
    public void A_hook_that_is_not_executable_blocks_commits()
    {
        if (OperatingSystem.IsWindows()) return; // git runs hooks by name there
        FreezeIntent();
        Stage();
        File.SetUnixFileMode(_repo.HookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("pre-commit hook", err);
        Assert.Equal(0, _reviews);
    }

    [Fact]
    public void A_redirected_hooks_path_blocks_commits()
    {
        FreezeIntent();
        Stage();
        var empty = Path.Combine(_root, ".git", "empty-hooks");
        Directory.CreateDirectory(empty);
        Proc.Git(_root, "config", "core.hooksPath", empty);
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("pre-commit hook", err);
        Assert.Equal(0, _reviews);
    }

    [Fact]
    public void Git_apply_cannot_rewrite_the_rules()
    {
        FreezeIntent();
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { BlockOn = [], Confirm = "after" }, Repo.Json));
        var patch = Path.Combine(_root, "..", "p-" + Guid.NewGuid().ToString("N") + ".patch");
        File.WriteAllText(patch, Proc.Git(_root, "diff"));
        Proc.Git(_root, "checkout", "--", ".kaldor/rules.json");
        Proc.Git(_root, "apply", patch);
        File.Delete(patch);
        Stage();
        Assert.Equal(0, Commit().Code);
        Assert.Equal(Rules.Default.BlockOn, _seen!.BlockOn);
    }

    [Fact]
    public void Removing_rules_json_does_not_turn_Kaldor_off()
    {
        Proc.Git(_root, "rm", "-q", ".kaldor/rules.json");
        var (code, err) = Hook("Write", new { file_path = Path.Combine(_root, "a.txt"), content = "x" });
        Assert.Equal(2, code);
        Assert.Contains("No frozen intent", err);
    }

    [Fact]
    public void Committing_a_rules_change_needs_approve()
    {
        FreezeIntent();
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { BlockOn = [] }, Repo.Json));
        Proc.Git(_root, "add", "-f", ".kaldor/rules.json");
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("Atypical.Kaldor approve", err);
        Assert.Equal(0, _reviews);

        var state = _repo.LoadState();
        state.Passes.Add(new Pass(Repo.Hash(_repo.StagedDiff()), state.IntentHash!, "approve"));
        _repo.SaveState(state);
        Assert.Equal(0, Commit().Code);
    }

    [Fact]
    public void Confirm_after_in_the_work_tree_only_is_ignored()
    {
        SetConfirm("after", commit: false);
        FreezeIntent(confirm: false);
        Stage();
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("not confirmed", err);
    }

    [Fact]
    public void Uncommitted_setup_uses_the_work_tree_rules()
    {
        var root = Path.Combine(Path.GetTempPath(), "kaldor-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".kaldor"));
            Proc.Git(root, "init", "-q", "-b", "main");
            File.WriteAllText(Path.Combine(root, ".kaldor", "rules.json"), JsonSerializer.Serialize(Rules.Default with { BlockOn = ["scope_creep"] }, Repo.Json));
            var repo = Repo.Find(root)!;
            Assert.True(repo.Enabled);
            Assert.Equal(["scope_creep"], repo.LoadRules().BlockOn);
        }
        finally { TestRepo.Delete(root); }
    }

    (int Code, string Err) Hook(string tool, object toolInput, string? cwd = null)
    {
        var input = JsonSerializer.Serialize(new { cwd = cwd ?? _root, tool_name = tool, tool_input = toolInput, hook_event_name = "PreToolUse" });
        var err = new StringWriter();
        var code = Gate.Run(input, err, FakeReview, new Dictionary<string, string?>());
        return (code, err.ToString());
    }

    (int Code, string Err) Commit(string command = "git commit -m change", string? cwd = null) => Hook("Bash", new { command }, cwd);

    void FreezeIntent(string text = "# Intent\nGoal: change a.txt\n", bool confirm = true)
    {
        File.WriteAllText(_repo.IntentPath, text);
        Assert.Equal(0, Commands.Freeze(_root));
        if (confirm) Commands.MarkConfirmed(_repo, text); // what `kaldor intent confirm` records once the user says yes
    }

    [Fact]
    public void Commits_wait_for_the_user_to_confirm_the_intent()
    {
        FreezeIntent(confirm: false);
        Assert.Equal(0, Hook("Write", new { file_path = Path.Combine(_root, "a.txt"), content = "x" }).Code);
        Stage();

        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("`dnx Atypical.Kaldor intent confirm` in a terminal outside Claude Code", err);
        Assert.Equal(0, _reviews);

        Commands.MarkConfirmed(_repo, File.ReadAllText(_repo.IntentPath));
        Assert.Equal(0, Commit().Code);
        Assert.Equal(1, _reviews);
    }

    /// <summary>Writes the rules and, as the user would, commits them: the gate applies the rules at HEAD.</summary>
    void SetConfirm(string value, bool commit = true)
    {
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { Confirm = value }, Repo.Json));
        if (!commit) return;
        Proc.Git(_root, "add", "-f", ".kaldor/rules.json");
        Proc.Git(_root, "commit", "-q", "--allow-empty", "-m", "rules");
        UserCommitted();
    }

    /// <summary>The user committed from their own terminal, which Kaldor does not review; they approve it.</summary>
    void UserCommitted() => _repo.ApproveCommits(_repo.LoadState(), _repo.UnreviewedCommits(Repo.PushRoots));

    [Fact]
    public void Confirm_after_sends_an_unconfirmed_intent_to_the_reviewer()
    {
        SetConfirm("after");
        FreezeIntent(confirm: false);
        Stage();
        Assert.Equal(0, Commit().Code);
        Assert.Equal(1, _reviews);
        Assert.True(_repo.ReadLog().Single(e => e.Kind == "review").Unconfirmed);
    }

    [Fact]
    public void Confirm_after_still_blocks_on_a_blocking_review()
    {
        SetConfirm("after");
        FreezeIntent(confirm: false);
        Stage();
        _next = Block;
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("Unannounced cache", err);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("before")]
    public void Confirm_before_or_absent_refuses_an_unconfirmed_intent(string? value)
    {
        if (value is not null) SetConfirm(value);
        else
        {
            var rules = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_repo.RulesPath))!.AsObject();
            Assert.True(rules.Remove("confirm")); // an old rules.json has no such key
            File.WriteAllText(_repo.RulesPath, rules.ToJsonString());
            Assert.DoesNotContain("\"confirm\"", File.ReadAllText(_repo.RulesPath));
        }
        FreezeIntent(confirm: false);
        Stage();
        Assert.Equal(2, Commit().Code);
        Assert.Equal(0, _reviews);
    }

    [Fact]
    public void Report_accepts_unconfirmed_before_or_after_the_count()
    {
        _repo.Log(new LogEntry(DateTimeOffset.Now, "review", "pass", Unconfirmed: true));
        _repo.Log(new LogEntry(DateTimeOffset.Now, "review", "pass"));
        foreach (var args in new[] { new[] { "report", "5", "--unconfirmed" }, ["report", "--unconfirmed", "5"] })
        {
            var (code, output, _) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, .. args], _root);
            Assert.Equal(0, code);
            Assert.Equal(1, output.Split("ACCEPTED").Length - 1);
        }
    }

    [Fact]
    public void Commits_after_the_user_confirms_are_not_marked_unconfirmed()
    {
        SetConfirm("after");
        FreezeIntent(confirm: false);
        Stage("one");
        Assert.Equal(0, Commit().Code);
        // `intent confirm` itself needs a terminal (see Agent_cannot_confirm_the_intent), so record what it records.
        Commands.MarkConfirmed(_repo, File.ReadAllText(_repo.IntentPath));
        Stage("two");
        Assert.Equal(0, Commit().Code);
        var reviews = _repo.ReadLog().Where(e => e.Kind == "review").ToList();
        Assert.Equal(2, reviews.Count);
        Assert.True(reviews[0].Unconfirmed);
        Assert.NotEqual(true, reviews[1].Unconfirmed);
    }

    [Fact]
    public void Report_marks_and_filters_unconfirmed_reviews()
    {
        _repo.Log(new LogEntry(DateTimeOffset.Now, "review", "pass", Unconfirmed: true));
        _repo.Log(new LogEntry(DateTimeOffset.Now, "review", "pass"));
        var all = new StringWriter();
        var old = Console.Out;
        try
        {
            Console.SetOut(all);
            Commands.Report(_root, 10);
            var both = all.ToString();
            all.GetStringBuilder().Clear();
            Commands.Report(_root, 10, unconfirmedOnly: true);
            Assert.Equal(2, both.Split("ACCEPTED").Length - 1);
            Assert.Equal(1, both.Split("UNCONFIRMED").Length - 1);
            Assert.Equal(1, all.ToString().Split("ACCEPTED").Length - 1);
        }
        finally { Console.SetOut(old); }
    }

    [Fact]
    public void Doctor_rejects_an_unknown_confirm_value()
    {
        SetConfirm("sometimes");
        var (_, output, _) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "doctor"], _root);
        Assert.Contains("FAIL  rules.json is valid", output);
    }

    [Fact]
    public void Doctor_warns_when_rules_json_differs_from_HEAD()
    {
        string Doctor() => Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "doctor"], _root).Out;
        Assert.DoesNotContain("uncommitted changes", Doctor());

        // core.autocrlf: CRLF in the work tree, LF in the blob. Same rules, so no warning.
        File.WriteAllText(_repo.RulesPath, File.ReadAllText(_repo.RulesPath).Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Assert.DoesNotContain("uncommitted changes", Doctor());

        SetConfirm("after", commit: false);
        Assert.Contains("rules.json has uncommitted changes", Doctor());
    }

    [Fact]
    public void Doctor_validates_the_work_tree_rules_too()
    {
        SetConfirm("aftr", commit: false);
        var output = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "doctor"], _root).Out;
        Assert.Contains("ok    rules.json is valid", output);
        Assert.Contains("FAIL  rules.json in the work tree is valid", output);
    }

    [Fact]
    public void A_replace_ref_cannot_change_the_rules_at_HEAD()
    {
        SetConfirm("before");
        var head = Proc.Git(_root, "rev-parse", "HEAD:.kaldor/rules.json").Trim();
        var (_, evil, _) = Proc.Run("git", ["hash-object", "-w", "--stdin"], _root,
            stdin: JsonSerializer.Serialize(Rules.Default with { Confirm = "after" }, Repo.Json));
        Proc.Git(_root, "replace", head, evil.Trim());
        Assert.Equal("before", _repo.LoadRules().Confirm);
    }

    [Fact]
    public void A_replace_ref_cannot_hide_a_staged_rules_change()
    {
        SetConfirm("before");
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { Confirm = "after" }, Repo.Json));
        Proc.Git(_root, "add", "-f", ".kaldor/rules.json");
        var head = Proc.Git(_root, "rev-parse", "HEAD:.kaldor/rules.json").Trim();
        var staged = Proc.Git(_root, "rev-parse", ":.kaldor/rules.json").Trim();
        Proc.Git(_root, "replace", head, staged);
        Assert.True(_repo.StagedKaldorChange());
    }

    [Fact]
    public void A_textconv_cannot_hide_a_staged_rules_change()
    {
        FreezeIntent();
        Proc.Git(_root, "config", "diff.k.textconv", "echo same");
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "*.json diff=k\n*.txt diff=k\n");
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { BlockOn = [] }, Repo.Json));
        Proc.Git(_root, "add", "-f", ".kaldor/rules.json");
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("Atypical.Kaldor approve", err);
        Assert.Equal(0, _reviews);

        Proc.Git(_root, "reset", "-q", ".kaldor/rules.json");
        Stage("real change");
        Assert.Contains("real change", _repo.StagedDiff());
    }

    [Fact]
    public void Without_rules_json_at_HEAD_a_guarded_repository_uses_the_default_rules()
    {
        Proc.Git(_root, "rm", "-q", "--cached", ".kaldor/rules.json");
        Proc.Git(_root, "commit", "-q", "-m", "no rules");
        UserCommitted();
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { BlockOn = ["scope_creep"] }, Repo.Json));
        FreezeIntent();
        Stage();
        Assert.Equal(0, Commit().Code);
        Assert.Equal(Rules.Default.BlockOn, _seen!.BlockOn);
    }

    static readonly Dictionary<string, string> NoClaude = new() { ["CLAUDECODE"] = "" };

    /// <summary>Runs the real CLI in a pseudo-terminal (util-linux <c>script</c>) answering "y"; null where that is not available.</summary>
    (int Code, string Output)? InPseudoTerminal(string claudeCode, params string[] args)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return null;
        var cli = $"dotnet {typeof(Commands).Assembly.Location} {string.Join(' ', args)}";
        var (code, output, err) = Proc.Run("script", ["-qec", cli, "/dev/null"], _root, stdin: "y\n",
            env: new Dictionary<string, string> { ["CLAUDECODE"] = claudeCode });
        return (code, output + err);
    }

    [Fact]
    public void Agent_cannot_confirm_through_a_pseudo_terminal()
    {
        FreezeIntent(confirm: false);
        if (InPseudoTerminal("1", "intent", "confirm") is not { } refused) return;
        Assert.NotEqual(0, refused.Code);
        Assert.Contains("inside a Claude Code session", refused.Output);
        Assert.NotEqual(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);

        // The same route confirms outside Claude Code, so the refusal above is the variable and nothing else.
        var allowed = InPseudoTerminal("", "intent", "confirm")!.Value;
        Assert.Equal(0, allowed.Code);
        Assert.Equal(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
    }

    [Fact]
    public void Agent_cannot_confirm_all_through_a_pseudo_terminal()
    {
        Queue("a.md", Draft);
        if (InPseudoTerminal("1", "intent", "confirm", "--all") is not { } refused) return;
        Assert.NotEqual(0, refused.Code);
        Assert.Contains("inside a Claude Code session", refused.Output);
        Assert.Empty(_repo.LoadPreconfirmed());
    }

    [Fact]
    public void Agent_cannot_approve_or_accept_through_a_pseudo_terminal()
    {
        FreezeIntent();
        Stage();
        if (InPseudoTerminal("1", "approve") is not { } refused) return;
        Assert.NotEqual(0, refused.Code);
        Assert.Contains("inside a Claude Code session", refused.Output);
        Assert.DoesNotContain(_repo.LoadState().Passes, p => p.By == "approve");

        var before = File.ReadAllText(_repo.IntentPath);
        Assert.Equal(0, Commands.ProposeAmendment(_root, "Also add a cache."));
        var accept = InPseudoTerminal("1", "intent", "amend", "--accept")!.Value;
        Assert.NotEqual(0, accept.Code);
        Assert.Contains("inside a Claude Code session", accept.Output);
        Assert.Equal(before, File.ReadAllText(_repo.IntentPath));
        Assert.Equal(0, InPseudoTerminal("", "intent", "amend", "--accept")!.Value.Code);
    }

    [Theory]
    [InlineData("unset CLAUDECODE; bash x.sh")]
    [InlineData("env -u CLAUDECODE bash x.sh")]
    [InlineData("env -i bash x.sh")]
    [InlineData("CLAUDECODE= bash x.sh")]
    [InlineData("bash -c 'unset CLAUDECODE'")]
    [InlineData("exec -c bash x.sh")]
    [InlineData("systemd-run --user bash x.sh")]
    public void The_gate_refuses_commands_that_clear_CLAUDECODE(string command)
    {
        var (code, err) = Commit(command);
        Assert.Equal(2, code);
        Assert.Contains("Clearing CLAUDECODE", err);
    }

    [Theory]
    [InlineData("echo \"unset CLAUDECODE\"")]
    [InlineData("CLAUDECODE=1 git status")]
    public void The_gate_allows_mentioning_or_keeping_CLAUDECODE(string command) =>
        Assert.Equal(0, Commit(command).Code);

    [Fact]
    public void Agent_cannot_confirm_the_intent()
    {
        Assert.Equal(2, Commit("kaldor intent confirm").Code);
        Assert.Equal(2, Commit("dnx --yes Atypical.Kaldor intent confirm").Code);

        // The command itself needs a person at an interactive terminal.
        FreezeIntent(confirm: false);
        var (code, output, err) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "intent", "confirm"], _root, stdin: "y\n", env: NoClaude);
        Assert.Equal(1, code);
        Assert.NotEqual(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
        // It refuses before showing anything, and says what to run instead.
        Assert.Equal("", output);
        Assert.DoesNotContain("Goal:", err);
        Assert.Contains("`dnx Atypical.Kaldor intent confirm` in a terminal outside Claude Code", err);
        Assert.Contains("or ask Claude to run it: Claude Code will ask you to allow it", err);
    }

    /// <summary>The real CLI inside Claude Code (CLAUDECODE=1, stdin redirected), with <paramref name="ticket"/> as KALDOR_TICKET.</summary>
    (int Code, string Out, string Err) Cli(string? ticket, params string[] args) =>
        Proc.Run("dotnet", [typeof(Commands).Assembly.Location, .. args], _root, stdin: "",
            env: new Dictionary<string, string> { ["CLAUDECODE"] = "1", ["KALDOR_TICKET"] = ticket ?? "" });

    static void AssertRefused((int Code, string Out, string Err) run)
    {
        Assert.Equal(1, run.Code);
        Assert.Equal("", run.Out);
        Assert.Contains("inside a Claude Code session", run.Err);
    }

    string IntentSubject => Repo.Hash(File.ReadAllText(_repo.IntentPath));

    [Fact]
    public void A_ticket_confirms_the_intent_once_without_a_terminal()
    {
        FreezeIntent(confirm: false);
        var ticket = Commands.IssueTicket(_repo, "intent confirm", IntentSubject);
        Assert.DoesNotContain(ticket, File.ReadAllText(_repo.StatePath));

        var (code, output, _) = Cli(ticket, "intent", "confirm");
        Assert.Equal(0, code);
        Assert.DoesNotContain("[y/N]", output);
        Assert.Equal(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
        Assert.Null(_repo.LoadState().Pending);
        Assert.Contains("(Claude Code prompt)", Cli(null, "report").Out);

        // Used once: the same ticket again is refused.
        _repo.SaveState(_repo.LoadState() with { ConfirmedHash = null });
        AssertRefused(Cli(ticket, "intent", "confirm"));
        Assert.Null(_repo.LoadState().ConfirmedHash);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("wrong")]
    [InlineData("expired")]
    [InlineData("other command")]
    [InlineData("intent changed")]
    public void A_bad_ticket_is_refused_before_printing_anything(string kind)
    {
        FreezeIntent(confirm: false);
        var ticket = Commands.IssueTicket(_repo, kind == "other command" ? "approve" : "intent confirm", IntentSubject);
        if (kind == "expired")
        {
            var state = _repo.LoadState();
            _repo.SaveState(state with { Pending = state.Pending! with { Expires = DateTimeOffset.UtcNow.AddSeconds(-1) } });
        }
        if (kind == "intent changed") FreezeIntent("# Intent\nGoal: change b.txt\n", confirm: false);

        AssertRefused(Cli(kind switch { "none" => null, "wrong" => new string('0', 64), _ => ticket }, "intent", "confirm"));
        Assert.NotEqual(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
    }

    /// <summary>The gate for <paramref name="command"/> in <paramref name="mode"/>, with its stdout.</summary>
    (int Code, string Err, string Out) AskHook(string command, string? mode = "default", string tool = "Bash")
    {
        var input = JsonSerializer.Serialize(new
        {
            cwd = _root, tool_name = tool, tool_input = new { command }, hook_event_name = "PreToolUse", permission_mode = mode,
        });
        var (err, output) = (new StringWriter(), new StringWriter());
        var code = Gate.Run(input, err, FakeReview, new Dictionary<string, string?>(), output);
        return (code, err.ToString(), output.ToString());
    }

    /// <summary>The ticket, the prompt text and the rewritten command of an <c>ask</c> decision.</summary>
    static (string Ticket, string Reason, string Command) Asked((int Code, string Err, string Out) hook)
    {
        Assert.Equal(0, hook.Code);
        Assert.Equal("", hook.Err);
        var output = System.Text.Json.Nodes.JsonNode.Parse(hook.Out)!["hookSpecificOutput"]!;
        Assert.Equal("PreToolUse", output["hookEventName"]!.GetValue<string>());
        Assert.Equal("ask", output["permissionDecision"]!.GetValue<string>());
        var command = output["updatedInput"]!["command"]!.GetValue<string>();
        var ticket = System.Text.RegularExpressions.Regex.Match(command, "KALDOR_TICKET='?([0-9a-f]{64})").Groups[1].Value;
        Assert.NotEqual("", ticket);
        return (ticket, output["permissionDecisionReason"]!.GetValue<string>(), command);
    }

    [Fact]
    public void The_gate_asks_the_user_and_the_ticket_it_hands_out_confirms_once()
    {
        FreezeIntent(confirm: false);
        var (ticket, reason, command) = Asked(AskHook("kaldor intent confirm"));
        Assert.Contains("Goal: change a.txt", reason);
        Assert.Equal($"KALDOR_TICKET={ticket} kaldor intent confirm", command);
        Assert.DoesNotContain(ticket, File.ReadAllText(_repo.StatePath));

        Assert.Equal(0, Cli(ticket, "intent", "confirm").Code);
        Assert.Equal(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
        Assert.Contains("(Claude Code prompt)", Cli(null, "report").Out);

        _repo.SaveState(_repo.LoadState() with { ConfirmedHash = null });
        AssertRefused(Cli(ticket, "intent", "confirm"));
        Assert.Null(_repo.LoadState().ConfirmedHash);
    }

    [Fact]
    public void Only_the_latest_ticket_is_valid()
    {
        FreezeIntent(confirm: false);
        var first = Asked(AskHook("kaldor intent confirm")).Ticket;
        Asked(AskHook("kaldor intent confirm"));
        AssertRefused(Cli(first, "intent", "confirm"));
        Assert.Null(_repo.LoadState().ConfirmedHash);
    }

    [Theory]
    [InlineData("Bash", "dnx --yes Atypical.Kaldor intent confirm", "KALDOR_TICKET=")]
    [InlineData("Bash", "dnx Atypical.Kaldor intent confirm", "KALDOR_TICKET=")]
    [InlineData("PowerShell", "kaldor intent confirm", "$env:KALDOR_TICKET='")]
    public void The_prompt_route_takes_dnx_and_PowerShell(string tool, string command, string prefix)
    {
        FreezeIntent(confirm: false);
        var asked = Asked(AskHook(command, tool: tool));
        Assert.StartsWith(prefix + asked.Ticket, asked.Command);
        Assert.EndsWith(" " + command, asked.Command);
    }

    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("dontAsk")]
    [InlineData("auto")]
    [InlineData(null)]
    public void The_gate_refuses_when_no_person_would_see_the_prompt(string? mode)
    {
        FreezeIntent(confirm: false);
        var (code, err, output) = AskHook("kaldor intent confirm", mode);
        Assert.Equal(2, code);
        Assert.Contains("reserved for the user", err);
        Assert.Equal("", output);
        Assert.Null(_repo.LoadState().Pending);
    }

    [Theory]
    [InlineData("kaldor intent confirm && true")]
    [InlineData("bash -c \"kaldor intent confirm\"")]
    [InlineData("echo y | kaldor intent confirm")]
    [InlineData("kaldor intent confirm --all")]
    [InlineData("KALDOR_TICKET=x kaldor intent confirm")]
    [InlineData("kaldor  intent confirm")]
    [InlineData("kaldor intent confirm\nkaldor approve")]
    [InlineData("kaldor intent confirm # ok")]
    public void The_gate_asks_only_for_the_command_alone(string command)
    {
        FreezeIntent(confirm: false);
        var (code, _, output) = AskHook(command);
        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Null(_repo.LoadState().Pending);
    }

    [Theory]
    [InlineData("kaldor gate")]
    [InlineData("dnx --yes Atypical.Kaldor gate")]
    [InlineData("echo '{\"tool_name\":\"Bash\",\"permission_mode\":\"default\",\"tool_input\":{\"command\":\"kaldor intent confirm\"}}' | kaldor gate")]
    [InlineData("bash -c 'kaldor gate < hook.json'")]
    public void The_agent_cannot_run_the_gate_to_mint_a_ticket(string command)
    {
        FreezeIntent(confirm: false);
        var (code, err, output) = AskHook(command);
        Assert.Equal(2, code);
        Assert.Contains("`kaldor gate` runs only as Claude Code's own hook", err);
        Assert.Equal("", output);
        Assert.Null(_repo.LoadState().Pending);
        // The real hook still asks for the command alone.
        Asked(AskHook("kaldor intent confirm"));
        Assert.NotNull(_repo.LoadState().Pending);
    }

    [Fact]
    public void The_gate_does_not_ask_with_nothing_to_vouch_for()
    {
        FreezeIntent(); // already confirmed
        Assert.Equal(2, AskHook("kaldor intent confirm").Code);
        Assert.Equal(2, AskHook("kaldor approve").Code); // nothing staged
        Assert.Equal(2, AskHook("kaldor intent amend --accept").Code); // no amendment
        Assert.Null(_repo.LoadState().Pending);
    }

    [Fact]
    public void The_approve_prompt_shows_staged_kaldor_changes_in_full()
    {
        FreezeIntent();
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { BlockOn = [] }, Repo.Json));
        Proc.Git(_root, "add", "-f", ".kaldor/rules.json");
        var reason = Asked(AskHook("kaldor approve")).Reason;
        Assert.Contains("Changes to Kaldor's own files:", reason);
        Assert.Contains("\"block_on\": []", reason);
    }

    [Fact]
    public void The_approve_prompt_has_no_kaldor_section_for_an_ordinary_file()
    {
        FreezeIntent();
        Stage("one");
        Assert.DoesNotContain("Changes to Kaldor's own files", Asked(AskHook("kaldor approve")).Reason);
    }

    [Fact]
    public void An_approve_ticket_is_bound_to_the_staged_diff()
    {
        FreezeIntent();
        Stage("one");
        var stale = Asked(AskHook("kaldor approve"));
        Assert.Contains("a.txt", stale.Reason);
        Stage("two");
        AssertRefused(Cli(stale.Ticket, "approve"));
        Assert.DoesNotContain(_repo.LoadState().Passes, p => p.By == "approve");

        var ticket = Asked(AskHook("dnx Atypical.Kaldor approve")).Ticket;
        Assert.Equal(0, Cli(ticket, "approve").Code);
        Assert.Contains(_repo.LoadState().Passes, p => p.By == "approve" && p.DiffHash == Repo.Hash(_repo.StagedDiff()));
        Assert.Contains("(Claude Code prompt)", Cli(null, "report").Out);
    }

    [Fact]
    public void An_amendment_ticket_is_bound_to_the_amendment()
    {
        FreezeIntent();
        Assert.Equal(0, Commands.ProposeAmendment(_root, "Also add a cache."));
        var stale = Asked(AskHook("kaldor intent amend --accept"));
        Assert.Contains("Also add a cache.", stale.Reason);
        Assert.Equal(0, Commands.ProposeAmendment(_root, "Also add a queue."));
        var before = File.ReadAllText(_repo.IntentPath);
        AssertRefused(Cli(stale.Ticket, "intent", "amend", "--accept"));
        Assert.Equal(before, File.ReadAllText(_repo.IntentPath));

        var ticket = Asked(AskHook("kaldor intent amend --accept")).Ticket;
        Assert.Equal(0, Cli(ticket, "intent", "amend", "--accept").Code);
        Assert.Contains("Also add a queue.", File.ReadAllText(_repo.IntentPath));
        Assert.Equal(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
    }

    [Fact]
    public void The_rewritten_input_keeps_the_other_tool_fields()
    {
        FreezeIntent(confirm: false);
        var input = JsonSerializer.Serialize(new
        {
            cwd = _root, tool_name = "Bash", tool_input = new { command = "kaldor intent confirm", timeout = 600000 },
            hook_event_name = "PreToolUse", permission_mode = "default",
        });
        var output = new StringWriter();
        Assert.Equal(0, Gate.Run(input, new StringWriter(), FakeReview, new Dictionary<string, string?>(), output));
        var updated = System.Text.Json.Nodes.JsonNode.Parse(output.ToString())!["hookSpecificOutput"]!["updatedInput"]!;
        Assert.Equal(600000, updated["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void Confirm_all_leaves_a_pending_ticket_alone()
    {
        FreezeIntent(confirm: false);
        var ticket = Asked(AskHook("kaldor intent confirm")).Ticket;
        Queue("a.md", Draft);
        Assert.NotEqual(0, Cli(ticket, "intent", "confirm", "--all").Code);
        Assert.Equal(0, Cli(ticket, "intent", "confirm").Code);
    }

    [Fact]
    public void A_ticket_for_one_command_does_not_run_another()
    {
        FreezeIntent(confirm: false);
        Stage();
        var ticket = Asked(AskHook("kaldor intent confirm")).Ticket;
        AssertRefused(Cli(ticket, "approve"));
        AssertRefused(Cli(ticket, "intent", "confirm")); // used up by the attempt
        Assert.DoesNotContain(_repo.LoadState().Passes, p => p.By == "approve");
    }

    const string Draft = "# Intent\nGoal: change a.txt\n";

    void Queue(string name, string text)
    {
        Directory.CreateDirectory(_repo.QueueDir);
        File.WriteAllText(Path.Combine(_repo.QueueDir, name), text);
    }

    [Fact]
    public void A_preconfirmed_intent_commits_without_confirmation_and_only_once()
    {
        Queue("a.md", Draft);
        Assert.Equal(0, Commands.ConfirmDrafts(_repo, _ => true));
        FreezeIntent(Draft, confirm: false);
        Stage();
        Assert.Equal(0, Commit().Code);
        Assert.Equal(1, _reviews);
        Assert.Equal(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
        Assert.Empty(_repo.LoadPreconfirmed());

        Proc.Git(_root, "commit", "-q", "-m", "task one");
        // AC4: the same text for a second task needs a new confirmation.
        File.WriteAllText(_repo.IntentPath, Draft + "second\n");
        Commands.Freeze(_root);
        File.WriteAllText(_repo.IntentPath, Draft);
        Commands.Freeze(_root);
        Assert.NotEqual(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
        Stage("more");
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("The user has not confirmed the frozen intent yet", err);
    }

    string AddWorktree(string name)
    {
        var path = _root + "-" + name;
        Proc.Git(_root, "worktree", "add", "-q", path, "-b", name);
        _worktrees.Add(path);
        return path;
    }

    readonly List<string> _worktrees = [];

    string FreezeIn(string worktree, string text)
    {
        Directory.CreateDirectory(Path.Combine(worktree, ".kaldor"));
        File.WriteAllText(Path.Combine(worktree, ".kaldor", "intent.md"), text);
        Assert.Equal(0, Commands.Freeze(worktree));
        return worktree;
    }

    static bool Confirmed(string worktree) => Repo.Find(worktree)!.LoadState() is { ConfirmedHash: not null } s && s.ConfirmedHash == s.IntentHash;

    [Fact]
    public void A_preconfirmation_from_the_main_checkout_confirms_a_freeze_in_a_new_linked_worktree_once()
    {
        Queue("a.md", Draft);
        Assert.Equal(0, Commands.ConfirmDrafts(_repo, _ => true));
        var one = AddWorktree("one");
        var two = AddWorktree("two");
        FreezeIn(one, Draft);
        Assert.True(Confirmed(one));
        FreezeIn(two, Draft);
        Assert.False(Confirmed(two));
    }

    [Fact]
    public void Re_freezing_already_confirmed_text_does_not_spend_another_worktrees_acceptance()
    {
        Queue("a.md", Draft);
        Commands.ConfirmDrafts(_repo, _ => true);
        var one = AddWorktree("one");
        FreezeIn(one, Draft);
        Commands.ConfirmDrafts(_repo, _ => true); // accepted again for the next worker
        FreezeIn(one, Draft); // one already has it confirmed
        Assert.Equal([Repo.Hash(Draft)], _repo.LoadPreconfirmed());
    }

    [Fact]
    public void Two_worktrees_racing_on_one_acceptance_get_it_once()
    {
        var hash = Repo.Hash(Draft);
        _repo.SavePreconfirmed([hash]);
        var repos = new[] { _repo, Repo.Find(AddWorktree("one"))!, Repo.Find(AddWorktree("two"))! };
        var wins = 0;
        Parallel.For(0, 8, i => { if (repos[i % 3].TakePreconfirmed(hash)) Interlocked.Increment(ref wins); });
        Assert.Equal(1, wins);
        Assert.Empty(_repo.LoadPreconfirmed());
    }

    [Fact]
    public void A_malformed_shared_file_is_empty_and_taking_from_it_leaves_it_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_repo.PreconfirmedPath)!);
        File.WriteAllText(_repo.PreconfirmedPath, "{nope");
        Assert.Empty(_repo.LoadPreconfirmed());
        Assert.False(_repo.TakePreconfirmed("x"));
        Assert.Equal("{nope", File.ReadAllText(_repo.PreconfirmedPath));
    }

    [Fact]
    public void Agent_cannot_write_the_shared_preconfirmed_file_from_a_linked_worktree()
    {
        var one = AddWorktree("one");
        var (code, err) = Hook("Write", new { file_path = _repo.PreconfirmedPath, content = "[]" }, one);
        Assert.Equal(2, code);
        Assert.Contains("written by the kaldor tool only", err);
        Assert.Equal(2, Hook("Bash", new { command = $"echo '[]' > \"{_repo.PreconfirmedPath}\"" }, one).Code);
    }

    [Fact]
    public void An_intent_that_differs_by_one_byte_is_not_preconfirmed()
    {
        Queue("a.md", Draft);
        Commands.ConfirmDrafts(_repo, _ => true);
        FreezeIntent(Draft + " ", confirm: false);
        Stage();
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("The user has not confirmed the frozen intent yet", err);
        Assert.Equal(0, _reviews);
    }

    [Fact]
    public void Declined_drafts_are_not_preconfirmed_and_confirm_all_logs_one_entry_per_accepted_draft()
    {
        Queue("a.md", Draft);
        Queue("b.md", "# Intent\nGoal: other\n");
        Assert.Equal(0, Commands.ConfirmDrafts(_repo, _ => true));
        Assert.Equal(2, _repo.ReadAllLogs().Count(x => x.Entry.Kind == "confirm" && x.Entry.Summary!.StartsWith("Pre-confirmed by")));

        var asked = 0;
        Commands.ConfirmDrafts(_repo, _ => asked++ == 1); // accepts only b.md, and replaces the earlier set
        Assert.Equal([Repo.Hash("# Intent\nGoal: other\n")], _repo.LoadPreconfirmed());
        FreezeIntent(Draft, confirm: false);
        Assert.NotEqual(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
    }

    [Fact]
    public void Confirm_all_with_an_empty_queue_exits_1()
    {
        Assert.Equal(1, Commands.ConfirmDrafts(_repo, _ => true));
    }

    [Fact]
    public void Agent_cannot_confirm_all()
    {
        Assert.Equal(2, Commit("kaldor intent confirm --all").Code);
        Assert.Equal(2, Commit("dnx --yes Atypical.Kaldor intent confirm --all").Code);
        Assert.Equal(2, Commit("ls && kaldor intent confirm --all").Code);

        Queue("a.md", Draft);
        var (code, output, err) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "intent", "confirm", "--all"], _root, stdin: "y\n", env: NoClaude);
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("intent confirm --all", err);
        Assert.Empty(_repo.LoadPreconfirmed());
    }

    [Fact]
    public void The_agent_can_write_queue_drafts_but_nothing_else_in_kaldor()
    {
        string P(string rel) => Path.Combine(_root, rel);
        Assert.Equal(0, Hook("Write", new { file_path = P(".kaldor/queue/a.md"), content = "x" }).Code);
        Assert.Equal(2, Commit("cp a.txt .kaldor/queue/b.md").Code); // shell writes could plant a link: Edit and Write only
        Assert.Equal(2, Hook("Write", new { file_path = P(".kaldor/queue/a.json"), content = "x" }).Code);
        Assert.Equal(2, Hook("Write", new { file_path = P(".kaldor/queue/sub/a.md"), content = "x" }).Code);
        Assert.Equal(2, Hook("Write", new { file_path = P(".kaldor/queue/../rules.json"), content = "x" }).Code);
        Assert.Equal(2, Hook("Write", new { file_path = P(".kaldor/rules.json"), content = "x" }).Code);
        Assert.Equal(2, Commit("cp a.txt .kaldor/queue/../rules.json").Code);
        Assert.Equal(2, Commit("ln -s ../.git/kaldor .kaldor/queue").Code);
        Assert.Equal(2, Commit("ln -s ../.git/kaldor/state.json .kaldor/queue/x.md").Code);
        Assert.Equal(2, Commit("cp -s ../.git/kaldor/state.json .kaldor/queue/x.md").Code);
        Assert.Equal(2, Commit("ln ../.git/kaldor/state.json .kaldor/queue/x.md").Code);
        Assert.Equal(2, Commit("echo x > .kaldor/queue/x.md").Code);
    }

    [Fact]
    public void A_queue_draft_is_never_a_staged_kaldor_change()
    {
        FreezeIntent();
        Queue("a.md", Draft);
        Stage();
        Proc.Git(_root, "add", "-A"); // the draft is gitignored, so it is not staged
        Assert.DoesNotContain("queue", Proc.Git(_root, "diff", "--cached", "--name-only"));
        Assert.False(_repo.StagedKaldorChange());
        Assert.Equal(0, Commit().Code);
        Assert.Equal(1, _reviews);
    }

    [Fact]
    public void Intent_wait_returns_once_the_user_confirms()
    {
        const string Text = "# Intent\nGoal: change a.txt\n";
        FreezeIntent(Text, confirm: false);
        // The user confirms from another terminal while the agent waits.
        var user = new Thread(() => { Thread.Sleep(300); Commands.MarkConfirmed(_repo, Text); });
        user.Start();

        Assert.Equal(0, Commands.WaitForConfirmation(_root, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50)));
        user.Join();
    }

    [Fact]
    public void Intent_wait_gives_up_on_timeout_or_when_there_is_nothing_to_wait_for()
    {
        Assert.Throws<KaldorException>(() => Commands.WaitForConfirmation(_root, TimeSpan.FromSeconds(1)));

        FreezeIntent(confirm: false);
        Assert.Equal(1, Commands.WaitForConfirmation(_root, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50)));

        File.WriteAllText(_repo.IntentPath, "# Intent\nGoal: something else\n");
        Assert.Throws<KaldorException>(() => Commands.WaitForConfirmation(_root, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Approve_refuses_without_a_terminal_before_printing_anything()
    {
        FreezeIntent();
        Stage();
        var (code, output, err) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "approve"], _root, stdin: "y\n", env: NoClaude);
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("`dnx Atypical.Kaldor approve` in a terminal outside Claude Code", err);
        Assert.Contains("or ask Claude to run it: Claude Code will ask you to allow it", err);
    }

    [Fact]
    public void An_accepted_amendment_is_confirmed()
    {
        FreezeIntent();
        Assert.Equal(0, Commands.ProposeAmendment(_root, "Also add a cache."));
        Commands.ApplyAmendment(_repo, "Also add a cache.");
        var state = _repo.LoadState();
        Assert.Equal(Repo.Hash(File.ReadAllText(_repo.IntentPath)), state.IntentHash);
        Assert.Equal(state.IntentHash, state.ConfirmedHash);
    }

    [Fact]
    public void An_approved_diff_does_not_wait_for_confirmation()
    {
        FreezeIntent(confirm: false);
        Stage();
        // What `kaldor approve` records once the user says yes.
        var state = _repo.LoadState();
        state.Passes.Add(new Pass(Repo.Hash(_repo.StagedDiff()), state.IntentHash!, "approve"));
        _repo.SaveState(state);
        Assert.Equal(0, Commit().Code);
        Assert.Equal(0, _reviews);
    }

    void Stage(string content = "hello")
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), content);
        Proc.Git(_root, "add", "a.txt");
    }

    [Fact]
    public void Edit_without_frozen_intent_is_denied()
    {
        var (code, err) = Hook("Write", new { file_path = Path.Combine(_root, "a.txt"), content = "x" });
        Assert.Equal(2, code);
        Assert.Contains("No frozen intent", err);
    }

    [Fact]
    public void Writing_the_intent_itself_is_allowed() =>
        Assert.Equal(0, Hook("Write", new { file_path = _repo.IntentPath, content = "x" }).Code);

    [Fact]
    public void Agent_cannot_write_kaldor_state()
    {
        FreezeIntent();
        Assert.Equal(2, Hook("Write", new { file_path = _repo.StatePath, content = "{}" }).Code);
        Assert.Equal(2, Hook("Edit", new { file_path = ".kaldor/log/x.json" }).Code);
    }

    [Fact]
    public void Commit_without_intent_is_denied()
    {
        Stage();
        Assert.Equal(2, Commit().Code);
        Assert.Equal(0, _reviews);
    }

    [Fact]
    public void Passing_review_lets_the_commit_through_once_per_diff()
    {
        FreezeIntent();
        Stage();
        Assert.Equal(0, Commit().Code);
        Assert.Equal(0, Commit().Code);
        Assert.Equal(1, _reviews);
        Assert.Single(_repo.ReadLog(), e => e.Kind == "review" && e.Verdict == "pass");
    }

    [Fact]
    public void Blocking_review_denies_with_the_deviations()
    {
        FreezeIntent();
        Stage();
        _next = Block;
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("Unannounced cache", err);
        Assert.Contains("Unannounced cache", Commit().Err);
        Assert.Equal(1, _reviews);
    }

    [Fact]
    public void Change_after_review_triggers_a_new_review()
    {
        FreezeIntent();
        Stage("one");
        Assert.Equal(0, Commit().Code);
        Stage("two");
        _next = Block;
        Assert.Equal(2, Commit().Code);
        Assert.Equal(2, _reviews);
    }

    [Fact]
    public void Intent_rewritten_after_freeze_is_denied()
    {
        FreezeIntent();
        Stage();
        File.WriteAllText(_repo.IntentPath, "# Intent\nGoal: change a.txt and add a cache\n");
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains("changed after it was frozen", err);
        Assert.Equal(0, _reviews);
    }

    [Fact]
    public void Intent_cannot_be_edited_or_refrozen_mid_task()
    {
        FreezeIntent();
        Stage();
        Assert.Equal(2, Hook("Edit", new { file_path = _repo.IntentPath }).Code);
        File.WriteAllText(_repo.IntentPath, "# Intent\nGoal: something else\n");
        Assert.Throws<KaldorException>(() => Commands.Freeze(_root));
    }

    [Fact]
    public void Git_dash_C_from_another_directory_is_reviewed()
    {
        FreezeIntent();
        Stage();
        _next = Block;
        Assert.Equal(2, Commit($"git -C \"{_root}\" commit -m x", cwd: Path.GetTempPath()).Code);
        Assert.Equal(2, Commit($"cd \"{_root}\" && git commit -m x", cwd: Path.GetTempPath()).Code);
    }

    [Fact]
    public void Skipping_git_hooks_is_denied()
    {
        FreezeIntent();
        Stage();
        Assert.Equal(2, Commit("git commit --no-verify -m x").Code);
    }

    [Fact]
    public void Commit_from_a_script_is_caught_by_the_pre_commit_check()
    {
        FreezeIntent();
        Stage();
        _next = Block;
        // The gate cannot see inside ./commit.sh; the git pre-commit hook runs `kaldor check` instead.
        Assert.Equal(0, Commit("./commit.sh").Code);
        Assert.Equal(1, Commands.Check(_root, FakeReview, isAgent: true, TextWriter.Null));
        Assert.Equal(0, Commands.Check(_root, FakeReview, isAgent: false, TextWriter.Null));
    }

    [Fact]
    public void Agent_cannot_approve_or_accept_its_own_amendment()
    {
        Assert.Equal(2, Commit("kaldor approve").Code);
        Assert.Equal(2, Commit("dnx --yes Atypical.Kaldor intent amend --accept").Code);
        // Mentioning them is not running them.
        Assert.Equal(0, Commit("echo \"ask the user to run kaldor approve\"").Code);
    }

    [Theory]
    [InlineData("echo '{}' > .git/kaldor/state.json")]
    [InlineData("echo x>>.git/kaldor/log/forged.json")]
    [InlineData("echo '{}' | tee .git/kaldor/state.json")]
    [InlineData("cp /tmp/forged.json .git/kaldor/state.json")]
    [InlineData("cd .git && rm -rf kaldor")]
    [InlineData("rm -rf .kaldor")]
    [InlineData("sed -i 's/sonnet/haiku/' .kaldor/rules.json")]
    [InlineData("Set-Content -Path .kaldor/rules.json -Value '{}'")]
    [InlineData("sudo dd if=/tmp/x of=.git/kaldor/state.json")]
    public void Shell_cannot_write_kaldor_state_or_rules(string command) =>
        Assert.Equal(2, Hook("Bash", new { command }).Code);

    [Theory]
    [InlineData("cat .kaldor/rules.json .git/kaldor/state.json")]
    [InlineData("cp .kaldor/rules.json /tmp/rules-backup.json")]
    [InlineData("echo 'the state lives in .git/kaldor/state.json' > notes.txt")]
    [InlineData("echo \"a > .kaldor/rules.json\"")]
    [InlineData("cat > .kaldor/intent.md <<'EOF'\n# Intent\nEOF")]
    public void Shell_can_read_and_mention_kaldor_state(string command) =>
        Assert.Equal(0, Hook("Bash", new { command }).Code);

    [Fact]
    public void Edit_tools_cannot_write_in_the_git_directory_state()
    {
        Assert.Equal(2, Hook("Write", new { file_path = Path.Combine(_root, ".git", "kaldor", "state.json"), content = "{}" }).Code);
        Assert.Equal(2, Hook("Edit", new { file_path = ".git/kaldor/log/x.json" }).Code);
    }

    [Fact]
    public void Repositories_without_kaldor_are_left_alone()
    {
        Proc.Git(_root, "rm", "-q", ".kaldor/rules.json");
        Proc.Git(_root, "commit", "-q", "-m", "drop kaldor");
        File.Delete(_repo.GuardedPath); // as the user would
        Stage();
        Assert.Equal(0, Commit().Code);
        Assert.Equal(0, Hook("Write", new { file_path = Path.Combine(_root, "b.txt") }).Code);
    }

    [Fact]
    public void Reviewer_session_is_not_gated()
    {
        Stage();
        var input = JsonSerializer.Serialize(new { cwd = _root, tool_name = "Bash", tool_input = new { command = "git commit" } });
        Assert.Equal(0, Gate.Run(input, TextWriter.Null, FakeReview, new Dictionary<string, string?> { ["KALDOR_REVIEWING"] = "1" }));
    }

    // ---- leaving the branch that has the rules (#47) ----

    (int Code, string Err) WriteA() => Hook("Write", new { file_path = Path.Combine(_root, "a.txt"), content = "x" });

    [Fact]
    public void Switching_to_an_orphan_branch_keeps_Kaldor_on()
    {
        G("switch", "--orphan", "x");
        var (code, err) = WriteA();
        Assert.Equal(2, code);
        Assert.Contains("No frozen intent", err);
    }

    [Fact]
    public void Checking_out_a_commit_before_init_keeps_Kaldor_on()
    {
        var before = Proc.Git(_root, "commit-tree", "4b825dc642cb6eb9a060e54bf8d69288fbee4904", "-m", "pre").Trim();
        G("checkout", "-q", "--detach", before);
        var (code, err) = WriteA();
        Assert.Equal(2, code);
        Assert.Contains("No frozen intent", err);
    }

    [Fact]
    public void Work_tree_rules_on_a_branch_without_them_are_ignored()
    {
        G("checkout", "-q", "--orphan", "x");
        G("rm", "-r", "--cached", "-q", ".kaldor");
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { BlockOn = [] }, Repo.Json));
        FreezeIntent();
        Stage();
        Assert.Equal(0, Commit().Code);
        Assert.Equal(Rules.Default.BlockOn, _seen!.BlockOn);
    }

    [Fact]
    public void A_branch_tip_with_rules_guards_the_repository()
    {
        TestRepo.Delete(Path.Combine(_root, ".git", "kaldor")); // a fresh clone has no marker yet
        G("switch", "--orphan", "x");
        Assert.False(File.Exists(_repo.GuardedPath));
        var (code, err) = WriteA();
        Assert.Equal(2, code);
        Assert.Contains("No frozen intent", err);
        Assert.True(File.Exists(_repo.GuardedPath));
    }

    [Fact]
    public void Doctor_reports_a_guarded_repository()
    {
        string Doctor() => Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "doctor"], _root).Out;
        var output = Doctor();
        Assert.Contains("ok    this repository is guarded on every branch", output);
        Assert.DoesNotContain("HEAD has no", output);

        G("switch", "--orphan", "x");
        Assert.Contains("warn  HEAD has no .kaldor/rules.json; the default rules apply", Doctor());
    }

    // ---- commits that skipped `git commit` (#45) ----

    void G(params string[] args) => Proc.Git(_root, args);

    /// <summary>A reviewed commit through the gate, as `git commit` would make it.</summary>
    void CommitReviewed(string file, string content, string? command = null)
    {
        File.WriteAllText(Path.Combine(_root, file), content);
        G("add", file);
        Assert.Equal(0, Commit().Code);
        G("commit", "-q", "-m", "add " + file);
    }

    /// <summary>The first commit, so that the reviewed tip is set; then a commit made some other way.</summary>
    string Bypass(string kind)
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        switch (kind)
        {
            case "commit-tree":
                File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
                G("add", "b.txt");
                G("update-ref", "HEAD", G2("commit-tree", G2("write-tree"), "-p", "HEAD", "-m", "x"));
                break;
            case "cherry-pick":
                G("cherry-pick", Side());
                break;
            case "cherry-pick of a stash":
                File.WriteAllText(Path.Combine(_root, "b.txt"), "b");
                G("add", "b.txt");
                G("stash", "push", "-q");
                G("cherry-pick", "-m1", "stash@{0}");
                break;
            case "revert":
                G("revert", "--no-edit", "HEAD");
                break;
            case "am":
                var file = Path.Combine(Path.GetTempPath(), "p-" + Guid.NewGuid().ToString("N") + ".patch");
                File.WriteAllText(file, G2("format-patch", "--stdout", "-1", Side()) + "\n");
                G("am", file);
                File.Delete(file);
                break;
        }
        return G2("rev-parse", "--short", "HEAD");
    }

    string G2(params string[] args) => Proc.Git(_root, args).Trim();

    /// <summary>A commit on another branch, HEAD back on main.</summary>
    string Side()
    {
        G("checkout", "-q", "-b", "side");
        File.WriteAllText(Path.Combine(_root, "s.txt"), "s");
        G("add", "s.txt");
        G("commit", "-q", "-m", "side");
        var sha = G2("rev-parse", "HEAD");
        G("checkout", "-q", "main");
        return sha;
    }

    [Theory]
    [InlineData("commit-tree")]
    [InlineData("cherry-pick")]
    [InlineData("cherry-pick of a stash")]
    [InlineData("revert")]
    [InlineData("am")]
    public void Commits_made_without_git_commit_are_refused(string kind)
    {
        var sha = Bypass(kind);
        Stage("two");
        var reviews = _reviews;
        var (code, err) = Commit();
        Assert.Equal(2, code);
        Assert.Contains(sha, err);
        Assert.Equal(reviews, _reviews);
        Assert.Equal(2, Commit("git push").Code);
    }

    [Fact]
    public void Pre_commit_check_refuses_the_same()
    {
        var sha = Bypass("cherry-pick");
        Stage("two");
        var err = new StringWriter();
        Assert.Equal(1, Commands.Check(_root, FakeReview, isAgent: true, err));
        Assert.Contains(sha, err.ToString());
    }

    [Theory]
    [InlineData("diff.mnemonicPrefix")]
    [InlineData("diff.noprefix")]
    [InlineData("diff.external")]
    [InlineData("diff.renames")]
    public void Git_diff_config_does_not_make_reviewed_commits_look_unreviewed(string key)
    {
        G("config", key, key == "diff.external" ? "false" : "true");
        FreezeIntent();
        File.WriteAllText(Path.Combine(_root, "old.txt"), "same\nlines\nhere\nfor\nrename\ndetection\n");
        G("add", "old.txt");
        Assert.Equal(0, Commit().Code);
        G("commit", "-q", "-m", "old");
        G("mv", "old.txt", "new.txt");
        Assert.Equal(0, Commit().Code);
        G("commit", "-q", "-m", "rename");
        Stage("two");
        Assert.Equal(0, Commit().Code);
    }

    [Fact]
    public void An_amended_commit_does_not_block()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "two");
        G("add", "a.txt");
        Assert.Equal(0, Commit("git commit --amend --no-edit").Code);
        G("commit", "-q", "--amend", "--no-edit");
        Stage("three");
        Assert.Equal(0, Commit().Code);
    }

    [Fact]
    public void A_reviewed_mode_change_does_not_cover_another_one()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        CommitReviewed("b.txt", "two");
        G("update-index", "--chmod=+x", "a.txt");
        Assert.Equal(0, Commit().Code);
        G("commit", "-q", "-m", "chmod a");
        G("update-index", "--chmod=+x", "b.txt");
        G("commit", "-q", "-m", "chmod b, never reviewed");
        Stage("four");
        Assert.Equal(2, Commit().Code);
    }

    [Fact]
    public void A_push_chained_after_a_command_that_adds_commits_is_refused()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        Assert.Equal(2, Commit("git cherry-pick abc && git push").Code);
        Assert.Equal(0, Commit("git add a.txt && git commit -m x && git push").Code);
    }

    [Fact]
    public void Reviewed_then_committed_does_not_block()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        CommitReviewed("b.txt", "two");
        Assert.Equal(0, Commit("git push").Code);
    }

    [Fact]
    public void The_history_that_exists_when_Kaldor_first_runs_is_trusted_and_nothing_after_it()
    {
        Side();
        G("cherry-pick", G2("rev-parse", "side"));
        File.Delete(_repo.ReviewedPath); // Kaldor has not run on this repository yet
        FreezeIntent();
        Stage();
        Assert.Equal(0, Commit().Code);
        G("commit", "-q", "-m", "change");


        File.WriteAllText(Path.Combine(_root, "c.txt"), "c");
        G("add", "c.txt");
        G("update-ref", "HEAD", G2("commit-tree", G2("write-tree"), "-p", "HEAD", "-m", "later"));
        Stage("two");
        Assert.Equal(2, Commit().Code);
    }

    [Fact]
    public void A_repository_with_per_worktree_state_from_an_older_version_upgrades_without_refusals()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        Stage("two");
        Assert.Equal(0, Commit().Code); // reviewed, recorded in this worktree's state.json
        File.Delete(_repo.ReviewedPath);  // as if Kaldor 1.4 had written it
        G("commit", "-q", "-m", "two");
        Assert.Equal(0, Commit("git push").Code);
        Stage("three");
        Assert.Equal(0, Commit().Code);
        Assert.True(_repo.LoadReviewed().Seeded);
    }

    [Fact]
    public void Old_state_files_without_patch_ids_still_load()
    {
        Directory.CreateDirectory(_repo.StateDir);
        File.WriteAllText(_repo.StatePath, "{\"intent_hash\":\"h\",\"passes\":[{\"diff_hash\":\"d\",\"intent_hash\":\"h\",\"by\":\"review\"}]}");
        var state = _repo.LoadState();
        Assert.Null(state.Passes[0].PatchId);
        Assert.Null(state.ReviewedTip);
    }

    [Fact]
    public void Rebasing_reviewed_commits_does_not_block()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        G("checkout", "-q", "-b", "feat");
        CommitReviewed("f.txt", "f");
        G("checkout", "-q", "main");
        File.WriteAllText(Path.Combine(_root, "m.txt"), "m");
        G("add", "m.txt");
        G("commit", "-q", "-m", "moved on");
        G("update-ref", "refs/remotes/origin/main", "HEAD");
        G("checkout", "-q", "feat");
        G("rebase", "-q", "main");
        Stage("two");
        Assert.Equal(0, Commit().Code);
        Assert.Equal(0, Commit("git push").Code);
    }

    [Fact]
    public void Clean_merge_does_not_block_and_a_merge_with_extra_changes_does()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        G("checkout", "-q", "-b", "feat");
        CommitReviewed("f.txt", "f");
        G("checkout", "-q", "main");
        File.WriteAllText(Path.Combine(_root, "m.txt"), "m");
        G("add", "m.txt");
        G("commit", "-q", "-m", "moved on");
        G("update-ref", "refs/remotes/origin/main", "HEAD");
        G("merge", "-q", "--no-ff", "-m", "merge", "feat");
        Stage("two");
        Assert.Equal(0, Commit().Code);

        // Same merge, with a change smuggled into the merge commit.
        G("reset", "-q", "--hard", "HEAD~1");
        G("merge", "-q", "--no-ff", "--no-commit", "feat");
        File.WriteAllText(Path.Combine(_root, "evil.txt"), "evil");
        G("add", "evil.txt");
        G("commit", "-q", "-m", "merge");
        Stage("three");
        Assert.Equal(2, Commit().Code);
    }

    [Fact]
    public void Commits_from_a_remote_do_not_block()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        var clone = _root + "-clone";
        try
        {
            Proc.Git(Path.GetTempPath(), "clone", "-q", _root, clone);
            File.WriteAllText(Path.Combine(clone, "r.txt"), "r");
            Proc.Git(clone, "add", "r.txt");
            Proc.Git(clone, "-c", "user.email=x@y", "-c", "user.name=x", "commit", "-q", "-m", "remote");
            G("remote", "add", "other", clone);
            G("fetch", "-q", "other");
            G("merge", "-q", "--ff-only", "other/main");
            Stage("two");
            Assert.Equal(0, Commit().Code);
        }
        finally { TestRepo.Delete(clone); }
    }

    [Fact]
    public void Approved_commits_no_longer_block()
    {
        Bypass("cherry-pick");
        var state = _repo.LoadState();
        _repo.ApproveCommits(state, _repo.UnreviewedCommits(Repo.PushRoots)); // what `kaldor approve` records once the user says yes
        Stage("two");
        Assert.Equal(0, Commit().Code);
        Assert.Equal(0, Commit("git push").Code);
    }

    [Fact]
    public void A_commit_with_other_content_than_the_reviewed_one_is_refused()
    {
        FreezeIntent();
        File.WriteAllText(Path.Combine(_root, "a.txt"), "reviewed");
        G("add", "a.txt");
        Assert.Equal(0, Commit().Code);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "reviewed ");
        G("add", "a.txt");
        G("commit", "-q", "-m", "whitespace only: still another change");
        Stage("next");
        Assert.Equal(2, Commit().Code);
    }

    [Fact]
    public void Push_in_a_repository_with_a_clean_history_goes_through()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        Assert.Equal(0, Commit("git -C . push origin main").Code);
        G("checkout", "-q", "-b", "reviewed");
        CommitReviewed("r.txt", "r");
        G("checkout", "-q", "main");
        Assert.Equal(0, Commit("git push --all").Code);
    }

    // ---- unreviewed commits on another branch or in another worktree (#53) ----

    /// <summary>A commit nobody reviewed, on branch x, made with commit-tree and update-ref.</summary>
    string Smuggle()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        File.WriteAllText(Path.Combine(_root, "evil.txt"), "evil");
        G("add", "evil.txt");
        var commit = G2("commit-tree", G2("write-tree"), "-p", "HEAD", "-m", "evil");
        G("reset", "-q");
        File.Delete(Path.Combine(_root, "evil.txt"));
        G("update-ref", "refs/heads/x", commit);
        return G2("rev-parse", "--short", commit);
    }

    void Ready(string worktree)
    {
        const string intent = "# Intent\nGoal: work in a worktree\n";
        FreezeIn(worktree, intent);
        Commands.MarkConfirmed(Repo.Find(worktree)!, intent);
    }

    string Worktree(params string[] args)
    {
        var path = _root + "-w" + _worktrees.Count;
        G(["worktree", "add", "-q", .. args.Select(a => a == "<path>" ? path : a)]);
        _worktrees.Add(path);
        return path;
    }

    [Fact]
    public void A_push_checks_other_branches_and_names_the_branch()
    {
        var sha = Smuggle();
        var (code, err) = Commit("git push origin x");
        Assert.Equal(2, code);
        Assert.Contains(sha, err);
        Assert.Contains("(on x)", err);
        Assert.Equal(2, Commit("git push --all").Code);
        Assert.Equal(2, Commit("git push origin main").Code); // a push checks every branch, whatever it sends
        G("tag", "t", "x");
        G("branch", "-D", "x");
        Assert.Equal(2, Commit("git push --tags").Code);
    }

    [Fact]
    public void A_new_worktree_does_not_trust_the_branch_it_starts_from()
    {
        var sha = Smuggle();
        var t = Worktree("<path>", "x");
        Ready(t);
        var (code, err) = Commit("git push origin x", t);
        Assert.Equal(2, code);
        Assert.Contains(sha, err);
        File.WriteAllText(Path.Combine(t, "a.txt"), "two");
        Proc.Git(t, "add", "a.txt");
        var (code2, err2) = Commit("git commit -m two", t);
        Assert.Equal(2, code2);
        Assert.Contains(sha, err2);
    }

    [Fact]
    public void A_commit_that_skipped_git_commit_in_a_new_branch_worktree_is_refused_on_push()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        var t = Worktree("-b", "y", "<path>");
        File.WriteAllText(Path.Combine(t, "evil.txt"), "evil");
        Proc.Git(t, "add", "evil.txt");
        Proc.Git(t, "commit", "-q", "-m", "evil, made without the gate");
        Assert.Equal(2, Commit("git push origin y", t).Code);
    }

    [Fact]
    public void A_commit_reviewed_in_one_worktree_passes_in_another()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        var t = Worktree("-b", "w", "<path>");
        Ready(t);
        File.WriteAllText(Path.Combine(t, "w.txt"), "w");
        Proc.Git(t, "add", "w.txt");
        Assert.Equal(0, Commit("git commit -m w", t).Code);
        Proc.Git(t, "commit", "-q", "-m", "w");
        Assert.Equal(0, Commit("git push origin w", t).Code);
        Assert.Equal(0, Commit("git push origin w").Code);
        G("merge", "-q", "--ff-only", "w");
        Stage("two");
        Assert.Equal(0, Commit().Code);
        Assert.Equal(0, Commit("git push").Code);
        var u = Worktree("--detach", "<path>", "w");
        Assert.Equal(0, Commit("git push origin w", u).Code);
    }

    [Fact]
    public void Approving_in_one_worktree_clears_the_commits_for_every_worktree()
    {
        Smuggle();
        var t = Worktree("--detach", "<path>");
        Assert.Equal(2, Commit("git push", t).Code);
        var wrepo = Repo.Find(t)!;
        wrepo.ApproveCommits(wrepo.LoadState(), wrepo.UnreviewedCommits(Repo.PushRoots)); // what `kaldor approve` records
        Assert.Equal(0, Commit("git push").Code);
        Assert.Equal(0, Commit("git push origin x", t).Code);
    }

    [Fact]
    public void A_push_of_a_raw_commit_id_is_refused()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        Assert.Equal(2, Commit("git push origin $(git commit-tree HEAD^{tree} -p HEAD -m evil):refs/heads/main").Code);
        Assert.Equal(2, Commit("git push origin " + G2("rev-parse", "HEAD") + ":refs/heads/main").Code);
        Assert.Equal(2, Commit("git push origin refs/stash:refs/heads/main").Code);
        Assert.Equal(2, Commit("git push origin ORIG_HEAD:main").Code);
        Assert.Equal(0, Commit("git push origin HEAD:refs/heads/main").Code);
        Assert.Equal(0, Commit("git push origin +main:refs/heads/main").Code);
    }

    [Fact]
    public void Refs_that_exist_when_Kaldor_first_runs_are_trusted()
    {
        File.Delete(_repo.ReviewedPath);
        Side(); // exists before Kaldor first runs
        FreezeIntent();
        G("update-ref", "refs/heads/old", G2("rev-parse", "side"));
        Assert.Equal(0, Commit("git push --all").Code);
    }
}
