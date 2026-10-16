# Video Game Technical Director

Owns engine feasibility, runtime architecture, performance budgets, technical standards, and technical approvals.

Technical planning publishes a compact milestone (epic), feature/content (story), and engineer/QA task hierarchy with testable acceptance criteria.

`GenerateCheckpointedPlanningAsync` first generates and validates the complete compact scope index,
then generates detailed implementation briefs for three tasks at a time. Every page requests a JSON
schema and permits at most three attempts, with completion reason and parse/validation errors retained
in a blocked diagnostic. A normal provider stop is not proof of complete JSON. No partial response
becomes a proposal or tickets. The assembled result still passes hierarchy, dependency, role and
engineering-brief checks.

The existing operating-state grant saves the frozen index and each brief page separately within
the platform's 64 KiB record limit. Exact input digests include accepted documents, canonical work,
repository facts and authenticated Producer directions. Retries reuse only validated pages for those
same inputs; concurrent writes cannot replace the accepted index or a saved brief. No grants changed.

`PlanningPagesTests` includes the three malformed/incomplete Pulse Break response shapes. To replay
the full retained responses, set `CSWEET_PLANNING_CAPTURE_ROOT` to the authorized local capture folder;
raw business documents and captured responses are not bundled in the package.

Technical review uses the Producer's canonical workflow assignment. Task integration is platform-owned.
The original approved plan may delegate only implementation; it does not need to be
rewritten to add reviewers. Role, selection and artifact-package checks still apply.
After upgrading an already blocked review, request a ticket retry to run a new attempt.

`GeneratePlanningAsync` accepts a complete JSON object wrapped in a plain or JSON Markdown fence. `UnwrapPlanningJson` removes only that enclosing fence; prose, multiple objects and incomplete JSON still require bounded correction. Hierarchy and dependency validation run after normalization.

## Contract

- Package ID: `com.csweet.video-game-technical-director`
- Version: `4.0.4`

Victor requests `compute.local-test-links.v1` at hiring or upgrade review so he can share browser
test links without a separate approval for each instance. Core limits this permission to local
port 8080 and the authorized instance lifetime. Owners can revoke access from Compute; the
agent cannot restore an explicit revocation. This permission does not include public hosting or outbound internet.
- Provides: `work.execution.run`
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
256,000) and **Maximum output tokens** (`maxOutputTokens`, default 32,000) set upper limits,
including model reasoning. `SpecialistAgent.ResponseOptions` also caps production planning at
32,000 tokens, documents/reviews at 8,192, and developer support/build selection at 4,096.
Lower configured limits remain effective; existing installations set to 128,000 receive these
task ceilings after upgrade. Ticket replies use a separate 2,048-token budget and default to
120 words. These settings do not enlarge a model's actual context window or override the
provider's execution ceiling. The SDK's separate project-planning and aggregate-review protocols
retain their existing budgets.

Prompts lead with decisions and next actions, remove repeated context, and keep distinct
acceptance criteria, scope and evidence requirements intact. Structured output is validated and
corrected through the existing bounded retries; output is never silently cut to fit a word limit.

## Install

Keep `csweet-plugin.json` at the repository root. Import a reviewed GitHub commit in C-Sweet, or
clone this repository as an immediate child of C-Sweet's configured local agent catalog. Review
the exact manifest, grants, activation mode, and source before approving installation.

Built with `CSweet.Agent.SDK` 3.65.0, `CSweet.WorkManagement.Contracts` 3.24.0, and the bundled video-game extension source.


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
The platform-scheduled OnDemand attention callback checks pending requests every five minutes without an LLM call.
Operating state preserves product/team ownership and approval or remediation status across restarts.
Planning continues while provisioning is pending; technical proposals report the actual setup status.
Ready means provisioning completed and the exact repository appears in the planning team's inventory.
Team changes require ownership reconciliation; arbitrary existing team repositories are never adopted.
This does not grant merge authorization or bypass the governed work execution/publishing system.

## Provider queue handling

Uses SDK 3.65.0 for acknowledged LLM waiting, conversation activity, and host-authoritative deadline updates. Deploy the matching C-Sweet AgentHost and reimport this package to enable the private polling protocol.

## Engineering ticket delivery (2.6.0)

Repository attention reviews now attach ready repository and default-branch configuration to staffed, provenance-bound game-engineer tickets on the same team and workstream. Existing planning, dependencies, accountability and stage assignments are preserved; already finalized and completed tickets are skipped. Board-read and delivery-finalization permissions are requested at board scope. Host authorization and planning approval checks still apply. This does not configure downstream code review or QA stages, which remain separate required work.

