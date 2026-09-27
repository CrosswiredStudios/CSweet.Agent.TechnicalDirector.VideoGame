using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

public sealed class PlanningHandoffTests
{
    private static (AgentCoordinationTurnRequest Request, GameProductionPlanningCycleV1 Cycle) Fixture(JsonNode context)
    {
        var self = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Victor", "Technical Director");
        var producer = new AgentCoordinationParticipant(Guid.NewGuid(), Guid.NewGuid(), "Gabriel", "Producer");
        var cycle = new GameProductionPlanningCycleV1(Guid.NewGuid(), Guid.NewGuid(), 4, Guid.NewGuid(),
            "profile", Guid.NewGuid(), 1, "accepted-package", "concept", "vision", "fingerprint");
        AgentCoordinationTurn Turn(int ordinal, Guid speaker, GameProductionPlanningCycleV1 value, JsonNode data)
        {
            var payload = JsonSerializer.SerializeToNode(value)!.AsObject();
            payload["coordinationContext"] = data.DeepClone();
            return new(Guid.NewGuid(), ordinal, speaker, "Continue", "Read the exact artifact context", DateTimeOffset.UtcNow,
                new("video-game.production.planning-cycle.v1", "1.0", value.PlanningFingerprint, 1, true,
                    JsonSerializer.SerializeToElement(payload), "digest"));
        }
        return (new(Guid.NewGuid(), 4, 4, "Planning", "Plan", [], self, producer, false,
            [Turn(0, producer.OrganizationUserId, cycle, context),
             Turn(1, self.OrganizationUserId, cycle, JsonValue.Create("Untrusted self context")!),
             Turn(2, producer.OrganizationUserId, cycle with { ApprovedPackageDigest = "old-package" }, JsonValue.Create("Stale context")!)]),cycle);
    }

    [Fact]
    public void FullContextIsReadOnlyFromTheAuthenticatedMatchingPlanningTurn()
    {
        var text = "Exact owner correction: physical captures deferred, never passed.\n" + new string('x', 40000) + "\nPrior decisions retained ≥ 60 fps.";
        var (request, cycle) = Fixture(JsonValue.Create(text)!);
        var message = SpecialistAgent.PlanningCoordinationMessage(request,cycle);
        Assert.Contains(text,message.Text);
        Assert.DoesNotContain("Untrusted self context",message.Text);
        Assert.DoesNotContain("Stale context",message.Text);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("empty")]
    [InlineData("oversized")]
    public void InvalidArtifactContextCannotBeSilentlyIgnored(string kind)
    {
        JsonNode data = kind switch { "object" => new JsonObject(), "empty" => JsonValue.Create(" ")!, _ => JsonValue.Create(new string('x', 65536))! };
        var (request,cycle) = Fixture(data);
        Assert.Throws<InvalidOperationException>(()=>SpecialistAgent.PlanningCoordinationMessage(request,cycle));
    }
}
