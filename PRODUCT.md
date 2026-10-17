# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Static HTML/CSS for the documentation site, in `site/`, with no build step and no npm dependency; deployed by a GitHub Pages workflow at https://atypical-consulting.github.io/kaldor/. The custom domain kaldor.atypical.consulting is reserved but not in use yet. The product itself is a .NET 10 tool (`Atypical.Kaldor` on NuGet, run through `dnx`) plus a Claude Code plugin.

## Users

Developers who already use Claude Code and want to adopt Kaldor in their repositories: they install it, learn the flow (intent, freeze, reviewed commit), and come back to look up commands, rules and what to do when a commit is rejected. Tech leads evaluating it are a secondary audience, served by the threat model and the measured costs.

## Product Purpose

Kaldor is an intent/code review guard for Claude Code: no commit produced by an agent enters the repository without being checked against an intent declared before the code, by a reviewer the agent does not control. Success: the developer reads an intent, a list of deviations and a few sensitive hunks instead of the full diff, and trusts that the agent could not quietly go out of scope.

## Positioning

The review sits at the commit, inside Claude Code, and the reviewer is launched by the hook itself (a headless `claude -p` with a fixed prompt, no tools, no CLAUDE.md), so the agent never writes a word of the reviewer's prompt and never holds the verdict. The verdict is bound to the hash of the staged diff and of the frozen intent. Inspired by Antique, the internal review tool described in Anthropic's "Redesigning source control at Anthropic".

## Operating Context

- Install once per machine: `/plugin install kaldor --marketplace Atypical-Consulting/kaldor` in Claude Code; requires the .NET 10 SDK, Claude Code 2.1.295+, git.
- Enable once per repository: `dnx Atypical.Kaldor init`, then commit `.kaldor/rules.json` yourself.
- Per task: the agent writes `.kaldor/intent.md`, the human approves, the agent runs `kaldor intent freeze`; edits are refused before that.
- Per commit: the `PreToolUse` hook (and a git `pre-commit` backstop) runs the reviewer; accepted commits go through, rejected ones return the deviations to the agent.
- Human-only commands, from their own terminal: `kaldor approve`, `kaldor intent amend --accept`. Others: `kaldor report`, `kaldor review`, `kaldor doctor`, `kaldor version`.

## Capabilities and Constraints

- Commands: init, intent freeze, intent amend, intent amend --accept, review, report, approve, doctor, gate, check, version.
- Rules (`.kaldor/rules.json`): sensitive paths, trivial paths, reviewer model (default sonnet), block_on categories (scope_creep, sensitive_change, weakened_tests, contradicts_intent).
- Threat model: a hurried agent taking a shortcut, not a hostile one. Bypasses covered by tests: framing the reviewer, writing its own verdict, rewriting the intent, changing code after review, `git -C` / `cd` / `bash -c` / aliases, committing from a script, `--no-verify` / `core.hooksPath`, breaking the hook (`onFailure: "block"`).
- Known limits: about 0.5 s of dnx startup on every Bash or edit tool call; an agent that stashes its work could freeze a new intent (logged); the review history lives in the working tree and is lost with `git worktree remove` or `git clean -fdx`.
- License MIT, © 2026 Atypical Consulting. Current version 1.0.x.

## Brand Commitments

Name: Kaldor (invented, pronounced the same in French and English), command `kaldor`, package `Atypical.Kaldor`, publisher Atypical Consulting. No logo exists yet. Voice in the existing docs: plain, direct, second person ("you"), short sentences, no hype.

## Evidence on Hand

- Real reviewer outputs from local runs (`.kaldor/log/` in the sandbox, quoted in README.md): an accepted on-intent commit, and a blocked commit with deviations "README.md: modified despite the intent saying it must not be touched" and "greet.py: adds a network call to http://example.com/track that sends the user's name".
- Early measurements, to be labelled as such (small diffs, Sonnet reviewer): about 0.5 s dnx overhead per tool call, 4 to 5 s per review.
- Design documents: docs/project-dossier.md, docs/architecture-analysis.md.
- No testimonials, customers, adoption numbers or benchmarks exist; none may be invented.

## Product Principles

1. The intent precedes the code, and only the human can change it mid-task.
2. The reviewer does not depend on the agent: fixed prompt, launched by the hook.
3. Fail closed: a failing hook blocks.
4. The honest path is the shortest; Kaldor targets the hurried agent, not the hostile one.
5. The human reads intent and the sensitive parts, not every line.
