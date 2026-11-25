"""System prompt for function-calling agents: reuses the Claude agent definition so all runtimes behave the same."""
from __future__ import annotations

from pathlib import Path

_AGENT_MD = Path(__file__).resolve().parents[3] / "agents" / "claude" / "agents" / "dotnet-relearn.md"

TOOL_NOTE = """
## Tools in this runtime
You call functions instead of shell commands:
relearn_analyze → get_summary / list_capabilities → read_capability (one at a time) + query_rules + get_flow + read_source
→ save_enrichment (per capability) → relearn_generate(kind="speckit") and relearn_generate(kind="bmad") → verify_outputs.
Never invent facts: every statement must come from a tool result.
"""


def system_prompt() -> str:
    text = _AGENT_MD.read_text(encoding="utf-8") if _AGENT_MD.exists() else "You are ReLearn, a legacy .NET reverse-engineering agent."
    if text.startswith("---"):
        text = text.split("---", 2)[2]  # drop YAML front matter
    return text.strip() + "\n" + TOOL_NOTE
