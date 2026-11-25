"""LangChain adapter: the ReLearn functions as LangChain tools (native function calling via bind_tools / create_agent)."""
from __future__ import annotations

from typing import Any

from langchain_core.tools import StructuredTool

from . import core
from .prompt import system_prompt
from .schemas import TOOL_SPECS

_DESC = {t["name"]: t["description"] for t in TOOL_SPECS}


def _save_enrichment(enrichment: dict[str, Any]) -> str:
    """Merge business-language enrichment into enrichment.json."""
    return core.save_enrichment(enrichment)


def get_tools() -> list[StructuredTool]:
    tools = []
    for name, fn in core.TOOLS.items():
        impl = _save_enrichment if name == "save_enrichment" else fn
        tools.append(StructuredTool.from_function(func=impl, name=name, description=_DESC.get(name, fn.__doc__ or name)))
    return tools


def create_relearn_agent(model: str = "anthropic:claude-sonnet-4-6", **kwargs: Any):
    """Free-roaming ReLearn agent (LangChain 1.x create_agent). For large solutions prefer pipeline.build_pipeline()."""
    from langchain.agents import create_agent

    return create_agent(model=model, tools=get_tools(), system_prompt=system_prompt(), **kwargs)
