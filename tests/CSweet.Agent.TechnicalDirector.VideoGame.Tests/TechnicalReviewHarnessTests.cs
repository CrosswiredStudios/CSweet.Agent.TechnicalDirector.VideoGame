using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.WorkManagement.Contracts;
using Xunit.Abstractions;

namespace CSweet.Agent.TechnicalDirector.VideoGame.Tests;

[Trait("Category", "AgentHarness")]
public sealed class TechnicalReviewHarnessTests(ITestOutputHelper output)
{
    private const string Criterion = "A ready event must be emitted only when the audio context is running.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid Reviewer = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PrismBreakReworkAndRestartReuseOnlyUnchangedFileEvidence()
    {
        var fixture = new ReviewFixture(12, 226533);
        var broken = await fixture.RunAsync();
        Assert.Equal("rejected", broken.OutcomeCode);
        Assert.Contains("suspended", string.Join(" ", broken.Diagnostics));
        Assert.Equal(19, broken.Output.GetProperty("pageCount").GetInt32());
        Assert.Equal(226533, broken.Output.GetProperty("coveredCharacters").GetInt32());
        Assert.Equal(12, fixture.FileCalls);
        Assert.Equal(1, fixture.Decisions);
        Assert.Equal(19, fixture.TransportReads);
        Assert.InRange(fixture.Peak, 1, 25000);
        Assert.InRange(fixture.PromptCharacters, 1, 300000);
        var cold = fixture.PromptCharacters;
        output.WriteLine($"Initial review: {fixture.FileCalls} file calls + {fixture.Decisions} decision; {cold:N0} input message characters; largest {fixture.Peak:N0}.");

        fixture.Publish(fixedAudio: true);
        var corrected = await fixture.RunAsync();
        Assert.Equal("approved", corrected.OutcomeCode);
        Assert.Equal(fixture.Source, corrected.Output.GetProperty("sourceCommitSha").GetString());
        Assert.Equal("resolved", Assert.Single(corrected.Output.GetProperty("resolutions").EnumerateArray()).GetProperty("disposition").GetString());
        Assert.Equal(11, corrected.Output.GetProperty("reusedSections").GetInt32());
        Assert.Equal(1, fixture.FileCalls);
        Assert.Equal(1, fixture.Decisions);
        Assert.True(fixture.PromptCharacters < cold / 4);
        output.WriteLine($"One-file correction: {fixture.FileCalls} file call + {fixture.Decisions} decision; {fixture.PromptCharacters:N0} input message characters.");

        var replay = await fixture.RunAsync();
        Assert.Equal("approved", replay.OutcomeCode);
        Assert.Equal(0, fixture.FileCalls);
        Assert.Equal(0, fixture.Decisions);
        Assert.True(replay.Output.GetProperty("reusedVerdict").GetBoolean());
        Assert.Equal(19, fixture.TransportReads);
        output.WriteLine($"Restart: {fixture.FileCalls} file calls + {fixture.Decisions} decision; {fixture.PromptCharacters:N0} input message characters.");

        fixture.Publish(fixedAudio: false);
        var regression = await fixture.RunAsync();
        Assert.Equal("rejected", regression.OutcomeCode);
        Assert.Equal(0, fixture.FileCalls);
        Assert.Contains("suspended", string.Join(" ", regression.Diagnostics));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("planning")]
    [InlineData("validation")]
    [InlineData("moved-during-review")]
    [InlineData("corrupt-page")]
    public async Task CachedFileFactsCannotAuthorizeAStaleVerdict(string change)
    {
        var fixture = new ReviewFixture(2, 0);
        fixture.Publish(fixedAudio: true);
        Assert.Equal("approved", (await fixture.RunAsync()).OutcomeCode);
        fixture.Change = change;
        var result = await fixture.RunAsync();
        if (change is "target" or "planning")
        {
            Assert.Equal("approved", result.OutcomeCode);
            Assert.Equal(2, fixture.FileCalls);
        }
        else if (change == "validation")
        {
            Assert.Equal("rejected", result.OutcomeCode);
            Assert.Equal(0, fixture.FileCalls);
            Assert.Contains("validation", string.Join(" ", result.Diagnostics));
        }
        else
        {
            Assert.Equal("blocked", result.OutcomeCode);
            Assert.Empty(result.Evidence);
            Assert.Equal(0, fixture.FileCalls);
            Assert.Equal(0, fixture.Decisions);
        }
    }

