#!/usr/bin/env bash
# Crée le dépôt public Kaldor sous l'organisation Atypical Consulting et pousse le contenu.
# Usage : ./publish.sh [organisation]   (défaut : Atypical-Consulting)
set -euo pipefail
ORG="${1:-Atypical-Consulting}"
cd "$(dirname "$0")"
gh repo create "$ORG/kaldor" --public \
  --description "Garde de revue intention/code pour Claude Code" \
  --homepage "https://kaldor.atypical.consulting" \
  --source . --remote origin --push
gh repo edit "$ORG/kaldor" --add-topic claude-code,code-review,dotnet,ai-agents,git-hooks
echo "Dépôt publié : https://github.com/$ORG/kaldor"
