# ReLearn — legacy .NET → modernization knowledge agent

ReLearn reverse-engineers a legacy .NET solution (C# and VB.NET; .NET Framework or modern .NET) with **Roslyn** and produces a language-neutral **knowledge pack**. A modernization agent can use the pack to rebuild the system on **Java/Spring**, **Node/Electron**, or any target you describe in a profile. The pack feeds directly into **GitHub Spec Kit** (feature specs) and the **BMAD Method** (brownfield docs and planning seeds).

```
 legacy .sln ──► relearn analyze (Roslyn) ──► knowledge pack (.relearn/)
                                                    │  + agent enrichment (business language)
                                                    ├──► relearn speckit ──► .specify/ + specs/NNN-capability/…  ──► /speckit.plan → tasks → implement
                                                    └──► relearn bmad    ──► docs/ (brief, architecture guidance, epics seed, project-context) ──► PRD → architecture → stories
```

## What it extracts

| # | Artifact | How |
|---|---|---|
| 1 | **Call graph** | IOperation walk of every method (C#/VB): direct, virtual and interface calls resolved to implementations (DI-aware), constructors, delegates, events, lambdas, MediatR/MassTransit dispatch, plus external API calls |
| 2 | **Dependency map** | projects, NuGet/packages.config, assembly refs, type → type edges (inherits/implements/injects/calls/creates), namespace and layer (stereotype) edges, DI registrations (MS DI, Autofac, Unity, Ninject, Windsor, SimpleInjector) |
| 3 | **AST** | per-file outline (types, members, signatures, attributes, cyclomatic complexity, XML docs); full syntax trees with `--ast full` |
| 4 | **Business rules** | guards and validations with messages, authorization, decisions on business literals, switch decision tables, state transitions, calculations (decimal/rounding), DataAnnotations, FluentValidation, business constants, EF constraints, SQL `CHECK`, `RAISERROR` in procedures |
| 5 | **Business flows** | entry point → ordered call steps, with the data, integrations and rules at each step; Mermaid sequence diagrams |
| 6 | **User journeys** | screens (MVC views, Razor Pages, Blazor, Web Forms, WinForms, WPF/XAML) with fields and actions, a navigation graph (RedirectToAction, links and forms, `new Form().Show()`, `Navigate`), and journeys built from them; REST APIs without a UI get resource-lifecycle journeys |
| 7 | **Database objects** | DbContexts/entities (EF Core, EF6, LINQ to SQL), tables/views/procs/functions/triggers from `.sql`, EDMX and migrations, SQL in code (ADO.NET, Dapper, NHibernate, EF raw SQL) with CRUD classification, redacted connection strings |
| 8 | **Integrations** | caching (MemoryCache, IDistributedCache, Redis, System.Runtime.Caching, HttpRuntime.Cache, session), queues (RabbitMQ, Azure Service Bus, MSMQ, SQS, MassTransit, NServiceBus, IBM MQ), streams (**Kafka**, Event Hubs, Kinesis), HTTP/SOAP/WCF/gRPC/SignalR clients, email, blob storage and files, schedulers (Hangfire, Quartz, timers), configuration keys, identity, search, Windows-only APIs (registry, COM, WMI, printing, Office interop, Crystal/SSRS) |

Flows are grouped into **capabilities**, which are the modernization units. Each one becomes one Spec Kit feature or one BMAD epic, and each is prioritized P1–P3.

## Quick start (Windows, .NET 8 SDK)

```powershell
cd code-relearn-agent
dotnet build src\ReLearn.Analyzer -c Release
$relearn = "dotnet src\ReLearn.Analyzer\bin\Release\net8.0\relearn.dll"

# smoke test on the bundled sample
dotnet restore samples\LegacyOrders
iex "$relearn analyze samples\LegacyOrders\LegacyOrders.csproj --out out\.relearn"
iex "$relearn speckit --pack out\.relearn --target java-spring --out out"
iex "$relearn bmad    --pack out\.relearn --target java-spring --out out"

# your system
iex "$relearn all C:\src\Legacy\Legacy.sln --target java-spring --out C:\src\modernization"
```

Install the agents into the repository where the new system will live:

```powershell
.\scripts\install.ps1 -Repo C:\src\modernization -Tools claude,copilot   # add -GlobalTool to get a global `relearn` command
```

This installs:
- **Claude Code**: `.claude/agents/dotnet-relearn.md`, `.claude/agents/modernizer.md`, `/relearn` and `/speckit.relearn` commands, and the ReLearn skills.
- **GitHub Copilot**: `.github/agents/*.agent.md`, including `speckit.relearn`.
- **BMAD**: the `agents/bmad/skills/` module (record `bmod-relearn` and the skills `relearn-analyst` (Rex), `relearn-extract` and `relearn-handoff`). Add it with your BMAD installer, then run `bmad setup relearn`.

## Running the agent from Claude Code

**1. Build the CLI** (once):
```powershell
cd <path>\code-relearn-agent
dotnet build src\ReLearn.Analyzer -c Release
```

**2. Install the agents into the repo you will work from** (for example, the new system's repo):
```powershell
.\scripts\install.ps1 -Repo C:\src\modernization -Tools claude
```
This copies the files to where Claude Code looks for them and fills in the path to the built CLI:
```
C:\src\modernization\.claude\
├── agents\dotnet-relearn.md      ← the ReLearn agent
├── agents\modernizer.md          ← implements the new system from the specs
├── commands\relearn.md           ← /relearn
├── commands\speckit.relearn.md   ← /speckit.relearn
└── skills\relearn-*\SKILL.md
```
If PowerShell blocks the script, run `Set-ExecutionPolicy -Scope Process Bypass` first. If you copy the files from `agents\claude\` by hand, replace `$RELEARN` in them with `dotnet <path>\relearn.dll`.

**3. Start Claude Code in that repo and call the agent:**
```powershell
cd C:\src\modernization
claude
```
Then, inside Claude Code, use any of these:
- `/relearn C:\src\Legacy\Legacy.sln java-spring .`
- `Use the dotnet-relearn agent to analyze C:\src\Legacy\Legacy.sln for a java-spring target`
- `/speckit.relearn C:\src\Legacy\Legacy.sln java-spring` (the Spec Kit route)

Type `/agents` to confirm that `dotnet-relearn` and `modernizer` are listed.

The agent runs `relearn analyze`, checks how reliable the extraction was, rewrites rules and flows in business language into `.relearn\enrichment.json`, and then generates `specs\` (Spec Kit) and `docs\` (BMAD) in the repo.

**Notes**
- **Permissions:** Claude Code asks before running `dotnet` commands. Approve them, or allow `Bash(dotnet:*)` in `.claude\settings.json`.
- **Legacy code outside the repo:** run `/add-dir C:\src\Legacy` so the agent can open the source files each rule cites.
- **Try the sample first:** `/relearn <path>\code-relearn-agent\samples\LegacyOrders\LegacyOrders.csproj java-spring .` (run `dotnet restore` on the sample first).
- **Next step:** once the specs exist, ask Claude Code to `Use the modernizer agent to implement specs/001-...`.

## Spec Kit integration
`relearn speckit` writes:
- `.specify/memory/constitution.md`: modernization principles (parity, traceability, contract compatibility, data fidelity, characterization tests first, strangler-fig delivery) plus the target stack. If you already have a customized constitution, the file is written as `constitution.relearn.md` instead.
- `.specify/relearn/target-profile.md`, `parity-matrix.md` and `feature-index.md`.
- `specs/NNN-<capability>/` containing `spec.md` (Spec Kit template: user stories, Given/When/Then from rules, FRs with legacy ids, key entities, success criteria, traceability table), `data-model.md`, `contracts/legacy-interface.md`, `research.md` (Decision/Rationale/Alternatives from the target mappings), `checklists/parity.md` and `legacy-evidence.md`.

Then, for each feature: `/speckit.clarify` → `/speckit.plan` → `/speckit.tasks` → `/speckit.analyze` → `/speckit.implement`.

## BMAD integration
`relearn bmad` writes `docs/` (BMAD's `project_knowledge`). It contains the as-is set (`project-overview`, `architecture-legacy`, `source-tree-analysis`, `component-inventory`, `data-models`, `api-contracts`, `integration-architecture`, `business-rules`, `business-flows`, `user-journeys`) and the to-be seeds:
- `modernization-brief.md` goes to PM `create-prd`.
- `target-architecture-guidance.md` goes to Architect `create-architecture`.
- `epics-seed.md` goes to `create-epics-and-stories`.
- `project-context.md` is for the dev agents.

## Targets
`targets/java-spring.json` and `targets/node-electron.json` map every legacy construct ReLearn can detect to an idiomatic target equivalent, with a rationale and alternatives. Only mappings for constructs actually found in your code are emitted. To add a target (for example .NET 8 + Blazor, Go, or Python/FastAPI), copy a profile and pass `--target path\to\profile.json`.

## Repository layout
```
src/ReLearn.Analyzer/      the CLI (Roslyn 4.11, MSBuildWorkspace with ad-hoc fallback)
  Loading/                 solution/project loading, csproj/packages.config reading
  Analysis/                type catalog, IOperation walker, classifiers, artifact scanner, flows/journeys/capabilities
  Output/                  JSON pack writer, AST writer, Mermaid/DOT
  Generators/              Spec Kit + BMAD generators, target profiles, enrichment overlay
targets/                   target profiles (java-spring, node-electron)
agents/claude/             Claude Code agents + commands (also installed as Copilot agents)
agents/speckit/            /speckit.relearn command
agents/bmad/skills/        BMAD module (bmod-relearn + skills)
samples/LegacyOrders/      tiny legacy app to smoke-test extraction
docs/KNOWLEDGE-PACK.md     pack format and rule kinds
scripts/                   install.ps1 / install.sh
```

## Notes
- For .NET Framework solutions, restore the solution and install the Visual Studio Build Tools or targeting packs so MSBuild can evaluate the projects. Otherwise ReLearn falls back to `--adhoc` mode, which gives partial semantics.
- Secrets are redacted. Treat the pack as internal documentation of your system.
- See `docs/KNOWLEDGE-PACK.md` for the limits of static extraction. The agent's validation and enrichment steps exist to cover them.
