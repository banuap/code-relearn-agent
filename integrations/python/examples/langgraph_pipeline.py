"""
ReLearn as a LangGraph pipeline (recommended for large solutions).

    pip install -r requirements.txt
    set RELEARN_CLI=dotnet C:\\path\\to\\relearn.dll
    python examples/langgraph_pipeline.py C:\\src\\Legacy\\Legacy.sln java-spring C:\\src\\modernization [--review]

With --review the graph pauses after enrichment so a human can edit .relearn/enrichment.json,
then resumes generation in the same thread.
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from relearn_tools.pipeline import build_pipeline  # noqa: E402

solution, target, out = sys.argv[1], sys.argv[2], sys.argv[3]
review = "--review" in sys.argv
graph = build_pipeline(model=os.environ.get("RELEARN_MODEL", "anthropic:claude-sonnet-4-6"), review=review)
config = {"configurable": {"thread_id": "relearn-1"}, "recursion_limit": 500}

state = graph.invoke({"solution": solution, "target": target, "out": out}, config)
if review:
    input("Enrichment written to .relearn/enrichment.json — review/edit it, then press Enter to generate… ")
    state = graph.invoke(None, config)  # resume after the interrupt

print(json.dumps(state.get("report", {}), indent=2))
