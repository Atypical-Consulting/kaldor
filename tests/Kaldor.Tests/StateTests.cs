using System.Text.Json;

namespace Kaldor.Tests;

/// <summary>Where Kaldor keeps its state and history: in the git common directory, per worktree.</summary>
public sealed class StateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kaldor-state-" + Guid.NewGuid().ToString("N"));
    string Linked => _root + "-wt";

    public StateTests() => TestRepo.Create(_root);

    public void Dispose() => TestRepo.Delete(_root, Linked);

    static LogEntry Entry(string kind) => new(DateTimeOffset.Now, kind);

    [Fact]
    public void State_and_history_live_in_the_git_directory()
    {
        var repo = Repo.Find(_root)!;
        repo.SaveState(new State("sha256:x", []));
        repo.Log(Entry("freeze"));

        var gitKaldor = Path.Combine(repo.Root, ".git", "kaldor");
        Assert.Equal(gitKaldor, repo.StateDir);
        Assert.True(File.Exists(Path.Combine(gitKaldor, "state.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(gitKaldor, "log")));
        Assert.False(File.Exists(Path.Combine(repo.Dir, "state.json")));

        // git clean no longer erases them.
        Proc.Git(_root, "clean", "-fdxq");
        Assert.Equal("sha256:x", Repo.Find(_root)!.LoadState().IntentHash);
    }

    [Fact]
    public void A_state_without_a_pending_ticket_still_loads()
    {
        var repo = Repo.Find(_root)!;
        Directory.CreateDirectory(repo.StateDir);
        File.WriteAllText(repo.StatePath, """{"intent_hash":"sha256:x","passes":[],"confirmed_hash":null}""");
        var state = repo.LoadState();
        Assert.Equal("sha256:x", state.IntentHash);
        Assert.Null(state.Pending);
    }

    [Fact]
    public void A_linked_worktree_has_its_own_state_and_report_sees_every_worktree()
    {
        Proc.Git(_root, "worktree", "add", "-q", Linked, "-b", "task");
        var main = Repo.Find(_root)!;
        var linked = Repo.Find(Linked)!;
        Assert.Equal("main", main.Worktree);
        Assert.NotEqual("main", linked.Worktree);
        Assert.Equal(Path.Combine(main.StateDir, "worktrees", linked.Worktree), linked.StateDir);

        main.SaveState(new State("sha256:main", []));
        linked.SaveState(new State("sha256:task", []));
        Assert.Equal("sha256:main", main.LoadState().IntentHash);
        Assert.Equal("sha256:task", linked.LoadState().IntentHash);

        main.Log(Entry("freeze"));
        linked.Log(Entry("review"));
        Proc.Git(_root, "worktree", "remove", "--force", Linked);

        // The removed worktree's history is still there, in order, for both sides.
        var all = Repo.Find(_root)!.ReadAllLogs();
        Assert.Equal([("main", "freeze"), (linked.Worktree, "review")], all.Select(e => (e.Worktree, e.Entry.Kind)));
    }

    [Fact]
    public void An_older_pre_commit_hook_is_brought_to_the_running_version()
    {
        var hook = Path.Combine(_root, ".git", "hooks", "pre-commit");
        const string Old = "#!/bin/sh\n# mine\n[ \"$CLAUDECODE\" = \"1\" ] || exit 0\nexec dnx --yes --version 1.0.0 Atypical.Kaldor check\n";
        File.WriteAllText(hook, Old);

        Repo.Find(_root);

        Assert.Equal(Old.Replace("1.0.0", Commands.Version), File.ReadAllText(hook));
    }

    [Fact]
    public void A_hook_without_kaldor_is_left_alone()
    {
        var hook = Path.Combine(_root, ".git", "hooks", "pre-commit");
        const string Other = "#!/bin/sh\nnpx lint-staged --version 1.0.0\n";
        File.WriteAllText(hook, Other);

        Repo.Find(_root);

        Assert.Equal(Other, File.ReadAllText(hook));
    }

    [Fact]
    public void State_from_kaldor_1_0_moves_to_the_git_directory()
    {
        var dir = Path.Combine(_root, ".kaldor");
        File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(new State("sha256:old", []), Repo.Json));
        File.WriteAllText(Path.Combine(dir, "amendment.md"), "more\n");
        Directory.CreateDirectory(Path.Combine(dir, "log"));
        File.WriteAllText(Path.Combine(dir, "log", "20260101T000000.0000000-freeze.json"), JsonSerializer.Serialize(Entry("freeze"), Repo.Json));

        var repo = Repo.Find(_root)!;
        Assert.Equal("sha256:old", repo.LoadState().IntentHash);
        Assert.True(File.Exists(repo.AmendmentPath));
        Assert.Equal("freeze", Assert.Single(repo.ReadLog()).Kind);
        Assert.False(File.Exists(Path.Combine(dir, "state.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "log")));
    }
}
