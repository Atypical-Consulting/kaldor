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
        Directory.CreateDirectory(_root);
        Proc.Git(_root, "init", "-q", "-b", "main");
        Proc.Git(_root, "config", "user.email", "test@example.com");
        Proc.Git(_root, "config", "user.name", "Test");
        Proc.Git(_root, "config", "commit.gpgsign", "false");
        Directory.CreateDirectory(Path.Combine(_root, ".kaldor"));
        File.WriteAllText(Path.Combine(_root, ".kaldor", "rules.json"), JsonSerializer.Serialize(Rules.Default, Repo.Json));
        File.WriteAllText(Path.Combine(_root, ".kaldor", ".gitignore"), "*\n!.gitignore\n!rules.json\n");
        Proc.Git(_root, "add", "-A");
        Proc.Git(_root, "commit", "-q", "-m", "init");
        _repo = Repo.Find(_root)!;
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    ReviewResult FakeReview(Rules rules, string intent, string diff)
    {
        _reviews++;
        return _next;
    }

    (int Code, string Err) Hook(string tool, object toolInput, string? cwd = null)
    {
        var input = JsonSerializer.Serialize(new { cwd = cwd ?? _root, tool_name = tool, tool_input = toolInput, hook_event_name = "PreToolUse" });
        var err = new StringWriter();
        var code = Gate.Run(input, err, FakeReview, new Dictionary<string, string?>());
        return (code, err.ToString());
    }

    (int Code, string Err) Commit(string command = "git commit -m change", string? cwd = null) => Hook("Bash", new { command }, cwd);

    void FreezeIntent(string text = "# Intent\nGoal: change a.txt\n")
    {
        File.WriteAllText(_repo.IntentPath, text);
        Assert.Equal(0, Commands.Freeze(_root));
        Commands.MarkConfirmed(_repo, text); // what `kaldor intent confirm` records once the user says yes
    }

    [Fact]
    public void Commits_wait_for_the_user_to_confirm_the_intent()
    {
        File.WriteAllText(_repo.IntentPath, "# Intent\nGoal: change a.txt\n");
        Assert.Equal(0, Commands.Freeze(_root));
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

    [Fact]
    public void Agent_cannot_confirm_the_intent()
    {
        Assert.Equal(2, Commit("kaldor intent confirm").Code);
        Assert.Equal(2, Commit("dnx --yes Atypical.Kaldor intent confirm").Code);

        // The command itself needs a person at an interactive terminal.
        File.WriteAllText(_repo.IntentPath, "# Intent\nGoal: change a.txt\n");
        Assert.Equal(0, Commands.Freeze(_root));
        var (code, output, err) = Proc.Run("dotnet", [typeof(Commands).Assembly.Location, "intent", "confirm"], _root, stdin: "y\n");
        Assert.Equal(1, code);
        Assert.NotEqual(_repo.LoadState().IntentHash, _repo.LoadState().ConfirmedHash);
        // It refuses before showing anything, and says what to run instead.
        Assert.Equal("", output);
        Assert.DoesNotContain("Goal:", err);
        Assert.Contains("`dnx Atypical.Kaldor intent confirm` in a terminal outside Claude Code", err);
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
        File.WriteAllText(_repo.IntentPath, "# Intent\nGoal: change a.txt\n");
        Assert.Equal(0, Commands.Freeze(_root));
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
        File.Delete(_repo.RulesPath);
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
}
