"""
Function-calling schemas for the ReLearn tools — provider neutral.

* TOOL_SPECS           — canonical list: {"name", "description", "parameters": <JSON Schema>}
* openai_tools()       — OpenAI / Azure OpenAI Chat Completions `tools=[...]` format
* anthropic_tools()    — Anthropic Messages API `tools=[...]` format
* call_tool(name, args)— dispatcher: executes a tool call and returns the string result
* export(path)         — writes relearn_tools.json for non-Python stacks (Spring AI, Semantic Kernel, etc.)
"""
from __future__ import annotations

import json
import traceback
from typing import Any

from .core import TOOLS

_str = {"type": "string"}
_bool = {"type": "boolean"}
_int = {"type": "integer"}

TOOL_SPECS: list[dict[str, Any]] = [
    {
        "name": "relearn_analyze",
        "description": "Run Roslyn static analysis on a legacy .NET solution (.sln/.csproj/folder) and write the knowledge pack "
                       "(call graph, dependency map, AST, business rules, flows, journeys, DB objects, integrations). Returns summary.md.",
        "parameters": {"type": "object", "properties": {
            "solution": {**_str, "description": "Path to .sln, .slnx, .csproj, .vbproj or folder"},
            "include_tests": {**_bool, "description": "Also analyze test projects (good rule evidence)"},
            "ast": {"type": "string", "enum": ["none", "outline", "full"], "description": "AST detail level"},
            "adhoc": {**_bool, "description": "Skip MSBuild and parse sources only (lower fidelity)"},
        }, "required": ["solution"]},
    },
    {"name": "get_summary", "description": "Return summary.md of the existing knowledge pack.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "list_capabilities", "description": "List capabilities (modernization units) with id, slug, priority, flow/rule counts and entities.",
     "parameters": {"type": "object", "properties": {}}},
    {
        "name": "read_capability",
        "description": "Load one capability bundle (entry points, flows, rules, entities, DB objects, data operations, integrations, journeys, screens). Load one at a time.",
        "parameters": {"type": "object", "properties": {"slug_or_id": {**_str, "description": "Capability slug or CAP-xxx id"}},
                       "required": ["slug_or_id"]},
    },
    {
        "name": "query_rules",
        "description": "Filter business rules by kind, capability, minimum confidence and free text.",
        "parameters": {"type": "object", "properties": {
            "kind": {"type": "string", "enum": ["", "validation", "guard", "existence", "authorization", "decision", "decision-table",
                                                 "state-transition", "calculation", "declarative-validation", "fluent-validation",
                                                 "parameter", "data-constraint"]},
            "capability": _str,
            "min_confidence": {"type": "string", "enum": ["", "low", "medium", "high"]},
            "text": _str,
            "limit": _int,
        }},
    },
    {"name": "get_flow", "description": "Return one business flow (FLOW-xxxx) with ordered steps, data, integrations and rules.",
     "parameters": {"type": "object", "properties": {"flow_id": _str}, "required": ["flow_id"]}},
    {
        "name": "read_pack_file",
        "description": "Read a top-level knowledge-pack JSON file.",
        "parameters": {"type": "object", "properties": {"name": {"type": "string", "enum": [
            "dependencies.json", "callgraph.json", "entrypoints.json", "data.json", "integrations.json", "journeys.json",
            "manifest.json", "rules.json", "flows.json", "capabilities.json", "enrichment.json"]}}, "required": ["name"]},
    },
    {
        "name": "read_source",
        "description": "Read lines of a legacy source file to verify extracted facts. Path relative to the legacy root, as in pack locations.",
        "parameters": {"type": "object", "properties": {"path": _str, "start_line": _int, "end_line": _int}, "required": ["path"]},
    },
    {
        "name": "save_enrichment",
        "description": "Merge business-language enrichment into enrichment.json. Keys: capabilities{CAP-id:{name,description,priority}}, "
                       "flows{FLOW-id:{name,goal,actor}}, rules{BR-id:{statement,title,confidence,questions[],drop,dropReason}}, journeys{UJ-id:{name,actor}}.",
        "parameters": {"type": "object", "properties": {"enrichment": {"type": "object", "properties": {
            "capabilities": {"type": "object"}, "flows": {"type": "object"}, "rules": {"type": "object"}, "journeys": {"type": "object"}}}},
            "required": ["enrichment"]},
    },
    {
        "name": "relearn_generate",
        "description": "Generate Spec Kit (kind=speckit) or BMAD (kind=bmad) artifacts from the pack for a target profile.",
        "parameters": {"type": "object", "properties": {
            "kind": {"type": "string", "enum": ["speckit", "bmad"]},
            "target": {**_str, "description": "java-spring | node-electron | path to profile JSON"},
            "out": {**_str, "description": "Output repository folder"},
            "only": {**_str, "description": "Optional comma-separated capability ids/slugs (speckit only)"},
        }, "required": ["kind", "target", "out"]},
    },
    {"name": "verify_outputs", "description": "Check generated outputs: clarification markers per spec, unassigned parity items, missing files.",
     "parameters": {"type": "object", "properties": {"out": _str}, "required": ["out"]}},
]


def openai_tools() -> list[dict[str, Any]]:
    return [{"type": "function", "function": {"name": t["name"], "description": t["description"], "parameters": t["parameters"]}}
            for t in TOOL_SPECS]


def anthropic_tools() -> list[dict[str, Any]]:
    return [{"name": t["name"], "description": t["description"], "input_schema": t["parameters"]} for t in TOOL_SPECS]


def call_tool(name: str, args: dict[str, Any] | str | None) -> str:
    """Execute a function call coming from any LLM provider. Never raises: errors are returned to the model."""
    fn = TOOLS.get(name)
    if fn is None:
        return f"Unknown tool '{name}'"
    try:
        kwargs = json.loads(args) if isinstance(args, str) else (args or {})
        return fn(**kwargs)
    except Exception as ex:  # surface the error to the model so it can correct the call
        return f"ERROR in {name}: {ex}\n{traceback.format_exc(limit=2)}"


def export(path: str = "relearn_tools.json") -> str:
    with open(path, "w", encoding="utf-8") as f:
        json.dump(TOOL_SPECS, f, indent=2)
    return path
