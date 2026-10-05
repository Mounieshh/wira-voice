using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using Xunit;

namespace WiraVoice.Tests;

public class FakeInputActor : IInputActor
{
    public UiControlCandidate? LastClickedControl { get; private set; }
    public string? LastTypedText { get; private set; }
    public bool ReturnPressed { get; private set; } = false;

    public Task<bool> ClickControlAsync(UiControlCandidate control, CancellationToken ct = default)
    {
        LastClickedControl = control;
        return Task.FromResult(true);
    }

    public Task<bool> TypeTextAsync(UiControlCandidate control, string text, CancellationToken ct = default)
    {
        LastClickedControl = control;
        LastTypedText = text;
        return Task.FromResult(true);
    }

    public Task<bool> PressReturnAsync(UiControlCandidate control, CancellationToken ct = default)
    {
        ReturnPressed = true;
        return Task.FromResult(true);
    }
}

public class InputActorTests
{
    [Fact]
    public async Task FakeInputActor_RecordsClicksAndText()
    {
        var actor = new FakeInputActor();
        var box = new Rectangle(10, 20, 100, 30);
        var candidate = new UiControlCandidate("n0_typeText", "Search", "Edit", "", box, "typeText", null);

        await actor.TypeTextAsync(candidate, "hello world");

        Assert.Equal("Search", actor.LastClickedControl?.Name);
        Assert.Equal("hello world", actor.LastTypedText);
    }
}
