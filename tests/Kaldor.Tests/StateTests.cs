using System.Text.Json;

namespace Kaldor.Tests;

/// <summary>Where Kaldor keeps its state and history: in the git common directory, per worktree.</summary>
public sealed class StateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kaldor-state-" + Guid.NewGuid().ToString("N"));
    string Linked => _root + "-wt";

    public StateTests()
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
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, Linked }.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    static LogEntry Entry(string kind) => new(DateTimeOffset.Now, kind, null, null, null, null, null, null, null);

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
