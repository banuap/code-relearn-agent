"""
LangGraph pipeline: deterministic ReLearn workflow with per-capability LLM enrichment via function calling
(structured output), parallel fan-out, optional human review and resumable checkpoints.

    analyze → plan ─┬─ enrich(CAP-001) ─┐
                    ├─ enrich(CAP-002) ─┼→ merge → [review] → generate → verify → END
                    └─ …               ─┘
"""
from __future__ import annotations

import json
import operator
from typing import Annotated, Any, TypedDict

from langgraph.graph import END, START, StateGraph
from langgraph.types import Send
from pydantic import BaseModel, Field

from . import core

# ----------------------------------------------------------------------------- structured output


class RuleEnrichment(BaseModel):
    id: str = Field(description="BR-xxxx id from the bundle")
    statement: str | None = Field(None, description="Business sentence a domain expert can confirm; keep exact numbers/messages")
    confidence: str | None = Field(None, description="low | medium | high")
    questions: list[str] = Field(default_factory=list, description="Open questions for a domain expert")
    drop: bool = Field(False, description="True for technical guards with no business meaning, or duplicates")
    drop_reason: str | None = None


class FlowEnrichment(BaseModel):
    id: str = Field(description="FLOW-xxxx id from the bundle")
    name: str = Field(description="Short business name, verb first, e.g. 'Submit order'")
    goal: str = Field(description="What the actor achieves, phrased as the 'so that …' of a user story")
    actor: str | None = Field(None, description="Business role performing it")


class CapabilityEnrichment(BaseModel):
    id: str
    name: str = Field(description="Business name of the capability")
    description: str = Field(description="2-3 sentences: who uses it and what it does")
    flows: list[FlowEnrichment] = Field(default_factory=list)
    rules: list[RuleEnrichment] = Field(default_factory=list)


ENRICH_PROMPT = """You are ReLearn, reverse-engineering a legacy .NET system for modernization.
Below is ONE capability bundle extracted by Roslyn static analysis (JSON). Produce business-language enrichment:
- name/description of the capability;
- for every flow: business name, goal, actor;
- for every rule: a business statement (exact thresholds/messages), confidence, open questions;
  set drop=true only for technical guards or duplicates (give drop_reason).
Use only facts present in the bundle. Put uncertainty into questions, never guess.

CAPABILITY BUNDLE:
{bundle}
"""

# ----------------------------------------------------------------------------- state


class RelearnState(TypedDict, total=False):
    solution: str
    target: str
    out: str
    max_capabilities: int
    summary: str
    capabilities: list[dict[str, Any]]
    enrichments: Annotated[list[dict[str, Any]], operator.add]
    generated: str
    report: dict[str, Any]


class EnrichInput(TypedDict):
    cap: dict[str, Any]


# ----------------------------------------------------------------------------- graph


def build_pipeline(model: str = "anthropic:claude-sonnet-4-6", review: bool = False, checkpointer: Any = None):
    """Return a compiled LangGraph. review=True pauses before generation (needs a checkpointer)."""
    from langchain.chat_models import init_chat_model

    llm = init_chat_model(model).with_structured_output(CapabilityEnrichment)

    def analyze(state: RelearnState) -> dict:
        summary = core.relearn_analyze(state["solution"])
        if summary.startswith("ERROR"):
            raise RuntimeError(summary)
        return {"summary": summary}

    def plan(state: RelearnState) -> dict:
        caps = json.loads(core.list_capabilities())
        limit = state.get("max_capabilities") or len(caps)
        return {"capabilities": caps[:limit]}

    def fan_out(state: RelearnState) -> list[Send] | str:
        sends = [Send("enrich", {"cap": c}) for c in state["capabilities"]]
        return sends or "merge"  # nothing to enrich (e.g. no entry points found)

    def enrich(inp: EnrichInput) -> dict:
        bundle = core.read_capability(inp["cap"]["slug"])
        result: CapabilityEnrichment = llm.invoke(ENRICH_PROMPT.format(bundle=bundle))
        result.id = inp["cap"]["id"]
        return {"enrichments": [result.model_dump()]}

    def merge(state: RelearnState) -> dict:
        payload: dict[str, dict] = {"capabilities": {}, "flows": {}, "rules": {}}
        for e in state.get("enrichments", []):
            payload["capabilities"][e["id"]] = {"name": e["name"], "description": e["description"]}
            for f in e["flows"]:
                payload["flows"][f["id"]] = {"name": f["name"], "goal": f["goal"], "actor": f["actor"]}
            for r in e["rules"]:
                payload["rules"][r["id"]] = {
                    "statement": r["statement"], "confidence": r["confidence"], "questions": r["questions"] or None,
                    "drop": r["drop"], "dropReason": r["drop_reason"],
                }
        core.save_enrichment(payload)
        return {}

    def generate(state: RelearnState) -> dict:
        a = core.relearn_generate("speckit", state["target"], state["out"])
        b = core.relearn_generate("bmad", state["target"], state["out"])
        return {"generated": a + "\n" + b}

    def verify(state: RelearnState) -> dict:
        return {"report": json.loads(core.verify_outputs(state["out"]))}

    g = StateGraph(RelearnState)
    g.add_node("analyze", analyze)
    g.add_node("plan", plan)
    g.add_node("enrich", enrich)
    g.add_node("merge", merge)
    g.add_node("generate", generate)
    g.add_node("verify", verify)
    g.add_edge(START, "analyze")
    g.add_edge("analyze", "plan")
    g.add_conditional_edges("plan", fan_out, ["enrich", "merge"])
    g.add_edge("enrich", "merge")
    g.add_edge("merge", "generate")
    g.add_edge("generate", "verify")
    g.add_edge("verify", END)

    if review:
        from langgraph.checkpoint.memory import InMemorySaver

        return g.compile(checkpointer=checkpointer or InMemorySaver(), interrupt_before=["generate"])
    return g.compile(checkpointer=checkpointer)
