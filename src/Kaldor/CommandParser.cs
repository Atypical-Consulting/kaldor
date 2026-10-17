using System.Text;

namespace Kaldor;

/// <summary>A <c>git commit</c> found in a shell command, with the directory it runs in.</summary>
sealed record CommitCall(string Dir, bool SkipsHooks);

/// <summary>
/// Finds <c>git commit</c> calls in a Bash or PowerShell command line, including the forms that slip past a
/// <c>Bash(git commit *)</c> filter: <c>git -C dir commit</c>, leading variables, wrappers, <c>cd x &amp;&amp;</c>,
/// subshells, <c>bash -c "..."</c> and aliases. A commit hidden in a script file is out of reach here; the git
/// <c>pre-commit</c> hook installed by <c>kaldor init</c> catches that one.
/// </summary>
static class CommandParser
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

    public sealed class Findings
    {
        public readonly List<CommitCall> Commits = [];
        public bool HumanOnly;
        /// <summary>Full paths the command writes or deletes, as far as the parser can tell.</summary>
        public readonly List<string> Writes = [];
    }

    /// <summary>Everything the gate needs to know about a command, in one pass.</summary>
    public static Findings Analyze(string command, string cwd, Func<string, IReadOnlyDictionary<string, string>>? aliases = null) =>
        ScanAll(command, cwd, aliases);

    /// <summary>
    /// True when the command runs <c>kaldor approve</c> or <c>kaldor intent amend --accept</c>, directly or through
    /// <c>dnx</c> / <c>dotnet</c>. A mention in quoted text, a comment or a quoted heredoc is not a run.
    /// </summary>
    public static bool RunsHumanOnlyCommand(string command, string cwd, Func<string, IReadOnlyDictionary<string, string>>? aliases = null) =>
        ScanAll(command, cwd, aliases).HumanOnly;

    public static List<CommitCall> FindCommits(string command, string cwd, Func<string, IReadOnlyDictionary<string, string>>? aliases = null) =>
        ScanAll(command, cwd, aliases).Commits;

    static Findings ScanAll(string command, string cwd, Func<string, IReadOnlyDictionary<string, string>>? aliases)
    {
        var found = new Findings();
        Scan(command, cwd, aliases ?? GitAliases, found, depth: 0);
        return found;
    }

    /// <summary>The arguments given to the Kaldor tool when <paramref name="name"/> launches it, else null.</summary>
    static List<string>? KaldorArgs(string name, List<string> rest)
    {
        if (name is "kaldor" or "kaldor.cmd") return rest;
        if (name is not ("dnx" or "dotnet")) return null;
        var tool = rest.FindIndex(a => a.Equals("Atypical.Kaldor", StringComparison.OrdinalIgnoreCase) || a.Equals("kaldor", StringComparison.OrdinalIgnoreCase));
        return tool < 0 ? null : rest[(tool + 1)..];
    }

    static bool IsHumanOnly(List<string> args) =>
        args is ["approve", ..] or ["intent", "amend", "--accept", ..];

    static void Scan(string command, string cwd, Func<string, IReadOnlyDictionary<string, string>> aliases, Findings found, int depth)
    {
        if (depth > 5) return;
        foreach (var words in Segments(command))
        {
            var segment = TakeRedirections(words, cwd, found);
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
            else if (KaldorArgs(name, rest) is { } toolArgs && IsHumanOnly(toolArgs))
            {
                found.HumanOnly = true;
            }
            else
            {
                found.Writes.AddRange(WriteTargets(name, rest).Select(t => Resolve(cwd, t)));
            }
        }
    }

    /// <summary>The marker the tokenizer puts in place of a redirection operator; the next word is its target.</summary>
    internal const string Redirect = "\0>";

    /// <summary>Records the redirection targets and returns the words without them.</summary>
    static List<string> TakeRedirections(List<string> words, string cwd, Findings found)
    {
        var kept = new List<string>();
        for (var k = 0; k < words.Count; k++)
        {
            if (words[k] != Redirect) kept.Add(words[k]);
            else if (k + 1 < words.Count) found.Writes.Add(Resolve(cwd, words[++k]));
        }
        return kept;
    }

    // Commands that write every path they are given, and those that write only the last one (the destination).
    static readonly HashSet<string> WritesAll =
    [
        "tee", "rm", "rmdir", "touch", "truncate", "shred", "unlink", "mv",
        "set-content", "add-content", "clear-content", "out-file", "new-item", "remove-item", "move-item",
        "sc", "ac", "clc", "ni", "ri", "del", "erase", "rd", "mi", "move",
    ];
    static readonly HashSet<string> WritesLast = ["cp", "install", "ln", "rsync", "scp", "copy-item", "cpi", "copy"];

    // ponytail: known writers only; a write from a script or an interpreter (python -c) is out of reach.
    static IEnumerable<string> WriteTargets(string name, List<string> args)
    {
        var paths = args.Where(a => !a.StartsWith('-')).ToList();
        if (WritesAll.Contains(name) || (name == "sed" && args.Any(a => a.StartsWith("-i") || a.StartsWith("--in-place"))))
            return paths;
        if (WritesLast.Contains(name)) return paths.TakeLast(1);
        if (name == "dd") return args.Where(a => a.StartsWith("of=")).Select(a => a[3..]);
        return [];
    }

    static string Resolve(string cwd, string path)
    {
        try { return Path.GetFullPath(Path.Combine(cwd, path)); }
        catch (ArgumentException) { return path; }
    }

    static void ScanGit(List<string> args, string cwd, Func<string, IReadOnlyDictionary<string, string>> aliases, Findings found, int depth)
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
            found.Commits.Add(new CommitCall(dir, skipsHooks || SkipsVerify(rest)));
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

    readonly record struct Heredoc(string Delimiter, bool StripTabs, bool Quoted);

    /// <summary>Reads the delimiter after <c>&lt;&lt;</c> or <c>&lt;&lt;-</c>; returns the index of its last character.</summary>
    static int ReadHeredocDelimiter(string command, int i, List<Heredoc> heredocs)
    {
        var stripTabs = i < command.Length && command[i] == '-';
        if (stripTabs) i++;
        while (i < command.Length && command[i] is ' ' or '\t') i++;
        var delimiter = new StringBuilder();
        var quoted = false;
        char quote = '\0';
        for (; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else delimiter.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; quoted = true; }
            else if (c == '\\' && i + 1 < command.Length) { quoted = true; delimiter.Append(command[++i]); }
            else if (c is ' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or '<' or '>' or '(' or ')') break;
            else delimiter.Append(c);
        }
        if (delimiter.Length > 0) heredocs.Add(new Heredoc(delimiter.ToString(), stripTabs, quoted));
        return i - 1;
    }

    /// <summary>Skips the heredoc bodies that start after the newline at <paramref name="i"/>; returns the index of the last newline skipped.</summary>
    static int SkipHeredocBodies(string command, int i, List<Heredoc> heredocs)
    {
        foreach (var heredoc in heredocs)
        {
            while (i < command.Length)
            {
                var end = command.IndexOf('\n', i + 1);
                if (end < 0) end = command.Length;
                var line = command[(i + 1)..end].TrimEnd('\r');
                i = end;
                if ((heredoc.StripTabs ? line.TrimStart('\t') : line) == heredoc.Delimiter) break;
            }
        }
        return i;
    }

    /// <summary>
    /// Splits a command line into simple commands (on <c>; &amp; | &amp;&amp; || ( ) $( `</c> and newlines) and each one
    /// into words, honouring single quotes, double quotes and backslash escapes. A command substitution inside double
    /// quotes is parsed as commands; comments and the bodies of quoted heredocs (<c>&lt;&lt;'EOF'</c>) are skipped.
    /// An unquoted heredoc body stays parsed as commands, since the shell expands the <c>$(...)</c> in it.
    /// </summary>
    internal static List<List<string>> Segments(string command)
    {
        var segments = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        char quote = '\0';
        // Open command substitutions: the quote to restore, the closer (')' or '`') and the nested '(' count.
        var substitutions = new Stack<(char Quote, char Closer, int Depth)>();
        var heredocs = new List<Heredoc>();

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
            if (c == '`' && quote == '\0' && substitutions.TryPeek(out var open) && open.Closer == '`')
            {
                EndSegment();
                substitutions.Pop();
                quote = open.Quote;
                inWord = quote != '\0';
                continue;
            }
            if (c == '`' || (c == '$' && i + 1 < command.Length && command[i + 1] == '('))
            {
                EndSegment();
                substitutions.Push((quote, c == '$' ? ')' : '`', 0));
                quote = '\0';
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
                case '#' when !inWord:
                    while (i + 1 < command.Length && command[i + 1] != '\n') i++;
                    break;
                case '<' when i + 2 < command.Length && command[i + 1] == '<' && command[i + 2] != '<' && (i == 0 || command[i - 1] != '<'):
                    EndWord();
                    i = ReadHeredocDelimiter(command, i + 2, heredocs);
                    break;
                case '\n' when heredocs.Count > 0:
                    EndSegment();
                    // One unquoted body on the line keeps every body parsed: they follow in order and cannot be told apart.
                    if (heredocs.All(h => h.Quoted)) i = SkipHeredocBodies(command, i, heredocs);
                    heredocs.Clear();
                    break;
                case '(' when substitutions.TryPeek(out var outer) && outer.Closer == ')':
                    EndSegment();
                    substitutions.Push(substitutions.Pop() with { Depth = outer.Depth + 1 });
                    break;
                case ')' when substitutions.TryPeek(out var inner) && inner.Closer == ')':
                    EndSegment();
                    substitutions.Pop();
                    if (inner.Depth > 0) substitutions.Push(inner with { Depth = inner.Depth - 1 });
                    else { quote = inner.Quote; inWord = quote != '\0'; }
                    break;
                case ';' or '&' or '|' or '(' or ')' or '\n' or '\r':
                    EndSegment();
                    break;
                case '>':
                    // `2>` and `&>`: the file descriptor is not a word. `>>`, `>|` and `>&` are one operator.
                    if (inWord && word.ToString().All(char.IsAsciiDigit)) { word.Clear(); inWord = false; }
                    EndWord();
                    while (i + 1 < command.Length && command[i + 1] is '>' or '|' or '&') i++;
                    words.Add(Redirect);
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
