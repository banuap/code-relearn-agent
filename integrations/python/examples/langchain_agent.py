"""
ReLearn as a LangChain agent (function calling through create_agent).

    pip install -r requirements.txt
    set RELEARN_CLI=dotnet C:\\path\\to\\relearn.dll
    python examples/langchain_agent.py C:\\src\\Legacy\\Legacy.sln java-spring C:\\src\\modernization
"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from relearn_tools.langchain_tools import create_relearn_agent  # noqa: E402

solution, target, out = sys.argv[1], sys.argv[2], sys.argv[3]
agent = create_relearn_agent(model=os.environ.get("RELEARN_MODEL", "anthropic:claude-sonnet-4-6"))

for chunk in agent.stream(
    {"messages": [{"role": "user", "content": f"Relearn {solution} for target {target}; write outputs to {out}. Run all phases."}]},
    {"recursion_limit": 300},
    stream_mode="updates",
):
    for node, update in chunk.items():
        for m in update.get("messages", []):
            print(f"[{node}] {getattr(m, 'content', '')!s:.300}")
