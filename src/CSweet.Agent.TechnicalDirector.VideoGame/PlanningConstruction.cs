using System.Text.Json;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static string PlanningConstructionGuidance() => """
        Construct the proposal in this order before returning JSON:
        1. Map every accepted deliverable and acceptance criterion to a story and executable tasks.
           Use existing canonical proposal keys for retained scope. Do not reduce scope to fit a preferred item count.
        2. Define milestones first, with parentProposalKey null. Define feature/content stories next,
           with parentProposalKey equal to an existing milestone key. Define tasks/spikes last,
           with parentProposalKey equal to an existing feature/content key. A parent is a container,
           not a previous task, and never the item's own key.
        3. Assign one exact role from Roles to each item. Coding, scaffolding, packaging and measured
           prototypes belong to game-engineer; independent validation belongs to game-quality-assurance.
           Keep Technical Director work limited to plans, decisions and reviews. Assign specialist
           asset production only when the accepted scope needs it; never erase an original-asset requirement.
        4. Copy specialization strings exactly from Skills. Do not invent synonyms such as game-audio
           or sound-design; use audio-design when that is the intended specialization. For engineering
           and QA use requiredSpecializationKeys [] and put domain skills in preferredSpecializationKeys.
        5. Add only dependencies on required results, not every related item. In a new proposal keep
           milestone/story dependencyProposalKeys [] and sequence executable leaves. A QA leaf depends
           on the implementation it validates; downstream consumers depend on required implementation
           and validation results. Preserve legitimate canonical dependencies when replanning.
           Never make a container wait on its own descendants. Parent links and dependency links both
           participate in cycle detection. Check the combined graph, including cross-story dependencies.
        6. Check each leaf is a bounded, independently testable increment with an observable output
           and objective acceptance criteria. Include repository setup, research needed to resolve technical
           uncertainty, implementation, independent QA and packaging when required by the accepted scope.
           Pending provisioning/hiring is a readiness constraint, not a reason to omit the plan or claim work done.
        7. Verify every key is unique, every parent/dependency resolves, every enum is from the supplied
           lists, every array is present (use [] when empty), and the result is one complete JSON object.
           Return only the proposal. The Producer creates canonical work, evaluates sprint readiness
           and dispatches work; a proposed plan is not evidence that tickets or an active sprint exist.

        The following is a valid CONTRACT EXAMPLE, not project scope, accepted identities, approvals,
        or completed work. Do not copy its example-* keys or movement scope into the project. Adapt
        its structure using the actual accepted deliverables and canonical keys. Notice that containment
        and sequencing are separate, and the QA task validates the implementation rather than its parent.
        """ + "\n" + JsonSerializer.Serialize(PlanningContractExample(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static PlanningOutput PlanningContractExample()
    {
        GameProposedWorkItemV1 Item(string key, string type, string title, string description,
            string criterion, string role, string? parent, string[] skills, string[] dependencies) =>
            new(key, type, title, description, [criterion], role, [], skills,
                ["work.execution.run.v1"], dependencies) { ParentProposalKey = parent };
        return new([
            Item("example-epic", VideoGameWorkItemTypeKeys.Milestone, "Playable prototype",
                "Deliver the example playable increment with independent validation.",
                "The movement story has implementation and independent QA evidence.",
                VideoGameRoleKeys.TechnicalDirector, null, [], []),
            Item("example-story", VideoGameWorkItemTypeKeys.Feature, "Player movement",
                "Provide a testable player movement increment.",
                "Left and right input move the player within the playfield, verified independently.",
                VideoGameRoleKeys.Engineer, "example-epic", [VideoGameSpecializationKeys.GameplayProgramming], []),
            Item("example-spike", VideoGameWorkItemTypeKeys.ResearchSpike, "Validate input integration",
                "Construct a small input prototype and record integration findings.",
                "A runnable prototype and report demonstrate supported key handling and focus behavior.",
                VideoGameRoleKeys.Engineer, "example-story", [VideoGameSpecializationKeys.EngineIntegration], []),
            Item("example-implementation", VideoGameWorkItemTypeKeys.Task, "Implement bounded movement",
                "Implement movement using the spike findings and add reproducible checks.",
                "Left/right keys move in the correct direction; player position never leaves the playfield.",
                VideoGameRoleKeys.Engineer, "example-story", [VideoGameSpecializationKeys.GameplayProgramming], ["example-spike"]),
            Item("example-qa", VideoGameWorkItemTypeKeys.Task, "Independently validate movement",
                "Test the implementation and record results and reproducible defects.",
                "An independent report covers left/right input, focus loss and both playfield boundaries.",
                VideoGameRoleKeys.QualityAssurance, "example-story", [VideoGameSpecializationKeys.TestPlanning], ["example-implementation"])
        ], [], [], []);
    }
}
