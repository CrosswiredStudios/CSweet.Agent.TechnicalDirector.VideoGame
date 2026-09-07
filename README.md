# Video Game Technical Director

Owns engine feasibility, runtime architecture, performance budgets, technical standards, and technical approvals.

## Contract

- Package ID: `com.csweet.video-game-technical-director`
- Version: `1.0.0`
- Provides: `work.execution.run.v1`
- Activation: manual
- Requested platform/provider capabilities: none
- Event subscriptions: none
- Network access: none

## Develop

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.TechnicalDirector.VideoGame -- --self-test
```

The tests run entirely in memory and require no C-Sweet instance or credentials.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the exact manifest, grants, activation mode, and source before approving installation.

Built with `CSweet.Agent.SDK` 3.28.0 and the bundled video-game extension source.


## Extension ownership and isolated builds

Game-specific payload helpers and decision logic live in the bundled `extensions/video-game` source snapshot under the publisher-owned `CrosswiredStudios.VideoGame` namespace. They are compiled into this agent, not published as C-Sweet platform contracts. The snapshot has versioned SHA-256 provenance and needs no sibling checkout or domain NuGet feed. C-Sweet handles generic coordination envelopes and profile metadata; agent permissions and existing wire type IDs remain unchanged.

## Scope-specific planning (2.3.1)

Planning coordination verifies every accepted package revision and digest, reads the actual game brief,
and uses the configured brokered model to propose a proportional backlog through packaging and QA.
This requests `platform.artifact-package.read.v1` in addition to document reads. Unknown roles/skills,
missing acceptance criteria, unresolved dependencies and cycles are rejected. Unresolved authority
questions are returned to the Producer rather than silently decided. No fixed studio roster is requested.

## Product repository ownership

Version 2.3.1 makes the Technical Director responsible for repository setup and branch/integration standards.
After verifying an accepted production planning package it requests a repository for that workstream,
using the business default template/provider and a stable product idempotency key. The host associates
the provisioning record with the product and grants its team repository access. Names use the workstream
ID so renamed products and repeated planning cycles cannot create duplicate repositories.

Reimport the agent and review the new `source-control.repository.provision.v2` organization grant and
`source-control.repository.team-options.v2` team grant. Business approval requirements still apply.
The AlwaysOn attention callback checks pending requests every five minutes without an LLM call.
Operating state preserves product/team ownership and approval or remediation status across restarts.
Planning continues while provisioning is pending; technical proposals report the actual setup status.
Ready means provisioning completed and the exact repository appears in the planning team's inventory.
Team changes require ownership reconciliation; arbitrary existing team repositories are never adopted.
This does not grant merge authorization or bypass the governed work execution/publishing system.
