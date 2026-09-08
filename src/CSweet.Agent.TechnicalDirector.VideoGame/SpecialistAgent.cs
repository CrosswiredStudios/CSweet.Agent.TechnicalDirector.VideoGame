using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent : VideoGameSpecialistAgentBase
{
    public override string AgentId => "com.csweet.video-game-technical-director";
    public override string Version => "2.8.0";
    protected override string RoleKey => "game-technical-director";
    protected override string ArtifactTypeKey => "video-game.technical-design.v1";
    protected override string RolePrompt => "Own the product Git repository, branch and integration standards, repository readiness, and technical review criteria. Own engine and toolchain feasibility, runtime architecture, performance budgets, technical standards, integration boundaries, and technical approvals. Require measured evidence and executable constraints.";
    protected override IReadOnlyList<string> RequiredSections => ["Architecture", "Toolchain Feasibility", "Performance Budgets", "Technical Standards", "Risks", "Approval Criteria"];
}
