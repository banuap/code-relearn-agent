---
description: Reverse-engineer a legacy .NET solution into a modernization knowledge pack + Spec Kit/BMAD artifacts
argument-hint: <path-to-sln> [java-spring|node-electron|profile.json] [out-dir]
---

Use the **dotnet-relearn** agent to process this request: $ARGUMENTS

Parse the arguments as: legacy solution path, target profile (default: `node-electron` if the solution contains WinForms/WPF projects, otherwise `java-spring`), output directory (default `./modernization`).

Run all seven phases of the agent's procedure (extract → validate → close gaps → enrich → generate → verify → hand off). Produce both Spec Kit and BMAD artifacts unless the user asked for only one. Finish with the hand-off report.
