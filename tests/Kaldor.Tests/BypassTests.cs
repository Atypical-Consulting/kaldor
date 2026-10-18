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

    public void Dispose() => TestRepo.Delete(_root);

    ReviewResult FakeReview(Rules rules, string intent, string diff)
    {
        _reviews++;
        _seen = rules;
        return _next;
    }

    Rules? _seen;

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
    }

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
    public void Without_rules_json_at_HEAD_the_gate_uses_the_work_tree_rules()
    {
        Proc.Git(_root, "rm", "-q", "--cached", ".kaldor/rules.json");
        Proc.Git(_root, "commit", "-q", "-m", "no rules");
        File.WriteAllText(_repo.RulesPath, JsonSerializer.Serialize(Rules.Default with { BlockOn = ["scope_creep"] }, Repo.Json));
        FreezeIntent();
        Stage();
        Assert.Equal(0, Commit().Code);
        Assert.Equal(["scope_creep"], _seen!.BlockOn);
    }

    [Fact]
    public void Agent_cannot_confirm_the_intent()
    {
        Assert.Equal(2, Commit("kaldor intent confirm").Code);
        Assert.Equal(2, Commit("dnx --yes Atypical.Kaldor intent confirm").Code);

        // The command itself needs a person at an interactive terminal.
        FreezeIntent(confirm: false);
        var (code, output, err) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "intent", "confirm"], _root, stdin: "y\n");
        Assert.Equal(1, code);
        Assert.NotEqual(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
        // It refuses before showing anything, and says what to run instead.
        Assert.Equal("", output);
        Assert.DoesNotContain("Goal:", err);
        Assert.Contains("`dnx Atypical.Kaldor intent confirm` in a terminal outside Claude Code", err);
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
        Assert.Empty(_repo.LoadState().Preconfirmed!);

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
        Assert.Equal([Repo.Hash("# Intent\nGoal: other\n")], _repo.LoadState().Preconfirmed);
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
        var (code, output, err) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "intent", "confirm", "--all"], _root, stdin: "y\n");
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("intent confirm --all", err);
        Assert.Null(_repo.LoadState().Preconfirmed);
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
        var (code, output, err) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "approve"], _root, stdin: "y\n");
        Assert.Equal(1, code);
        Assert.Equal("", output);
        Assert.Contains("`dnx Atypical.Kaldor approve` in a terminal outside Claude Code", err);
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
    public void Reviewed_then_committed_does_not_block()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        CommitReviewed("b.txt", "two");
        Assert.Equal(0, Commit("git push").Code);
    }

    [Fact]
    public void First_check_trusts_existing_history()
    {
        Side();
        G("cherry-pick", G2("rev-parse", "side"));
        FreezeIntent();
        Stage();
        Assert.Null(_repo.LoadState().ReviewedTip);
        Assert.Equal(0, Commit().Code);
        Assert.Equal(G2("rev-parse", "HEAD"), _repo.LoadState().ReviewedTip);
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
        _repo.ApproveCommits(state, _repo.UnreviewedCommits(state)); // what `kaldor approve` records once the user says yes
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
    public void Push_in_a_repository_with_a_clean_history_goes_through_and_only_HEAD_counts()
    {
        FreezeIntent();
        CommitReviewed("a.txt", "one");
        Assert.Equal(0, Commit("git -C . push origin main").Code);
        Side();
        Assert.Equal(0, Commit("git push").Code);
    }
}
