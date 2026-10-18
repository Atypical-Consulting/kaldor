---
name: intent
description: Use at the start of every coding task in a repository that has a .kaldor/ folder, before the first Edit or Write - writes and freezes the task's intent so Kaldor can review each commit against it. Also use when a Kaldor message blocks an edit or a commit ("No frozen intent", "rejected this commit", "intent changed after it was frozen").
---

# Kaldor intent

This repository is guarded by Kaldor. Every commit you make is reviewed by an independent reviewer that sees
only the frozen intent, the project rules in `.kaldor/rules.json` and the staged diff. It never sees this
conversation, so the intent must say everything the reviewer needs.

## Start of a task

1. Start from a clean work tree (commit or discard earlier work first).
2. Write `.kaldor/intent.md`, short and concrete:

   ```markdown
   # Intent
   Goal: <one sentence>
   Expected behavior: <what changes for a user or a caller>
   Must not touch: <areas, files or behaviors that must stay as they are>
   Expected files: <files you expect to change, tests included>
   ```

3. Show the intent to the user and wait for their agreement or correction.
4. Freeze it: `kaldor intent freeze`. From then on, edits are allowed.
5. Ask the user to confirm it by running `dnx Atypical.Kaldor intent confirm` in a terminal outside Claude Code
   (the `!` prefix has no terminal, and `kaldor` is on PATH only inside Claude Code). Commits are refused until
   they do; you cannot run it yourself.
6. Right away, run `kaldor intent wait` in the background and keep working on the edits meanwhile. It returns
   as soon as the user has confirmed; then commit. Do not ask the user to tell you when they are done. If it times
   out, run it again, or check in with the user.

## Committing

Stage and commit as usual. The commit waits for the review (up to a few minutes).

- **Accepted**: the commit goes through.
- **Rejected**: you receive the deviations. Fix the code so it matches the intent, then commit again. Do not
  try to get around the review: skipping hooks, writing Kaldor's state or rules, committing from a script or rewriting
  the intent are all blocked, and they waste the user's trust.

## When the intent itself must change

If the work genuinely needs more than the intent allows, do not edit `.kaldor/intent.md`. Propose an amendment:

```bash
kaldor intent amend "Also add an index on orders.created_at: pagination is too slow without it."
```

Then tell the user and ask them to run `dnx Atypical.Kaldor intent amend --accept` in a terminal outside Claude Code. Only they can
accept it; until then, commits are reviewed against the original intent.

If the user disagrees with a rejection, they can force the commit with `dnx Atypical.Kaldor approve` in a terminal outside Claude Code.
Never run `kaldor approve`, `kaldor intent confirm` or `kaldor intent amend --accept` yourself: all three are
refused to you.
