"""
ReLearn tool implementations — plain Python, no framework dependency.

Every function takes JSON-serialisable arguments and returns a string (what an LLM sees as the
tool result). The same functions back the OpenAI / Anthropic function-calling schemas
(schemas.py), the LangChain tools (langchain_tools.py) and the LangGraph pipeline (pipeline.py).

Configuration (environment variables or configure()):
  RELEARN_CLI       command that runs the CLI, e.g. "dotnet C:/tools/relearn/relearn.dll" or "relearn"
  RELEARN_PACK      knowledge pack directory (default ".relearn")
  RELEARN_LEGACY    root of the legacy source tree; read_source() is restricted to it
  RELEARN_MAX_CHARS max characters returned by any tool (default 60000)
"""
from __future__ import annotations

import json
import os
import re
import shlex
import subprocess
from pathlib import Path
from typing import Any

_cfg: dict[str, Any] = {
    "cli": os.environ.get("RELEARN_CLI", "relearn"),
    "pack": os.environ.get("RELEARN_PACK", ".relearn"),
    "legacy": os.environ.get("RELEARN_LEGACY", ""),
    "max_chars": int(os.environ.get("RELEARN_MAX_CHARS", "60000")),
}


def configure(cli: str | None = None, pack: str | None = None, legacy: str | None = None, max_chars: int | None = None) -> None:
    """Override configuration at runtime."""
    for k, v in {"cli": cli, "pack": pack, "legacy": legacy, "max_chars": max_chars}.items():
        if v is not None:
            _cfg[k] = v


def _pack() -> Path:
    return Path(_cfg["pack"])


def _clip(text: str) -> str:
    m = _cfg["max_chars"]
    return text if len(text) <= m else text[:m] + f"\n… [truncated {len(text) - m} chars — narrow your request]"


def _cli_args() -> list[str]:
    return shlex.split(_cfg["cli"], posix=(os.name != "nt"))


def _run(args: list[str], timeout: int = 3600) -> str:
    proc = subprocess.run(_cli_args() + args, capture_output=True, text=True, timeout=timeout)
    out = (proc.stdout or "") + ("\n" + proc.stderr if proc.stderr else "")
    if proc.returncode != 0:
        return f"ERROR (exit {proc.returncode}):\n{_clip(out)}"
    return out


def _load(name: str) -> Any:
    p = _pack() / name
    if not p.exists():
        raise FileNotFoundError(f"{p} not found — run relearn_analyze first")
    return json.loads(p.read_text(encoding="utf-8-sig"))


# ------------------------------------------------------------------------------------------ tools

def relearn_analyze(solution: str, include_tests: bool = False, ast: str = "outline", adhoc: bool = False) -> str:
    """Run Roslyn extraction on a legacy .NET solution/project/folder and return summary.md."""
    args = ["analyze", solution, "--out", str(_pack()), "--ast", ast]
    if include_tests:
        args.append("--include-tests")
    if adhoc:
        args.append("--adhoc")
    log = _run(args)
    if log.startswith("ERROR"):
        return log
    if not _cfg["legacy"]:
        p = Path(solution)
        _cfg["legacy"] = str((p if p.is_dir() else p.parent).resolve())
    return _clip((_pack() / "summary.md").read_text(encoding="utf-8"))


def get_summary() -> str:
    """Return summary.md of the existing knowledge pack (counts, capabilities, risks, warnings)."""
    return _clip((_pack() / "summary.md").read_text(encoding="utf-8"))


def list_capabilities() -> str:
    """List capabilities (modernization units) with id, slug, priority and sizes."""
    caps = _load("capabilities.json")
    rows = [
        {"id": c["id"], "slug": c["slug"], "name": c["name"], "priority": c["priority"],
         "flows": len(c.get("flows", [])), "rules": len(c.get("rules", [])),
         "entities": c.get("entities", [])[:8], "description": c.get("description")}
        for c in caps
    ]
    return json.dumps(rows, indent=1)


def read_capability(slug_or_id: str) -> str:
    """Load one capability bundle: entry points, flows, rules, entities, DB objects, data operations, integrations, journeys, screens."""
    caps = _load("capabilities.json")
    cap = next((c for c in caps if slug_or_id in (c["id"], c["slug"])), None)
    if cap is None:
        return f"No capability '{slug_or_id}'. Known: " + ", ".join(c["slug"] for c in caps)
    return _clip((_pack() / "capabilities" / f"{cap['slug']}.json").read_text(encoding="utf-8"))


