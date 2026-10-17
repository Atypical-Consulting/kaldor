using System.Text;
using System.Text.RegularExpressions;

namespace Kaldor;

/// <summary>A <c>git commit</c> found in a shell command, with the directory it runs in.</summary>
sealed record CommitCall(string Dir, bool SkipsHooks);

/// <summary>
/// Finds <c>git commit</c> calls in a Bash or PowerShell command line, including the forms that slip past a
/// <c>Bash(git commit *)</c> filter: <c>git -C dir commit</c>, leading variables, wrappers, <c>cd x &amp;&amp;</c>,
/// subshells, <c>bash -c "..."</c> and aliases. A commit hidden in a script file is out of reach here; the git
/// <c>pre-commit</c> hook installed by <c>kaldor init</c> catches that one.
/// </summary>
static partial class CommandParser
{
    static readonly HashSet<string> Prefixes =
        ["sudo", "command", "env", "exec", "time", "nohup", "nice", "builtin", "!", "{", "if", "then", "else", "elif", "do", "while", "until"];

    static readonly HashSet<string> Shells = ["sh", "bash", "zsh", "dash", "ksh", "pwsh", "powershell"];

    // Git refuses aliases that shadow a builtin, so only an unknown subcommand can be an alias.
    static readonly HashSet<string> Builtins =
    [
        "add", "am", "apply", "bisect", "blame", "branch", "checkout", "cherry-pick", "clean", "clone", "config",
        "describe", "diff", "fetch", "format-patch", "gc", "grep", "init", "log", "ls-files", "merge", "mv", "notes",
        "pull", "push", "rebase", "reflog", "remote", "reset", "restore", "rev-parse", "revert", "rm", "shortlog",
        "show", "stash", "status", "submodule", "switch", "tag", "worktree",
    ];

    [GeneratedRegex(@"(?i)\b(kaldor|atypical\.kaldor)\b[^;&|\n]*?(\bapprove\b|--accept\b)")]
    private static partial Regex HumanOnlyRegex();

    /// <summary>True when the command runs one of the commands reserved for a human.</summary>
    public static bool RunsHumanOnlyCommand(string command) => HumanOnlyRegex().IsMatch(command);

    public static List<CommitCall> FindCommits(string command, string cwd, Func<string, IReadOnlyDictionary<string, string>>? aliases = null)
    {
        var found = new List<CommitCall>();
        Scan(command, cwd, aliases ?? GitAliases, found, depth: 0);
        return found;
    }

    static void Scan(string command, string cwd, Func<string, IReadOnlyDictionary<string, string>> aliases, List<CommitCall> found, int depth)
    {
        if (depth > 5) return;
        foreach (var segment in Segments(command))
        {
            var i = 0;
            var wrapped = false;
            while (i < segment.Count &&
                   (Prefixes.Contains(segment[i]) || IsAssignment(segment[i]) || (wrapped && segment[i].StartsWith('-'))))
                wrapped |= Prefixes.Contains(segment[i++]);
            if (i >= segment.Count) continue;

            var name = Path.GetFileName(segment[i]).ToLowerInvariant().Replace(".exe", "");
            var rest = segment.Skip(i + 1).ToList();

            if (name is "cd" or "pushd" or "set-location" or "sl")
            {
                var target = rest.FirstOrDefault(a => !a.StartsWith('-'));
                if (target is not null && !target.StartsWith('~')) cwd = Path.GetFullPath(Path.Combine(cwd, target));
            }
            else if (Shells.Contains(name))
            {
                var flag = rest.FindIndex(a => a is "-c" or "-lc" or "-ic" || a.Equals("-Command", StringComparison.OrdinalIgnoreCase));
                if (flag >= 0 && flag + 1 < rest.Count) Scan(rest[flag + 1], cwd, aliases, found, depth + 1);
            }
            else if (name is "eval" or "invoke-expression" or "iex")
            {
                Scan(string.Join(' ', rest), cwd, aliases, found, depth + 1);
            }
            else if (name == "git")
            {
                ScanGit(rest, cwd, aliases, found, depth);
            }
        }
    }

