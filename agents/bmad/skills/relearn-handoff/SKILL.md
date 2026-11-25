---
name: relearn-handoff
description: Turn a ReLearn knowledge pack into BMAD brownfield project knowledge (docs/) and planning seeds — modernization brief for the PM, target-architecture guidance for the Architect, epics seed, project-context for dev agents — and optionally GitHub Spec Kit feature specs, for a target such as Java/Spring or Node/Electron.
---

# relearn-handoff

## Inputs
- `target` — `java-spring`, `node-electron`, or a custom profile JSON (default: config `relearn_target`)
- `also_speckit` — yes/no (default yes if a `.specify/` folder exists)

## Steps
1. Ensure `{relearn_pack_dir}/manifest.json` exists; otherwise run **relearn-extract** first.
2. Generate BMAD knowledge:
   ```
   {relearn_cli} bmad --pack {relearn_pack_dir} --target {target} --out . --docs docs
   ```
   Produces `docs/index.md`, `project-overview.md`, `architecture-legacy.md`, `source-tree-analysis.md`, `component-inventory.md`, `data-models.md`, `api-contracts.md`, `integration-architecture.md`, `business-rules.md`, `business-flows.md`, `user-journeys.md`, `modernization-brief.md`, `target-architecture-guidance.md`, `epics-seed.md`, `project-context.md`.
3. If `also_speckit`:
   ```
   {relearn_cli} speckit --pack {relearn_pack_dir} --target {target} --out .
   ```
4. Review `docs/modernization-brief.md` with the user; resolve or list `[NEEDS CLARIFICATION` items.
5. Hand off:
   - **Analyst / PM (John)** → `create-prd` with `docs/modernization-brief.md` + `docs/business-rules.md` as inputs. Scope = capabilities; MVP = P1.
   - **Architect (Winston)** → `create-architecture` with `docs/architecture-legacy.md`, `docs/integration-architecture.md`, `docs/data-models.md`, `docs/target-architecture-guidance.md`.
   - **PM** → `create-epics-and-stories` seeded from `docs/epics-seed.md` (one epic per capability, one story per flow, rule ids as acceptance criteria).
   - **Dev / QA** → must load `docs/project-context.md`; every story cites BR-/FLOW- ids; parity tests first.
6. Summarize what was generated and the recommended next BMAD command.
