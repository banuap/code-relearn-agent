# Knowledge pack format (schema 1.0)

`relearn analyze` writes a folder that is the contract between the **ReLearn** (extraction) agent and any **modernization** agent. It is language-neutral: nothing in it assumes the target is Java, Node or anything else.

```
.relearn/
├── manifest.json            tool/version, load mode, fidelity, counts, warnings, file index
├── summary.md               human/LLM entry point — read first
├── dependencies.json        projects, packages, types (+stereotype), type/namespace/layer edges, DI registrations
├── callgraph.json           method nodes, call edges (direct/virtual/interface/ctor/delegate/event/lambda/mediator),
│                            interface→implementation map, external API calls
├── entrypoints.json         HTTP/minimal API/Razor/MVC, WinForms/WPF/WebForms events & commands, WCF/ASMX/gRPC/SignalR,
│                            hosted services, jobs, message consumers, Azure Functions, Main
├── data.json                DbContexts, entities (fields/keys/relations/constraints), DB objects (tables/views/procs/
│                            functions/triggers…), data operations (CRUD + SQL text), connections (redacted), EF config
├── integrations.json        integration points (cache, queue, stream/Kafka, pubsub, http, soap, grpc, email, storage,
│                            file, scheduler, realtime, config, identity, search, platform), config entries, endpoints
├── rules.json               business-rule candidates
├── flows.json               business flows (entry point → ordered steps with data/integrations/rules)
├── journeys.json            screens (fields/actions), navigation edges, user journeys
├── capabilities.json        flows grouped into modernization units, prioritised P1–P3
├── capabilities/<slug>.json one self-contained bundle per capability (load one at a time into an LLM)
├── enrichment.json          (optional, written by the agent) business-language overlay
├── ast/<project>/<file>.json  per-file outline (types, members, attributes, complexity, docs); full syntax with --ast full
└── graphs/                  projects.mmd, layers.mmd, entities.mmd, callgraph.dot, flows/FLOW-*.mmd
```

## Ids
| prefix | item | stable across runs? |
|---|---|---|
| `M:`/`T:`/`P:` | method/type ids = Roslyn documentation-comment ids | yes |
| `EP-` | entry point | yes if code unchanged (ordering is deterministic per run) |
| `FLOW-` | flow (one per entry point) | same as EP |
| `BR-` | business rule | same caveat |
| `INT-` | integration point | same caveat |
| `UJ-` | user journey | same caveat |
| `CAP-` | capability (ranked by score) | may shift when code changes — prefer `slug` |

Keep `enrichment.json` keyed by ids from the same run; regenerate it if you re-run `analyze` after code changes.

## Rule kinds
| kind | detected from |
|---|---|
| `validation` | `if (…) throw X("msg")`, `return BadRequest("msg")`, `ModelState.AddModelError`, `Result.Fail`, `MessageBox.Show` in a branch; `RAISERROR`/`THROW` in stored procedures |
| `guard` / `existence` | argument/null guards (trivial ones dropped unless `--include-trivial`), not-found branches |
| `authorization` | `[Authorize(Roles/Policy)]`, `PrincipalPermission`, `AddPolicy(...)`, unauthorized branches |
| `decision` | conditions comparing against business literals, enum members or constants |
| `decision-table` | `switch` statements / switch expressions (rows = cases) |
| `state-transition` | assignments of enum/literal values to `*Status`/`*State`/`*Stage` members (with guarding condition) |
| `calculation` | numeric expressions with significant literals/constants/`Math.Round` assigned or returned |
| `declarative-validation` | DataAnnotations attributes, Web Forms validator controls |
| `fluent-validation` | FluentValidation `RuleFor(...)` chains |
| `parameter` | business-named `const` values (rates, limits, thresholds, fees, days…) |
| `data-constraint` | EF `HasQueryFilter`, `HasCheckConstraint`, unique indexes; SQL `CHECK` constraints |

## Enrichment overlay (`enrichment.json`)
```json
{
  "capabilities": { "CAP-001": { "name": "…", "description": "…", "priority": "P1" } },
  "flows":        { "FLOW-0001": { "name": "…", "goal": "…", "actor": "…" } },
  "rules":        { "BR-0001": { "statement": "…", "title": "…", "confidence": "high", "questions": ["…"] },
                    "BR-0002": { "drop": true, "dropReason": "duplicate of BR-0001" } },
  "journeys":     { "UJ-0001": { "name": "…", "actor": "…" } }
}
```
`relearn speckit` and `relearn bmad` apply it automatically.

## Known limits of static extraction
- Reflection, `dynamic`, runtime-composed DI and plugin loading are not followed.
- Behaviour in configuration, reference-data tables, client-side JavaScript and external schedulers is invisible.
- Stored procedures are parsed for objects and `RAISERROR`/`THROW` rules only — their logic needs human/LLM review.
- Interface calls fan out to all source implementations unless DI registrations select one.
- Ad-hoc mode (no MSBuild) leaves third-party types unresolved; integration detection then relies on names.