### Technical code review (2.6.0)
Technical review reads the current typed work item, verifies the independent reviewer assignment, and uses the shared SDK's exact source/target reference and bounded diff pages. It produces an evidence-backed approval or actionable rejection. Platform-owned integration and independent QA retain their existing gates. The agent requests only the work-item Git review capability; it no longer runs a full-patch fallback or standalone merge-decision stage. Source edits, direct Git operations and merge execution remain outside this agent.

### Engineering stage finalization (2.6.0)
Repository reconciliation adds independent technical-review and quality responsibilities to engineering plans. The shared SDK reviewer reads the current typed item and reviews an exact source/target through bounded diff pages. Task integration is platform-owned; standalone merge-decision execution and the old full-patch review fallback are removed. Workstream read and planning-revision capabilities still require explicit grants.

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

## Lightweight producer coordination

SDK 3.65.0 adds `ProjectDeliveryPlanning` and `ProjectDeliveryReview`. An approved project-bound
`project.delivery.plan-request.v1` receives a validated architecture and ordered ticket/sprint
proposal. For the lightweight manager-brief execution policy, the assigned technical quality stage
reviews the exact patch and published developer tests. It does not claim it reran the tests. Rework
findings are persisted on the ticket, and the Producer retains delivery acceptance and merge authority.

The Technical Director produces plans and reviews; coding, prototypes, package locks, measured execution, and commits belong to engineering and independent QA. Replanning includes full canonical requirements and criteria. Previously finalized tickets reassigned to engineering still receive technical review and independent QA requirements before delivery can execute.

Version 2.11.6 accepts the Producer 2.10.4 structured `roleRepair` extension only from the authenticated counterpart for the exact accepted planning cycle. `ReadRoleRepairInstruction` validates its schema and `BuildRoleRepairPlanning` checks the source planning hash against the current canonical board. It deterministically preserves requirements, acceptance criteria, constraints, unrelated roles and ticket identities. The retained Technical Director task requires a real plan; a new engineering task owns every original delivery criterion and independent QA verifies all of them. Unfinished downstream work waits for validation. This scope-preserving transformation does not ask a model to transcribe the backlog or claim any delivery is complete. Unknown versions, stale scope, missing canonical dependencies and output-key collisions block without changing the board. Existing normal planning is unchanged; no new capability grants are required.
## Technical-review response formatting (2.11.7)

`ParseTechnicalDecision` accepts literal escaped whitespace at the end of an otherwise valid JSON response, as emitted by some providers. It preserves all findings and validates the exact candidate SHA and decision consistency before recording a result. It does not extract JSON from prose or accept multiple decisions. Invalid JSON has a specific blocker message. Rejected candidates return through the configured engineering rework transition; technical review does not implement changes or authorize merge.

## Bounded review formatting repair (2.11.8)

`GenerateTechnicalDecisionAsync` permits one formatting retry for invalid JSON. It retains the original candidate context and validates the corrected decision. A complete initial object followed by malformed trailing content supplies an immutable decision/summary/findings baseline for repair; a correction cannot switch a rejection into approval or drop findings. Wrong SHA and inconsistent decisions never trigger repair. Two malformed responses block with a specific error.

## Complete planning handoffs (2.11.9)

`PlanningCoordinationMessage` reads `coordinationContext` only from the matching planning-cycle artifact on the authenticated Producer's turn. Producer 2.11.2 uses this existing artifact field for complete handoffs that exceed the chat-message limit. Invalid context fails closed; another participant, project, cycle or accepted-package revision cannot supply it. Explicit owner-authorized replacements take precedence over conflicting older planning while unrelated scope, completed work, QA and merge controls remain intact.

## Technical lead for blocked developers (2.12.0)

When the assigned developer is blocked by a technical failure on a work item, they open a work-item-scoped support coordination (`software-development.support-request.v1`) with the team's technical lead. On game teams that lead is the Technical Director. I answer with `software-architecture.guidance.v1`: a diagnosis, ordered next steps, invariants, relevant design decisions, explicit verification, and remaining risks. The developer then retries the exact blocked stage.

I still plan and review only: I never write code, run measurements, or claim results. If the fix needs a scope, criteria, environment or tooling decision, the guidance sets `requiresArchitectureApproval` and names what the Producer or owner must decide, so the developer does not retry on an unapproved change. This uses the existing coordination read and respond grants, with updated purpose text.

## Ticket discussion (2.14.0)

Answers directed ticket questions using current ticket context and recorded review evidence. Responses clarify findings without changing review decisions, acceptance criteria or validation requirements.

