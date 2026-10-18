using Kaldor;

const string Usage = """
    kaldor: intent/code review guard for Claude Code

    Usage:
      kaldor init                       enable Kaldor in this repository
      kaldor intent freeze              freeze .kaldor/intent.md (the agent runs it)
      kaldor intent confirm             confirm the frozen intent; commits wait for it (you only)
      kaldor intent confirm --all       confirm every draft in .kaldor/queue/ up front (you only)
      kaldor intent wait [--timeout s]  wait until the user confirms the intent (the agent; default 540 s)
      kaldor intent amend "<text>"      propose a change to the frozen intent (the agent)
      kaldor intent amend --accept      accept the proposed change (you only)
      kaldor review                     dry-run review of the staged diff, nothing recorded
      kaldor report [count] [--unconfirmed]  latest reviews at intent level (default 10); only those made before confirmation
      kaldor approve                    force the staged diff through (you only)
      kaldor doctor                     check the installation
      kaldor gate                       PreToolUse hook entry point (reads the hook JSON on stdin)
      kaldor check                      git pre-commit hook entry point
      kaldor version                    print the version (also --version)
    """;

var cwd = Environment.CurrentDirectory;
Reviewer review = Claude.Review;

try
{
    return args switch
    {
        ["gate"] => Gate.Run(Console.In.ReadToEnd(), Console.Error, review),
        ["check"] => Commands.Check(cwd, review, Environment.GetEnvironmentVariable("CLAUDECODE") == "1", Console.Error),
        ["init"] => Commands.Init(cwd),
        ["intent", "freeze"] => Commands.Freeze(cwd),
        ["intent", "confirm"] => Commands.ConfirmIntent(cwd),
        ["intent", "confirm", "--all"] => Commands.ConfirmAll(cwd),
        ["intent", "wait"] => Commands.WaitForConfirmation(cwd, TimeSpan.FromSeconds(540)),
        ["intent", "wait", "--timeout", var s] when int.TryParse(s, out var seconds) && seconds > 0 =>
            Commands.WaitForConfirmation(cwd, TimeSpan.FromSeconds(seconds)),
        ["intent", "amend", "--accept"] => Commands.AcceptAmendment(cwd),
        ["intent", "amend", .. var text] when text.Length > 0 => Commands.ProposeAmendment(cwd, string.Join(' ', text)),
        ["review"] => Commands.Review(cwd, review),
        ["report"] => Commands.Report(cwd, 10),
        ["report", "--unconfirmed"] => Commands.Report(cwd, 10, unconfirmedOnly: true),
        ["report", var n, "--unconfirmed"] when int.TryParse(n, out var uc) => Commands.Report(cwd, uc, unconfirmedOnly: true),
        ["report", "--unconfirmed", var m] when int.TryParse(m, out var uc2) => Commands.Report(cwd, uc2, unconfirmedOnly: true),
        ["report", var n] when int.TryParse(n, out var count) => Commands.Report(cwd, count),
        ["approve"] => Commands.Approve(cwd),
        ["doctor"] => Commands.Doctor(cwd),
        // `version` too: dnx keeps `--version` for itself, so `dnx Atypical.Kaldor --version` never reaches us.
        ["version" or "--version"] => Print(Commands.Version, 0),
        ["--help" or "-h" or "help"] => Print(Usage, 0),
        _ => Print(Usage, 1),
    };
}
catch (KaldorException ex)
{
    Console.Error.WriteLine($"Kaldor: {ex.Message}");
    return 1;
}

static int Print(string text, int code)
{
    (code == 0 ? Console.Out : Console.Error).WriteLine(text);
    return code;
}
