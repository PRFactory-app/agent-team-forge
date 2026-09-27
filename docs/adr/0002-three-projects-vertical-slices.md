# 0002. Three projects, organized by feature

Status: accepted

## Context

Early planning proposed many projects and Clean Architecture layering. For a
small local tool this adds ports, mappers and assemblies without a real
substitution need, and scatters every change across the solution.

## Decision

- Three production projects: **AgentTeamForge.Host → AgentTeamForge.Business →
  AgentTeamForge.DAL**, plus one test project.
- Host: CLI, setup, MCP bridge, IPC, daemon lifecycle, web console and
  composition. Business: feature logic and backend/terminal integration. DAL:
  SQL, migrations and persistence.
- Business calls DAL directly. There are no repository ports, no
  handler/validator/mapper chain per operation, and no global `Interfaces/` or
  `Dtos/` folders. Add an interface only for a real substitution (for example a
  fake backend).
- Organize by feature (vertical slice): a change implements one observable
  behavior through the layers it needs, with its tests.
- A fourth production project needs a demonstrated need and an explicit
  decision.

## Consequences

- Features are found by name across the three projects
  (`Features/Jobs`, `Features/Wake`, `Features/External`, …).
- Bridge and client modes run the same binary but never open the database;
  only the daemon does.