The agent subscribes to `com.csweet.work.item.discussion.changed.v1` and uses grant-governed item,
comment, board and orchestration reads. Ask with `@Full Name: your question` on a team ticket.
Replies use `discussion.reply` and an exact comment/revision correlation; duplicates and stale
requests are ignored. Informational notifications do not trigger automatic conversation loops.
Bounded attention recovery scans the current assigned project for missed requests. The host retains
undelivered notifications while offline; install the matching C-Sweet host update and approve new
manifest permissions. Comments provide context, never execution authority or review approval.

## Hierarchical delivery

The V2 assignment identifies Task, Story, Epic or Release scope. Tasks require an authorized sprint; aggregate reviews require an active delivery plan and do not invent sprint identities. Use the exact assigned branch, commit or artifact revision. Every task requires independent QA, including documents. Managers accept aggregate candidates using current evidence; task completion does not promote to main or authorize deployment.

## Planning validation diagnostics (2.16.0)

PlanningValidation.PlanValidationIssues supplies item-specific correction and blocked-result errors. Self-parent stories, missing references, unsupported skills and parent/dependency cycles identify the offending keys. The same validator controls acceptance; the generator never accepts an invalid proposal after exhausting correction. Install this version before retrying a planning collaboration rejected under a previous version.

PlanningConstructionGuidance adds an ordered checklist and a contract-valid worked example to every planning attempt. The example demonstrates milestone/story/task containment, implementation then independent QA, and exact role/skill values without a default research prerequisite. Examples supply structure only; accepted scope, canonical keys and owner-authorized decisions remain authoritative.

Performance targets remain implementation constraints. Profiling follows runnable increments, with representative-device validation and optimization in later hardening/QA. A blocking performance spike requires a specific architectural decision, supporting risk evidence, costly consequences, narrowly affected dependencies, a bounded experiment and stopping rule, and available measurement resources. Missing hardware is an environment dependency requiring a decision, not grounds for repeated code rework. Existing accepted criteria change only through an authorized amendment; deferred validation is never passed validation.

## Certified release build-readiness

SDK 3.65.0 allows the Producer to assign build-readiness to an updated Technical Director or Developer when no Build Release Engineer is available. The assigned agent must advertise `work.execution.build-readiness.v1` and V2 execution. `DeliveryBuildReadiness.ExecuteAsync` requests the configured certified provider build for every exact release repository candidate, returns durable pending build identities, and verifies successful output provenance. It grants no deployment, publication, or independent product approval authority. Existing project, repository and build grants remain required.

## Project compute test delivery

- Discover approved project compute features, cached images, local-link reachability and certified build alternatives.
- Own team test compute and recover it through durable current-state reads.
- Deliver exact-source build/HTTP evidence and a provider-verified local test URL to the project manager and CEO; retain independent QA and technical acceptance.
- Request scoped compute, organization-directory and communication capabilities. Installation approval does not grant network access.

Current compute review links are local to the provider machine. Public hosting remains a separate capability. Pending setup and local-link approval preserve durable progress; unknown command outcomes are not automatically repeated.

## Delegation and tracking

SDK 3.65.0 dispatches authenticated delegation before specialist conversation fallback. The manifest requests delegation, work tracking, directory and conversation reads, personal commitments and model interpretation. These declarations require reviewed grants; existing resource permissions and approval rules still apply. The shared SDK owns coordinator reporting and wake recovery.

## Execution contract (3.1.0)

Execution uses one `work.execution.run` capability and `WorkExecutionAssignment` from WorkManagement.Contracts 4.0.0 through SDK 4.2.1. Task and aggregate scopes retain their distinct authority checks within that envelope. Upgrade the platform and executing agents together; no legacy execution adapter remains.

Uses SDK 4.2.1 bounded exact-publication task technical review and repository source hygiene. Install the matching platform broker and trusted Git publisher before upgrading. Existing generated/dependency files require explicit source cleanup; build recipes generate deployable artifacts from pinned dependencies.

Activation defaults to OnDemand. Durable messages, assignments, scheduled duties and attention reviews wake the agent; idle runtimes release their Office slot. Attention wakes require the platform scheduler that supports sleeping OnDemand installations. Existing installations retain their selected activation mode until their schedule is updated.

## Small tasks and comment revisions

Require actionable Outcome, Context, Pseudocode, Assumptions and Verification sections in new engineering briefs, with bounded correction and validation. Split independent deliverables even when sequential. Answer revision comments with explicit correction, clarification or replacement recommendations. Comments can supersede specific implementation guidance, but cannot waive accepted outcomes, criteria, reviews or grants. Substantial changes require linked replacement tickets through governed replanning; a replacement recommendation is not automatic cancellation or execution authority. The original stays blocked until that planning is applied.
