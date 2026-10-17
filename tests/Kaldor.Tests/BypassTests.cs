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
        _repo = new Repo(Path.GetFullPath(_root));
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
