#!/usr/bin/env bash
# Creates the public Kaldor repository under the Atypical Consulting organization and pushes the content.
# Usage: ./publish.sh [organization]   (default: Atypical-Consulting)
set -euo pipefail
ORG="${1:-Atypical-Consulting}"
cd "$(dirname "$0")"
gh repo create "$ORG/kaldor" --public \
  --description "Intent/code review guard for Claude Code" \
  --homepage "https://kaldor.atypical.consulting" \
  --source . --remote origin --push
gh repo edit "$ORG/kaldor" --add-topic claude-code,code-review,dotnet,ai-agents,git-hooks
echo "Repository published: https://github.com/$ORG/kaldor"
