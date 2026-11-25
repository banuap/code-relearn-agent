---
description: Generate Spec Kit feature specs for a modernization by reverse-engineering a legacy .NET solution with ReLearn (Roslyn).
---

## User Input

```text
$ARGUMENTS
```

You **MUST** consider the user input before proceeding (if not empty). Expected: `<legacy .sln path> [target profile] [--only CAP-001,…]`.

## Outline

1. **Locate the tool.** Find `relearn` (`relearn --help`, or `dotnet <path>/relearn.dll`). If missing, build it: `dotnet build <relearn-repo>/src/ReLearn.Analyzer -c Release`.
2. **Extract.** `relearn analyze <sln> --out .relearn`. Read `.relearn/summary.md`; if `loadMode` is `adhoc` or compile errors are reported, tell the user the fidelity is partial and suggest restoring the legacy solution.
3. **Enrich.** For each capability in `.relearn/capabilities.json` (priority order), read `.relearn/capabilities/<slug>.json` and the cited source, then write business-language statements into `.relearn/enrichment.json` (capability name/description, flow name/goal/actor, rule statement/drop/questions). Do not guess — use `questions`.
4. **Generate.** `relearn speckit --pack .relearn --target <profile> --out .` (adds `--only` if given). This writes:
   - `.specify/memory/constitution.md` (or `constitution.relearn.md` if a constitution already exists — offer to merge via `/speckit.constitution`)
   - `.specify/relearn/{target-profile,parity-matrix,feature-index}.md`
   - `specs/NNN-<capability>/{spec.md,data-model.md,research.md,legacy-evidence.md,contracts/legacy-interface.md,checklists/parity.md}`
5. **Check.** For each generated spec: count `[NEEDS CLARIFICATION` markers; confirm every FR has a legacy id in *Legacy Traceability*; list unassigned rows in the parity matrix.
6. **Report** the feature index and recommend, per feature in priority order: `/speckit.clarify` → `/speckit.plan` → `/speckit.tasks` → `/speckit.analyze` → `/speckit.implement`.

## Rules
- Specs describe WHAT/WHY; target technology belongs in `research.md` and `/speckit.plan`.
- Never copy secrets; connection strings are already redacted in the pack.
- Keep legacy ids (BR-, FLOW-, EP-, INT-, CAP-) in everything you write.
