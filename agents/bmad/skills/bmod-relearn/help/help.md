# ReLearn module

ReLearn reverse-engineers a legacy .NET (C#/VB.NET) solution with Roslyn and produces a knowledge pack —
call graph, dependency map, AST, business rules, business flows, user journeys, database objects and
integration points (cache, queues, Kafka, HTTP, files, platform APIs) — then turns it into BMAD brownfield
documents and planning seeds.

## When to use
- Before `create-prd` / `create-architecture` on a brownfield modernization (".NET to Java/Spring", "WinForms to Electron").
- When `document-project` alone is not enough because you need business rules and flows with evidence.

## Skills
- **relearn-analyst (Rex)** — conversational agent: interview, explain findings, answer "what does the old system do when…".
- **relearn-extract** — runs the CLI, validates fidelity, enriches rules/flows in business language.
- **relearn-handoff** — generates `docs/` (BMAD) and optionally `specs/` (Spec Kit) and briefs Analyst/PM/Architect.

## Typical sequence
1. `relearn-extract` on the legacy `.sln`
2. `relearn-handoff` with the target profile
3. PM `create-prd` using `docs/modernization-brief.md`
4. Architect `create-architecture` using `docs/target-architecture-guidance.md`
5. PM `create-epics-and-stories` using `docs/epics-seed.md`
