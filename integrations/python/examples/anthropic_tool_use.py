"""
ReLearn via raw Anthropic tool use — no LangChain, no MCP.

    pip install anthropic
    set RELEARN_CLI=dotnet C:\\path\\to\\relearn.dll
    python examples/anthropic_tool_use.py C:\\src\\Legacy\\Legacy.sln java-spring C:\\src\\modernization
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import anthropic  # noqa: E402

from relearn_tools import anthropic_tools, call_tool, system_prompt  # noqa: E402

MODEL = os.environ.get("RELEARN_MODEL", "claude-sonnet-4-6")
solution, target, out = sys.argv[1], sys.argv[2], sys.argv[3]

client = anthropic.Anthropic()
messages = [{"role": "user", "content": f"Relearn {solution} for target {target}; write outputs to {out}. Run all phases."}]

for _ in range(200):
    resp = client.messages.create(model=MODEL, max_tokens=8000, system=system_prompt(), tools=anthropic_tools(), messages=messages)
    messages.append({"role": "assistant", "content": resp.content})
    if resp.stop_reason != "tool_use":
        print("".join(b.text for b in resp.content if b.type == "text"))
        break
    results = []
    for block in resp.content:
        if block.type == "tool_use":
            print(f"→ {block.name}({str(block.input)[:120]})")
            results.append({"type": "tool_result", "tool_use_id": block.id, "content": call_tool(block.name, block.input)})
    messages.append({"role": "user", "content": results})
else:
    print("Stopped: turn limit reached")
