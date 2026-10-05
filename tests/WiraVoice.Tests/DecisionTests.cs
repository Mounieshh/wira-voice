using System.Drawing;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Decision;
using Xunit;

namespace WiraVoice.Tests;

public class DecisionTests
{
    [Fact]
    public void LexicalTokenizer_ExtractsTokensWithOffsets()
    {
        string text = "Click Submit button";
        var tokens = LexicalTokenizer.Tokenize(text);

        Assert.Equal(3, tokens.Count);
        Assert.Equal("Click", tokens[0].Text);
        Assert.Equal(0, tokens[0].StartUtf16);
        Assert.Equal(5, tokens[0].EndUtf16);

        Assert.Equal("Submit", tokens[1].Text);
        Assert.Equal(6, tokens[1].StartUtf16);
        Assert.Equal(12, tokens[1].EndUtf16);

        string sliced = LexicalTokenizer.SliceVerbatim(text, tokens, "t1", "t1");
        Assert.Equal("Submit", sliced);
    }

    [Fact]
    public void SensitiveTextFilter_RedactsApiKeysAndSecrets()
    {
        string raw = "My api_key=sk-123456789 and password: mySuperSecretPassword";
        string clean = SensitiveTextFilter.Redact(raw);

        Assert.DoesNotContain("sk-123456789", clean);
        Assert.DoesNotContain("mySuperSecretPassword", clean);
        Assert.Contains("[REDACTED]", clean);
    }

    [Fact]
    public async Task JevClient_EvaluatesActionHeuristicFallback()
    {
        using var client = new JevOpenRouterClient();
        var box = new Rectangle(0, 0, 100, 40);
        var submitBtn = new UiControlCandidate("n0_pressClick", "Submit", "Button", "", box, "pressClick", null);
        var cancelBtn = new UiControlCandidate("n1_pressClick", "Cancel", "Button", "", box, "pressClick", null);
        var scene = new UiSceneSnapshot(System.IntPtr.Zero, 1, "test", "Dialog", new[] { submitBtn, cancelBtn });

        var decision = await client.EvaluateIntentAsync("Please click submit now", scene);

        Assert.NotNull(decision);
        Assert.Equal("n0_pressClick", decision.SelectedControlId);
        Assert.True(decision.Confidence > 0.5);
    }
}