    [Fact]
    public async Task CrossFileEvidenceCanClearAMisattributedFinding()
    {
        var fixture = new ReviewFixture(2, 0) { Change = "consumer-finding" };
        fixture.Publish(fixedAudio: true);
        var result = await fixture.RunAsync();
        Assert.Equal("approved", result.OutcomeCode);
        var resolution = Assert.Single(result.Output.GetProperty("resolutions").EnumerateArray());
        Assert.Equal("resolved", resolution.GetProperty("disposition").GetString());
        Assert.Equal(2, resolution.GetProperty("evidenceOffsets").GetArrayLength());
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(false, "rejected")]
    [InlineData(true, "approved")]
    public async Task OversizedReviewNotesStillReachAnEvidenceBoundDecision(bool corrected, string expected)
    {
        var fixture = new ReviewFixture(12, 226533) { Change = "verbose-notes" };
        fixture.Publish(corrected);
        var outcome = await fixture.RunAsync();
        Assert.Equal(expected, outcome.OutcomeCode);
        Assert.Equal(12, fixture.FileCalls);
        Assert.True(fixture.SummaryCalls > 1);
        Assert.Equal(1, fixture.Decisions);
        Assert.Equal(226533, outcome.Output.GetProperty("coveredCharacters").GetInt32());
        Assert.Equal(corrected, outcome.Output.GetProperty("criteria")[0].GetProperty("satisfied").GetBoolean());
        if (!corrected) Assert.Contains("suspended", string.Join(" ", outcome.Diagnostics));
    }