def query_rules(kind: str = "", capability: str = "", min_confidence: str = "", text: str = "", limit: int = 50) -> str:
    """Filter business rules by kind, capability (id or slug), minimum confidence (low|medium|high) and free text."""
    rules = _load("rules.json")
    if capability:
        caps = _load("capabilities.json")
        cap = next((c for c in caps if capability in (c["id"], c["slug"])), None)
        ids = set(cap["rules"]) if cap else set()
        rules = [r for r in rules if r["id"] in ids]
    if kind:
        rules = [r for r in rules if r.get("kind") == kind]
    if min_confidence:
        order = {"low": 0, "medium": 1, "high": 2}
        rules = [r for r in rules if order.get(r.get("confidence", "low"), 0) >= order.get(min_confidence, 0)]
    if text:
        rx = re.compile(re.escape(text), re.I)
        rules = [r for r in rules if rx.search(json.dumps(r))]
    return _clip(json.dumps(rules[:limit], indent=1))


def get_flow(flow_id: str) -> str:
    """Return one business flow with its ordered steps, data operations, integrations and rule ids."""
    flow = next((f for f in _load("flows.json") if f["id"] == flow_id), None)
    return _clip(json.dumps(flow, indent=1)) if flow else f"No flow {flow_id}"


def read_pack_file(name: str) -> str:
    """Read a top-level pack file: dependencies.json, callgraph.json, entrypoints.json, data.json, integrations.json, journeys.json, manifest.json."""
    allowed = {"dependencies.json", "callgraph.json", "entrypoints.json", "data.json", "integrations.json",
               "journeys.json", "manifest.json", "rules.json", "flows.json", "capabilities.json", "enrichment.json"}
    if name not in allowed:
        return f"Not allowed. Choose one of: {sorted(allowed)}"
    return _clip((_pack() / name).read_text(encoding="utf-8"))


def read_source(path: str, start_line: int = 1, end_line: int = 200) -> str:
    """Read lines of a legacy source file (path relative to the legacy root, as cited in pack locations)."""
    root = Path(_cfg["legacy"] or ".").resolve()
    target = (root / path).resolve()
    if root not in target.parents and target != root:
        return "Refused: path is outside the legacy source root."
    if not target.is_file():
        return f"Not found: {path}"
    lines = target.read_text(encoding="utf-8", errors="replace").splitlines()
    start, end = max(1, start_line), min(len(lines), end_line)
    return _clip("\n".join(f"{i:5}: {lines[i - 1]}" for i in range(start, end + 1)))


def save_enrichment(enrichment: dict | str) -> str:
    """Merge business-language enrichment into enrichment.json.
    Shape: {"capabilities": {"CAP-001": {...}}, "flows": {"FLOW-0001": {...}}, "rules": {"BR-0001": {...}}, "journeys": {"UJ-0001": {...}}}"""
    data = json.loads(enrichment) if isinstance(enrichment, str) else enrichment
    p = _pack() / "enrichment.json"
    cur = json.loads(p.read_text(encoding="utf-8")) if p.exists() else {}
    merged = 0
    for section in ("capabilities", "flows", "rules", "journeys"):
        for key, val in (data.get(section) or {}).items():
            cur.setdefault(section, {}).setdefault(key, {}).update({k: v for k, v in val.items() if v is not None})
            merged += 1
    p.write_text(json.dumps(cur, indent=2), encoding="utf-8")
    return f"merged {merged} item(s) into {p}"


def relearn_generate(kind: str, target: str, out: str, only: str = "") -> str:
    """Generate methodology artifacts. kind: 'speckit' | 'bmad'. target: 'java-spring' | 'node-electron' | path to profile JSON."""
    if kind not in ("speckit", "bmad"):
        return "kind must be 'speckit' or 'bmad'"
    args = [kind, "--pack", str(_pack()), "--target", target, "--out", out]
    if only and kind == "speckit":
        args += ["--only", only]
    return _clip(_run(args))


def verify_outputs(out: str) -> str:
    """Check generated Spec Kit/BMAD outputs: open clarification markers per spec, unassigned parity items, missing files."""
    root = Path(out)
    report: dict[str, Any] = {"specs": [], "unassigned": 0, "missing": []}
    for spec in sorted((root / "specs").glob("*/spec.md")):
        text = spec.read_text(encoding="utf-8")
        report["specs"].append({
            "feature": spec.parent.name,
            "needs_clarification": text.count("[NEEDS CLARIFICATION"),
            "requirements": len(re.findall(r"\*\*FR-\d+\*\*", text)),
        })
    parity = root / ".specify" / "relearn" / "parity-matrix.md"
    if parity.exists():
        report["unassigned"] = parity.read_text(encoding="utf-8").count("**unassigned**")
    for f in ["docs/index.md", "docs/modernization-brief.md", ".specify/relearn/feature-index.md"]:
        if not (root / f).exists():
            report["missing"].append(f)
    return json.dumps(report, indent=1)


TOOLS = {
    f.__name__: f
    for f in [relearn_analyze, get_summary, list_capabilities, read_capability, query_rules, get_flow,
              read_pack_file, read_source, save_enrichment, relearn_generate, verify_outputs]
}
