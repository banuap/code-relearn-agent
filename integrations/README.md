# ReLearn integrations — function calling (no MCP required)

The ReLearn agent is a set of **11 tools** plus a **system prompt** (`agents/claude/agents/dotnet-relearn.md`).
The tools are defined once and work in any framework that supports LLM function calling.

| tool | purpose |
|---|---|
| `relearn_analyze` | run Roslyn extraction on a `.sln`/`.csproj`/folder → knowledge pack, returns `summary.md` |
| `get_summary`, `list_capabilities` | orient: counts, risks, modernization units |
| `read_capability` | load one capability bundle (flows, rules, entities, DB objects, integrations, journeys) |
| `query_rules`, `get_flow`, `read_pack_file` | targeted look-ups in the pack |
| `read_source` | read legacy source lines to verify a fact (restricted to the legacy root) |
| `save_enrichment` | write business-language rules/flows/capabilities into `enrichment.json` |
| `relearn_generate` | emit Spec Kit (`speckit`) or BMAD (`bmad`) artifacts for a target profile |
| `verify_outputs` | count open clarifications, unassigned parity items, missing files |

## Pick your runtime

| runtime | use |
|---|---|
| **LangChain** (`create_agent`) | `relearn_tools.langchain_tools.create_relearn_agent()` — see `python/examples/langchain_agent.py` |
| **LangGraph** (recommended for large solutions) | `relearn_tools.pipeline.build_pipeline()` — analyze → per-capability parallel enrichment with structured output → optional human review → generate → verify. See `python/examples/langgraph_pipeline.py` |
| **OpenAI / Azure OpenAI SDK** | `openai_tools()` + `call_tool()` — `python/examples/openai_function_calling.py` |
| **Anthropic SDK** | `anthropic_tools()` + `call_tool()` — `python/examples/anthropic_tool_use.py` |
| **Any other stack** (Spring AI, Semantic Kernel, LangChain4j, Vercel AI SDK…) | `relearn_tools.json` holds the JSON-Schema definitions; implement each tool by shelling out to the `relearn` CLI and reading the pack JSON files |

## Setup

```powershell
cd integrations\python
pip install -r requirements.txt
$env:RELEARN_CLI   = "dotnet C:\path\to\code-relearn-agent\src\ReLearn.Analyzer\bin\Release\net8.0\relearn.dll"
$env:RELEARN_PACK  = "C:\src\modernization\.relearn"     # where the knowledge pack is written
$env:RELEARN_LEGACY = "C:\src\Legacy"                     # optional: root for read_source (defaults to the solution folder)
$env:ANTHROPIC_API_KEY = "…"                              # or OPENAI_API_KEY etc.

python examples\langgraph_pipeline.py C:\src\Legacy\Legacy.sln java-spring C:\src\modernization --review
```

Set `RELEARN_MODEL` to change the model (e.g. `openai:gpt-4.1` for LangChain, `gpt-4.1` for the raw OpenAI example).

## Free agent vs pipeline
- **Free agent** (LangChain/raw SDK loops): the model decides which tools to call, following the ReLearn prompt. Flexible and good for exploration and Q&A ("what happens when an order exceeds the credit limit?").
- **LangGraph pipeline**: fixed phases, one LLM call per capability with a typed schema (`CapabilityEnrichment`), parallel fan-out, resumable with a checkpointer, human review before generation. Predictable cost and output — use it for real modernization runs.

Tool results are capped at `RELEARN_MAX_CHARS` (default 60 000) so a large bundle never overflows the context window; the model is told to narrow its request when that happens.
