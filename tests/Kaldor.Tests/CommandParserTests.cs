namespace Kaldor.Tests;

public class CommandParserTests
{
    static readonly string Cwd = Path.GetFullPath(Path.GetTempPath());
    static readonly Func<string, IReadOnlyDictionary<string, string>> NoAliases = _ => new Dictionary<string, string>();

    static List<CommitCall> Find(string command, Func<string, IReadOnlyDictionary<string, string>>? aliases = null) =>
        CommandParser.FindCommits(command, Cwd, aliases ?? NoAliases);

    [Theory]
    [InlineData("git commit -m 'x'")]
    [InlineData("git add . && git commit -m \"feat: x\"")]
    [InlineData("GIT_AUTHOR_NAME=bot git commit -m x")]
    [InlineData("sudo -E env FOO=1 git commit")]
    [InlineData("/usr/bin/git commit -m x")]
    [InlineData("git -c user.name=x --no-pager commit -m x")]
    [InlineData("(git commit -m x)")]
    [InlineData("echo $(git commit -m x)")]
    [InlineData("echo `git commit -m x`")]
    [InlineData("bash -c 'git add -A; git commit -m x'")]
    [InlineData("sh -lc \"git commit -m x\"")]
    [InlineData("eval git commit -m x")]
    [InlineData("& git commit -m x")]
    [InlineData("pwsh -Command \"git commit -m x\"")]
    [InlineData("git.exe commit -m x")]
    [InlineData("if true; then git commit -m x; fi")]
    [InlineData("git status\ngit commit -m x")]
    [InlineData("git commit -m \"$(cat <<'EOF'\nfeat: x\nEOF\n)\"")]
    public void Detects_commit(string command) => Assert.Single(Find(command));

    [Theory]
    [InlineData("git status")]
    [InlineData("echo \"git commit\"")]
    [InlineData("grep -r 'git commit' docs")]
    [InlineData("git log --grep commit")]
    [InlineData("dotnet test")]
    [InlineData("git commit-tree HEAD^{tree}")]
    public void Ignores_non_commits(string command) => Assert.Empty(Find(command));

    [Fact]
    public void Follows_git_dash_C()
    {
        var call = Assert.Single(Find("git -C ../other commit -m x"));
        Assert.Equal(Path.GetFullPath(Path.Combine(Cwd, "../other")), call.Dir);
    }

    [Fact]
    public void Follows_cd_before_commit()
    {
        var call = Assert.Single(Find("cd sub/project && git commit -m x"));
        Assert.Equal(Path.GetFullPath(Path.Combine(Cwd, "sub/project")), call.Dir);
    }

    [Fact]
    public void Expands_aliases()
    {
        var aliases = new Dictionary<string, string> { ["ci"] = "commit -v", ["save"] = "!git add -A && git commit -m wip" };
        Assert.Single(Find("git ci -m x", _ => aliases));
        Assert.Single(Find("git save", _ => aliases));
        Assert.Empty(Find("git st", _ => aliases));
    }

    [Theory]
    [InlineData("git commit --no-verify -m x", true)]
    [InlineData("git commit -n -m x", true)]
    [InlineData("git commit -anm x", true)]
    [InlineData("git -c core.hooksPath=/dev/null commit -m x", true)]
    [InlineData("git commit -am 'no -n here'", false)]
    [InlineData("git commit -m -n", false)]
    public void Spots_hook_skipping(string command, bool skips) =>
        Assert.Equal(skips, Assert.Single(Find(command)).SkipsHooks);

    [Theory]
    [InlineData("kaldor approve", true)]
    [InlineData("dnx --yes Atypical.Kaldor approve", true)]
    [InlineData("echo y | kaldor intent amend --accept", true)]
    [InlineData("kaldor intent amend \"add caching\"", false)]
    [InlineData("kaldor report", false)]
    public void Spots_human_only_commands(string command, bool humanOnly) =>
        Assert.Equal(humanOnly, CommandParser.RunsHumanOnlyCommand(command));
}
