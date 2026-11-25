---
name: relearn-extract
description: Run ReLearn's Roslyn static analysis on a legacy .NET solution, validate the extraction's fidelity, close gaps by reading code, and enrich business rules, flows, capabilities and journeys in business language (enrichment.json). Use before any modernization planning on a .NET brownfield.
---

# relearn-extract

## Inputs
- `solution` — path to the legacy `.sln` / `.csproj` / folder (ask if not given)
- config: `relearn_cli` (default `relearn`), `relearn_pack_dir` (default `.relearn`)

## Steps

### 1. Preflight
- `dotnet --list-sdks` must show 8.0.x or later. If the CLI is not built: `dotnet build <relearn-repo>/src/ReLearn.Analyzer -c Release`.
- For .NET Framework solutions, restore packages first so MSBuild can evaluate the projects.

### 2. Extract
```
{relearn_cli} analyze {solution} --out {relearn_pack_dir} --ast outline
```
Add `--include-tests` when tests exist (test names are good rule evidence).

### 3. Validate
Read `summary.md` + `manifest.json`:
- `loadMode` = `msbuild` and few compile errors → full fidelity. Otherwise record "partial fidelity" and why.
- Spot-check 3 entry points, 3 rules, 1 flow against the cited `file:line`.
- List blind spots: reflection, config-driven behaviour, stored-procedure logic, reference-data rules, client-side JS, external schedulers.

### 4. Close gaps
Read source for constructs the tool could not classify (custom base classes, in-house ORMs or buses). Record findings in `{relearn_pack_dir}/gaps.md` with evidence.

### 5. Enrich (one capability at a time, priority order)
Load `capabilities/<slug>.json`, read cited code, then add to `{relearn_pack_dir}/enrichment.json`:
- `capabilities.<CAP-id>`: `name`, `description`, optional `priority`
- `flows.<FLOW-id>`: `name`, `goal`, `actor`
- `rules.<BR-id>`: `statement` (business sentence with exact numbers/messages), `confidence`, `questions[]`, or `drop: true` + `dropReason`
- `journeys.<UJ-id>`: `name`, `actor`

### 6. Report
Counts, fidelity, gaps, number of open questions, and suggest **relearn-handoff**.
