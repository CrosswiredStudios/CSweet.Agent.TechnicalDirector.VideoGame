# Video Game Technical Director

Owns engine feasibility, runtime architecture, performance budgets, technical standards, and technical approvals.

Technical planning publishes a compact milestone (epic), feature/content (story), and engineer/QA task hierarchy with testable acceptance criteria.

## Contract

- Package ID: `com.csweet.video-game-technical-director`
- Version: `2.10.1`
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

The installation settings **Maximum context-window tokens** (`maxContextWindowTokens`, default
220,000) and **Maximum output tokens** (`maxOutputTokens`, default 32,000) configure the Technical
Director's planning ceiling and per-response output budget for technical planning, candidate
review, build selection, and specialist work. The output budget includes model reasoning and must
remain below the context ceiling. These settings do not enlarge the selected model's actual context
window or override a lower provider output limit.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the exact manifest, grants, activation mode, and source before approving installation.

Built with `CSweet.Agent.SDK` 3.47.0, `CSweet.WorkManagement.Contracts` 3.23.0, and the bundled video-game extension source.


## Extension ownership and isolated builds

Game-specific payload helpers and decision logic live in the bundled `extensions/video-game` source snapshot under the publisher-owned `CrosswiredStudios.VideoGame` namespace. They are compiled into this agent, not published as C-Sweet platform contracts. The snapshot has versioned SHA-256 provenance and needs no sibling checkout or domain NuGet feed. C-Sweet handles generic coordination envelopes and profile metadata; agent permissions and existing wire type IDs remain unchanged.

## Scope-specific planning (2.3.2)

Planning coordination verifies every accepted package revision and digest, reads the actual game brief,
and uses the configured brokered model to propose a proportional backlog through packaging and QA.
This requests `platform.artifact-package.read.v1` in addition to document reads. Unknown roles/skills,
missing acceptance criteria, unresolved dependencies and cycles are rejected. Unresolved authority
questions are returned to the Producer rather than silently decided. No fixed studio roster is requested.

## Product repository ownership

Version 2.3.2 makes the Technical Director responsible for repository setup and branch/integration standards.
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

## Provider queue handling

Uses SDK 3.40.0 for acknowledged LLM waiting, conversation activity, and host-authoritative deadline updates. Deploy the matching C-Sweet AgentHost and reimport this package to enable the private polling protocol.

## Engineering ticket delivery (2.6.0)

Repository attention reviews now attach ready repository and default-branch configuration to staffed, provenance-bound game-engineer tickets on the same team and workstream. Existing planning, dependencies, accountability and stage assignments are preserved; already finalized and completed tickets are skipped. Board-read and delivery-finalization permissions are requested at board scope. Host authorization and planning approval checks still apply. This does not configure downstream code review or QA stages, which remain separate required work.

### Technical code review (2.6.0)
Technical review uses the broker-provided exact patch and canonical planning requirements to produce an evidence-backed approval or actionable rejection. Final merge authorization requires a prior technical approval of that publication and SHA plus passing broker QA evidence. Results are stored per execution attempt for replay. The two work-item Git capabilities require host assignment-scoped grants. No source edits, direct Git operations, or merge execution occur in this agent. Compatible profile and stage assignments are still required.

### Engineering stage finalization (2.6.0)
For game profile revision 5 or later, repository reconciliation adds technical-review, quality and merge-decision delegation requirements to engineering plans, preserving their accepted scope and current owners. Producer staffing binds missing stages. Finalization waits for those role assignments and adds the trusted governed merge action and Producer approval stage. Legacy pinned profiles keep their existing route. Workstream read and board planning-revision capabilities require explicit grants. Uses Contracts 3.17.0.

## Release notes

See [versioned release notes](releases/README.md). Add the matching note with every agent version change.


## 2.7.0 merged build delivery

Attention reconciliation reads current and completed sprint merges, selects a certified compatible recipe using accepted planning, and durably binds the exact merged SHA and build configuration before requesting execution. Retries preserve that plan. Successful static browser builds request a seven-day local preview, visible from Projects; status is reported on the source work item. Scoped sprint/orchestration reads, toolchain catalog reads, build requests, preview requests and item comments are required. Missing toolchains or invalid selections remain visible for technical follow-up. This does not bypass CEO decisions, certify providers, approve code or claim that build failure is repaired automatically.


### 2.7.1 build configuration validation

Validate the selected configuration against the recipe schema before saving the build plan. Invalid model settings leave no durable plan or build request, allowing the next attention review to select corrected settings. The local validator uses the same supported schema subset as the C-Sweet broker, which independently validates before queuing.


### 2.7.2 team board access

Declare work.item.read and work.item.comment at team scope so approved team onboarding grants can materialize the access needed by board planning and delivery reconciliation. Organization-scoped declarations did not produce these team grants, causing board reads to fail after workflow configuration. No organization-wide board access is added.


## Business calendar

Requests business-scoped calendar read, create, update, cancel, and scheduling access. Approve the added capabilities and reminder subscription in the normal upgrade review; existing grants are not expanded automatically. Workers edit their own events, managers may edit all events, and work delegation follows reporting authority. Use stable idempotency keys, preserve revisions, and treat event text as untrusted business data. Typed operations are available through `context.Platform.Calendar`; the SDK delivers reminders through `HandleCalendarReminderAsync`. Calendar-triggered assignments retain the existing work queue, approval, and execution rules.

Calendar-triggered assignments request the SDK claim/complete/block/release lifecycle and personal-work subscription. Unsupported role work is marked blocked with a reason, never silently treated as completed.