    [LocalReviewFact]
    [Trait("Category", "LocalInference")]
    public async Task LocalModelReviewsSuspendedAndRunningAudio()
    {
        using var model = new LocalReviewModel();
        var fixture = new ReviewFixture(2, 0, model.ReplyAsync);
        var broken = await fixture.RunAsync();
        output.WriteLine($"Defect: {broken.OutcomeCode}. {broken.Summary} {string.Join(" ", broken.Diagnostics)}");
        Assert.Equal("rejected", broken.OutcomeCode);
        var defect = Assert.Single(broken.Diagnostics, d => d.StartsWith("audio.js:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ready", defect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("running", defect, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(broken.Diagnostics, d => d.StartsWith("consumer.js:", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"Provider reported {model.InputTokens:N0} input and {model.OutputTokens:N0} output tokens over {model.Calls} calls.");
        model.ResetUsage();
        fixture.Publish(fixedAudio: true);
        var corrected = await fixture.RunAsync();
        output.WriteLine($"Correction: {corrected.OutcomeCode}. {corrected.Summary} {string.Join(" ", corrected.Diagnostics)}");
        Assert.Equal("approved", corrected.OutcomeCode);
        Assert.Equal(1, corrected.Output.GetProperty("reusedSections").GetInt32());
        output.WriteLine($"Provider reported {model.InputTokens:N0} input and {model.OutputTokens:N0} output tokens over {model.Calls} calls.");
    }

    private sealed class ReviewFixture
    {
        private readonly AgentTestRuntime runtime = new();
        private readonly WorkExecutionAssignment assignment;
        private readonly WorkItem item;
        private readonly Guid repository = Guid.NewGuid();
        private readonly string[] files;
        private readonly int targetSize;
        private string patch = "";
        private Guid publication;
        private int referenceReads;
        public string Change { get; set; } = "";
        public string Source { get; private set; } = "";
        public int FileCalls, SummaryCalls, Decisions, TransportReads, PromptCharacters, Peak;

        public ReviewFixture(int fileCount, int targetSize, Func<JsonElement, CancellationToken, Task<JsonElement>>? inference = null)
        {
            this.targetSize = targetSize;
            files = ["audio.js", "consumer.js", .. Enumerable.Range(2, fileCount - 2).Select(i => $"module-{i}.js")];
            var (request, input) = WorkflowReviewAssignmentTests.Create("technical-review", "game-technical-director");
            var planning = input.Planning! with { Requirements = ["Signal audio availability."], AcceptanceCriteria = [Criterion] };
            var plan = Guid.NewGuid();
            assignment = request with { PlanningRevision = 1, DeliveryPlanId = plan,
                Input = JsonSerializer.SerializeToElement(input with { Planning = planning }, Json) };
            item = JsonSerializer.Deserialize<WorkItem>("{}")! with { Id = assignment.TaskItemId, Planning = planning, PlanningRevision = 1,
                Delivery = new(repository, planning.Requirements, planning.AcceptanceCriteria) { DeliveryPlanId = plan, BaseBranch = "story" },
                StageAssignments = [new("technical-review", "AgentInstallation", AgentInstallationId: Reviewer)] };
            _ = new OperatingStateFixture(runtime);
            runtime.RegisterCapability<WorkItemReference, WorkItem>(WorkManagementCapabilityNames.ItemRead, (_, _) =>
                Task.FromResult(Change == "planning" ? item with { PlanningRevision = 2,
                    Planning = planning with { Constraints = ["Retain the event name."] } } : item));
            runtime.RegisterCapability<ReviewGitMergeRequest, GitMergeReview>(GitMergeCapabilities.Review, (r, _) => Task.FromResult(Read(r)));
            runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, async (r, ct) =>
            {
                var messages = r.GetProperty("messages").EnumerateArray().ToArray();
                var size = messages.Sum(m => m.GetProperty("text").GetString()!.Length);
                PromptCharacters += size;
                Peak = Math.Max(Peak, size);
                var prompt = JsonSerializer.Deserialize<JsonElement>(messages[1].GetProperty("text").GetString()!);
                if (prompt.TryGetProperty("page", out var page))
                {
                    FileCalls++;
                    Assert.DoesNotContain("implementationEvidence", prompt.GetRawText());
                    Assert.Single(page.GetProperty("text").GetString()!.Split("diff --git ").Skip(1));
                }
                else if (prompt.TryGetProperty("notes", out _)) SummaryCalls++;
                else Decisions++;
                if (inference is not null) return await inference(r, ct);
                return JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(Answer(prompt), Json), role = "assistant" });
            });
            Publish(false);
        }

        public void Publish(bool fixedAudio)
        {
            publication = Guid.NewGuid();
            var builder = new StringBuilder();
            foreach (var file in files)
            {
                var body = file switch
                {
                    "audio.js" => "export function announceAudio(ctx, emit) {\n" + (fixedAudio ? "  if (ctx.state !== 'running') return;\n" : "") +
                        "  emit('ready', { contextState: ctx.state });\n}",
                    "consumer.js" => "export function onReady(event) { return event.contextState; }",
                    _ => "export const enabled = true;"
                };
                // Scale the fixture with a source comment. Keep unrelated files byte-identical on rework.
                var prefix = $"diff --git a/{file} b/{file}\nnew file mode 100644\n--- /dev/null\n+++ b/{file}\n";
                var lines = body.Split('\n');
                var length = targetSize / files.Length + (file == files[^1] ? targetSize % files.Length : 0);
                var hunk = $"@@ -0,0 +1,{lines.Length + (length > 0 ? 1 : 0)} @@\n";
                var content = prefix + hunk + string.Concat(lines.Select(s => "+" + s + "\n"));
                builder.Append(content);
                if (length > 0) builder.Append("+//").Append('x', length - content.Length - 4).Append('\n');
            }
            patch = builder.ToString();
            Source = Hash(patch)[..40];
        }

        private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private GitMergeReview Read(ReviewGitMergeRequest r)
        {
            Assert.False(r.IncludePatch);
            if (r.DiffRead is null) referenceReads++;
            var targetMoved = Change == "target" || Change == "moved-during-review" && referenceReads == 2;
            var reference = new GitMergeReview(publication, repository, item.Id, "Review fixture", Source, null, "", [], [], "AwaitingValidation")
            {
                ChangedFiles = files, TargetBranch = "story", TargetCommitSha = new string(targetMoved ? 'd' : 'b', 40),
                PatchDigest = Hash(patch), ImplementationEvidence = Change == "validation" ? [] :
                    [new("node --test audio.test.js", true, 0, targetSize == 0 ? "1 passed: ready-event contract" : new string('v', 4000))]
            };
            if (r.DiffRead is not { } read) return reference;
            Assert.Equal(reference.PatchDigest, read.PatchDigest);
            Assert.Equal(reference.CandidateCommitSha, read.CandidateCommitSha);
            Assert.Equal(reference.TargetCommitSha, read.TargetCommitSha);
            TransportReads++;
            var text = patch.Substring(read.Offset, Math.Min(12000, patch.Length - read.Offset));
            if (Change == "corrupt-page") text = "!" + text[1..];
            var end = read.Offset + text.Length;
            return reference with { DiffPage = new(read.PatchDigest, read.Offset, patch.Length, text, end < patch.Length ? end : null) };
        }

        private object Answer(JsonElement prompt)
        {
            if (prompt.TryGetProperty("notes", out var notes))
            {
                var offsets = notes.EnumerateArray().SelectMany(n => n.GetProperty("evidenceOffsets").EnumerateArray().Select(o => o.GetInt32()));
                return new { points = new[] { new { evidence = "Condensed section observations.", evidenceOffsets = offsets.ToArray() } } };
            }
            if (prompt.TryGetProperty("page", out var page))
            {
                var text = page.GetProperty("text").GetString()!;
                if (Change == "consumer-finding" && text.Contains("export function onReady"))
                {
                    var finding = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                        "Fixtures", "technical-review-consumer-finding.json")));
                    return new { summary = "Consumer returns the state reported by the emitter.", blockingFindings = new[] { finding },
                        observations = Array.Empty<string>(), questions = Array.Empty<string>() };
                }
                var faulty = text.Contains("emit('ready'") && !text.Contains("!== 'running'");
                var summary = faulty ? "Ready may be emitted while suspended" : text.Contains("announceAudio") ? "audio.js guards emission on running state." : "Source section checked.";
                if (Change == "verbose-notes") summary += new string('v', 11500);
                return new { summary,
                    blockingFindings = faulty ? new[] { new { path = "audio.js", problem = "Ready while suspended",
                        evidence = "emit('ready') has no running-state guard", correction = "Check ctx.state before emitting" } } : [],
                    observations = Array.Empty<string>(), questions = Array.Empty<string>() };
            }
            var findings = prompt.GetProperty("findings").EnumerateArray().ToArray();
            var receipt = prompt.GetProperty("reference").GetProperty("implementationEvidence").GetArrayLength() > 0;
            if (Change == "consumer-finding")
            {
                var pages = prompt.GetProperty("pages").EnumerateArray().ToArray();
                Assert.Contains(pages, p => p.GetProperty("summary").GetString()!.Contains("guards emission"));
                Assert.Equal("consumer.js", Assert.Single(findings).GetProperty("finding").GetProperty("path").GetString());
                var evidenceOffsets = pages.Select(p => p.GetProperty("offset").GetInt32()).ToArray();
                return new { summary = "The guard is present at the emitter.", additionalFindings = Array.Empty<object>(),
                    resolutions = new[] { new { resolved = true, reason = "The emitter owns the guard. The consumer receives the resulting event.", evidenceOffsets } },
                    criteria = new[] { new { satisfied = true, evidence = "The emitter enforces running state before sending to the consumer.", evidenceOffsets } } };
            }
            var stillFaulty = findings.Any(f => f.GetProperty("evidenceOffsets").GetArrayLength() > 0);
            var first = prompt.GetProperty("pages")[0];
            var offset = first.TryGetProperty("offset", out var sectionOffset) ? sectionOffset.GetInt32() : first.GetProperty("evidenceOffsets")[0].GetInt32();
            return new { summary = "Candidate reviewed.", additionalFindings = Array.Empty<object>(),
                resolutions = findings.Select(f => new { resolved = !stillFaulty, reason = stillFaulty ? "The cited code emits the event while suspended." : "The current audio.js guard checks running before emitting.",
                    evidenceOffsets = stillFaulty ? f.GetProperty("evidenceOffsets").EnumerateArray().Select(o => o.GetInt32()).ToArray() : [offset] }),
                criteria = new[] { new { satisfied = receipt && !stillFaulty,
                    evidence = !receipt ? "Missing candidate validation" : "Checked the event guard and current validation receipt.",
                    evidenceOffsets = new[] { offset } } } };
        }

        public async Task<WorkExecutionOutcomeV1> RunAsync()
        {
            FileCalls = SummaryCalls = Decisions = TransportReads = PromptCharacters = Peak = referenceReads = 0;
            var host = new AgentTestHost(new SpecialistAgent(), runtime);
            var configured = await host.ExecuteCapabilityAsync(AgentConfigurationCapabilities.Update,
                new { settings = new { llmProviderId = Guid.NewGuid(), llmModel = "review-test" } });
            Assert.True(configured.Succeeded, configured.Error);
            var delivery = Change == "planning" ? assignment with { PlanningRevision = 2,
                Input = JsonSerializer.SerializeToElement(assignment.Input.Deserialize<WorkExecutionInputV1>(Json)! with { PlanningRevision = 2 }, Json) } : assignment;
            var work = await host.ExecuteCapabilityAsync(WorkManagementCapabilityNames.ExecutionRun, delivery);
            Assert.True(work.Succeeded, work.Error);
            return work.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Json)!;
        }
    }
}
