using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Commands;
using WiraVoice.Core.Diagnostics;
using WiraVoice.Core.Language;
using Xunit;

namespace WiraVoice.Tests;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        Handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Handler(request);
    }
}

public class ComputahArchitectureTests
{
    [Fact]
    public void SensitiveText_DetectsAndRedactsCredentials()
    {
        var source = "export API_KEY=\"secret-12345\" and sk-123456789012345678 in text";
        var ranges = SensitiveText.ProtectedRanges(source);
        Assert.NotEmpty(ranges);

        var redacted = SensitiveText.Redact(source);
        Assert.DoesNotContain("secret-12345", redacted);
        Assert.DoesNotContain("sk-123456789012345678", redacted);
        Assert.Contains("[REDACTED CREDENTIAL]", redacted);
    }

    [Fact]
    public void SourceToken_RedactsCredentialInModelText_PreservesRawText()
    {
        var source = "Enter password=supersecret now";
        var tokens = SourceToken.Read(source);

        Assert.NotEmpty(tokens);
        // Find the password token
        var secretToken = tokens.FirstOrDefault(t => t.Text.Contains("supersecret"));
        Assert.Equal("[REDACTED CREDENTIAL]", secretToken.ModelText);
        Assert.Contains("supersecret", secretToken.Text);
    }

    [Fact]
    public void CommandLanguage_ValidatedUrl_ValidatesCorrectly()
    {
        var valid1 = CommandLanguage.ValidatedUrl("google.com");
        Assert.NotNull(valid1);
        Assert.Equal("google.com", valid1.Host);

        var valid2 = CommandLanguage.ValidatedUrl("https://news.ycombinator.com/item?id=1");
        Assert.NotNull(valid2);
        Assert.Equal("news.ycombinator.com", valid2.Host);

        var invalid1 = CommandLanguage.ValidatedUrl("not an address");
        Assert.Null(invalid1);

        var invalid2 = CommandLanguage.ValidatedUrl("http://-invalid-.com");
        Assert.Null(invalid2);
    }

    [Fact]
    public void CommandEngine_NumericTargetMatches_CalculatesTolerance()
    {
        var node = new AXNode
        {
            Id = 1,
            Role = "AXSlider",
            Minimum = 0,
            Maximum = 100
        };

        // Exact match
        Assert.True(CommandEngine.NumericTargetMatches("50.0", "50", node));

        // Percent match (50% of 0..100 is 50)
        Assert.True(CommandEngine.NumericTargetMatches("50.0", "50%", node));

        // Close match within tolerance
        Assert.True(CommandEngine.NumericTargetMatches("50.05", "50", node));

        // Out of range or different value
        Assert.False(CommandEngine.NumericTargetMatches("10.0", "50", node));
    }

    [Fact]
    public async Task JevSelector_ChunksQuestions_WhenExceedingBudget()
    {
        // 300 options exceeds budget of 254
        var options = Enumerable.Range(0, 300)
            .Select(i => new JevOption($"opt_{i}", $"Option {i} description"))
            .ToList();

        var q = new JevQuestion("Select the best option", options, optionBudget: 150, key: "target");

        int requestsCount = 0;
        var fakeHandler = new FakeHttpMessageHandler(async req =>
        {
            requestsCount++;
            var body = await req.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var questionsObj = doc.RootElement.GetProperty("questions");

            // Build answers response matching wire keys
            var answers = new Dictionary<string, object>();
            foreach (var prop in questionsObj.EnumerateObject())
            {
                var criteria = prop.Value.GetProperty("criteria");
                var firstChoice = criteria.EnumerateObject().First().Name;

                var probs = new Dictionary<string, double>();
                int cCount = criteria.EnumerateObject().Count();
                double pOther = 0.1 / (cCount - 1);
                foreach (var opt in criteria.EnumerateObject())
                {
                    probs[opt.Name] = opt.Name == firstChoice ? 0.9 : pOther;
                }
                // Normalize sum to 1.0
                double sum = probs.Values.Sum();
                probs[firstChoice] += (1.0 - sum);

                answers[prop.Name] = new Dictionary<string, object>
                {
                    ["type"] = "choice",
                    ["choice"] = firstChoice,
                    ["confidence"] = 0.9,
                    ["probabilities"] = probs
                };
            }

            var responseJson = JsonSerializer.Serialize(new
            {
                model = "jev-1.13.0",
                answers = answers,
                usage = new { input_tokens = 50 }
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(fakeHandler);
        var selector = new JevSelector("fake_key", httpClient: http);

        var judgments = await selector.JudgeAsync([], [q]);

        Assert.NotNull(judgments);
        Assert.Single(judgments.Ids);
        Assert.NotNull(judgments.Ids[0]);
        // Because 300 options were chunked by 150 into 2 parts, it took round 1 (2 parts) + round 2 (finalists)
        Assert.True(requestsCount >= 2);
    }
}