    static void ScanGit(List<string> args, string cwd, Func<string, IReadOnlyDictionary<string, string>> aliases, List<CommitCall> found, int depth)
    {
        var dir = cwd;
        var skipsHooks = false;
        var j = 0;
        while (j < args.Count && args[j].StartsWith('-'))
        {
            var a = args[j];
            if (a == "-C" && j + 1 < args.Count) { dir = Path.GetFullPath(Path.Combine(dir, args[j + 1])); j += 2; }
            else if (a == "-c" && j + 1 < args.Count) { skipsHooks |= args[j + 1].StartsWith("core.hooksPath", StringComparison.OrdinalIgnoreCase); j += 2; }
            else if (a is "--git-dir" or "--work-tree" or "--namespace" or "--exec-path" && j + 1 < args.Count) j += 2;
            else j++;
        }
        if (j >= args.Count) return;

        var sub = args[j];
        var rest = args.Skip(j + 1).ToList();
        if (sub == "commit")
        {
            found.Add(new CommitCall(dir, skipsHooks || SkipsVerify(rest)));
        }
        else if (!Builtins.Contains(sub) && aliases(dir).TryGetValue(sub, out var expansion))
        {
            if (expansion.StartsWith('!')) Scan(expansion[1..], dir, aliases, found, depth + 1);
            else ScanGit([.. Split(expansion), .. rest], dir, aliases, found, depth + 1);
        }
    }

    static readonly HashSet<string> ValueFlags =
        ["-m", "-F", "-c", "-C", "-t", "--message", "--file", "--author", "--date", "--template", "--reuse-message", "--reedit-message", "--fixup", "--squash", "--cleanup"];

    /// <summary><c>--no-verify</c>, <c>-n</c>, or a short-flag cluster such as <c>-an</c>, skipping flag values such as <c>-m -n</c>.</summary>
    static bool SkipsVerify(List<string> args)
    {
        for (var k = 0; k < args.Count; k++)
        {
            var arg = args[k];
            if (ValueFlags.Contains(arg)) { k++; continue; }
            if (arg == "--no-verify") return true;
            if (arg.Length > 1 && arg[0] == '-' && arg[1] != '-')
            {
                var flags = arg[1..].TakeWhile(c => c is not ('m' or 'F' or 'c' or 'C' or 't')).ToList();
                if (flags.Contains('n')) return true;
                if (flags.Count == arg.Length - 2) k++; // the cluster ends with a value flag: its value is the next word
            }
        }
        return false;
    }

    static bool IsAssignment(string word) =>
        word.Length > 0 && (char.IsLetter(word[0]) || word[0] == '_' || word[0] == '$') && word.Contains('=') &&
        word[..word.IndexOf('=')].All(c => char.IsLetterOrDigit(c) || c is '_' or ':' or '$');

    static IReadOnlyDictionary<string, string> GitAliases(string dir)
    {
        var aliases = new Dictionary<string, string>();
        if (!Directory.Exists(dir)) return aliases;
        var (code, output, _) = Proc.Run("git", ["config", "--get-regexp", @"^alias\."], dir);
        if (code != 0) return aliases;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var space = line.IndexOf(' ');
            if (space > 6) aliases[line[6..space]] = line[(space + 1)..];
        }
        return aliases;
    }

    static List<string> Split(string text) => Segments(text).SelectMany(s => s).ToList();

    /// <summary>
    /// Splits a command line into simple commands (on <c>; &amp; | &amp;&amp; || ( ) $( `</c> and newlines) and each one
    /// into words, honouring single quotes, double quotes and backslash escapes.
    /// </summary>
    internal static List<List<string>> Segments(string command)
    {
        var segments = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        char quote = '\0';

        void EndWord()
        {
            if (inWord) words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }

        void EndSegment()
        {
            EndWord();
            if (words.Count > 0) segments.Add(words);
            words = [];
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else word.Append(c);
                continue;
            }
            if (c == '\\' && i + 1 < command.Length && quote == '\0')
            {
                if (command[i + 1] != '\n') { word.Append(command[i + 1]); inWord = true; }
                i++;
                continue;
            }
            // Command substitution starts a new command even inside double quotes.
            if (c == '`' || (c == '$' && i + 1 < command.Length && command[i + 1] == '('))
            {
                EndSegment();
                if (c == '$') i++;
                continue;
            }
            if (quote == '"')
            {
                if (c == '"') quote = '\0'; else word.Append(c);
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    inWord = true;
                    break;
                case ';' or '&' or '|' or '(' or ')' or '\n' or '\r':
                    EndSegment();
                    break;
                case ' ' or '\t':
                    EndWord();
                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }
        EndSegment();
        return segments;
    }
}
