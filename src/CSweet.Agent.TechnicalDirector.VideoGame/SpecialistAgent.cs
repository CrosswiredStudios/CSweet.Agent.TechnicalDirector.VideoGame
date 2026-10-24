using CrosswiredStudios.VideoGame.AgentKit;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.TechnicalDirector.VideoGame;

public sealed partial class SpecialistAgent : VideoGameSpecialistAgentBase
{
    internal const int DefaultContextWindowTokens = 256_000;
    internal const int DefaultOutputTokens = 32_000;
    internal const int PlanningOutputTokens = 32_000;
    internal const int ReviewOutputTokens = 8_192;
    internal const int GuidanceOutputTokens = 4_096;
    private const int MinimumOutputTokens = 2_048;
    public override string AgentId => "com.csweet.video-game-technical-director";
    public override string Version => "5.0.12";
    protected override string RoleKey => "game-technical-director";
    protected override string ArtifactTypeKey => "video-game.technical-design.v1";
    protected override string RolePrompt => "Own the product Git repository, branch and integration standards, repository readiness, and technical review criteria. Own engine and toolchain feasibility, runtime architecture, performance budgets, technical standards, integration boundaries, and technical approvals. Favor a small playable prototype, incremental refinement and profiling-led optimization. Treat performance budgets as ongoing constraints; require hard device measurements at the appropriate later validation gate, with an early blocking spike only for a specific justified architectural risk. Require evidence from implementation and QA owners appropriate to the current stage, without inventing stricter acceptance gates. Discover available approved project compute and certified build options. Coordinate isolated team test compute and deliver the exact-source, health-checked review URL to the project manager and CEO. A test URL is not a public release. For assigned release build-readiness, use the agreed compute test or certified build path through the typed platform client. You do not write implementation code, build prototypes in a host shell, fabricate measurements, commit repository changes, deploy publicly, or publish releases.";
    protected override IReadOnlyList<string> RequiredSections => ["Architecture", "Toolchain Feasibility", "Performance Budgets", "Technical Standards", "Risks", "Approval Criteria"];

    protected override AgentConfigurationBuilder Configure(AgentConfigurationBuilder builder) =>
        base.Configure(builder)
            .Number("maxContextWindowTokens", "Maximum context-window tokens", required: true,
                description: "Planning ceiling for Technical Director model requests; set this no higher than the selected model's real context window.",
                minimum: 32_769, step: 1_000,
                defaultValue: DefaultContextWindowTokens)
            .Number("maxOutputTokens", "Maximum output tokens", required: true,
                description: "Upper limit including reasoning. Task ceilings are 32000 for planning, 8192 for documents/reviews, and 4096 for support/build selection. Lower configured limits are respected.",
                minimum: MinimumOutputTokens, step: 1_000,
                defaultValue: DefaultOutputTokens,
                lessThanFieldKey: "maxContextWindowTokens");

    internal static int ResolveOutputTokens(AgentSettings settings, int taskCeiling = PlanningOutputTokens)
    {
        var contextWindow = Math.Max(settings.GetInt32("maxContextWindowTokens", DefaultContextWindowTokens),
            MinimumOutputTokens + 1);
        var output = Math.Max(settings.GetInt32("maxOutputTokens", DefaultOutputTokens),
            MinimumOutputTokens);
        return Math.Min(taskCeiling, Math.Min(output, contextWindow - 1));
    }

    protected override ChatOptions? ResponseOptions() =>
        ResponseOptions(ReviewOutputTokens);

    private ChatOptions ResponseOptions(int taskCeiling) =>
        new() { MaxOutputTokens = ResolveOutputTokens(Settings, taskCeiling) };
}
