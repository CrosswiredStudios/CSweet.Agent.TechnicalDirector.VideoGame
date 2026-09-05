using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed class SpecialistAgent : VideoGameSpecialistAgentBase
{
    public override string AgentId => "com.csweet.video-game-technical-director";
    public override string Version => "2.1.1";
    protected override string RoleKey => "game-technical-director";
    protected override string ArtifactTypeKey => "video-game.technical-design.v1";
    protected override string RolePrompt => "Own engine and toolchain feasibility, runtime architecture, performance budgets, technical standards, integration boundaries, and technical approvals. Require measured evidence and executable constraints.";
    protected override IReadOnlyList<string> RequiredSections => ["Architecture", "Toolchain Feasibility", "Performance Budgets", "Technical Standards", "Risks", "Approval Criteria"];
}
