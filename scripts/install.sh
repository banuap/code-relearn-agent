#!/usr/bin/env bash
# Builds the relearn CLI and installs ReLearn agents into a repo (Claude Code + Copilot + Spec Kit command).
# usage: scripts/install.sh <repo> [claude,copilot]
set -euo pipefail
REPO="${1:?usage: install.sh <repo> [claude,copilot]}"
TOOLS="${2:-claude,copilot}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ="$ROOT/src/ReLearn.Analyzer/ReLearn.Analyzer.csproj"

dotnet build "$PROJ" -c Release
CLI="dotnet $ROOT/src/ReLearn.Analyzer/bin/Release/net8.0/relearn.dll"

copy() { mkdir -p "$(dirname "$2")"; sed -e "s#\$RELEARN#$CLI#g" -e "s#{relearn_cli}#$CLI#g" -e "s#<relearn-repo>#$ROOT#g" "$1" > "$2"; echo "  + $2"; }

if [[ "$TOOLS" == *claude* ]]; then
  for f in "$ROOT"/agents/claude/agents/*.md;   do copy "$f" "$REPO/.claude/agents/$(basename "$f")"; done
  for f in "$ROOT"/agents/claude/commands/*.md; do copy "$f" "$REPO/.claude/commands/$(basename "$f")"; done
  copy "$ROOT/agents/speckit/commands/speckit.relearn.md" "$REPO/.claude/commands/speckit.relearn.md"
  for s in relearn-analyst relearn-extract relearn-handoff; do copy "$ROOT/agents/bmad/skills/$s/SKILL.md" "$REPO/.claude/skills/$s/SKILL.md"; done
fi
if [[ "$TOOLS" == *copilot* ]]; then
  for f in "$ROOT"/agents/claude/agents/*.md; do n="$(basename "$f" .md)"; copy "$f" "$REPO/.github/agents/$n.agent.md"; done
  copy "$ROOT/agents/speckit/commands/speckit.relearn.md" "$REPO/.github/agents/speckit.relearn.agent.md"
fi
echo "CLI: $CLI"
