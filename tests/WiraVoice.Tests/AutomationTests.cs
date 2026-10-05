using System;
using System.Drawing;
using WiraVoice.Core.Automation;
using Xunit;

namespace WiraVoice.Tests;

public class AutomationTests
{
    [Fact]
    public void UiControlCandidate_PreservesProperties()
    {
        var box = new Rectangle(100, 200, 80, 30);
        var candidate = new UiControlCandidate("n0_pressClick", "Submit", "Button", "Submits the form", box, "pressClick", null);

        Assert.Equal("n0_pressClick", candidate.Id);
        Assert.Equal("Submit", candidate.Name);
        Assert.Equal("Button", candidate.Role);
        Assert.Equal("pressClick", candidate.SupportedOperation);
        Assert.Equal(100, candidate.BoundingBox.X);
        Assert.Equal(200, candidate.BoundingBox.Y);
    }

    [Fact]
    public void UiSceneSnapshot_PreservesControls()
    {
        var box = new Rectangle(50, 50, 200, 25);
        var candidate = new UiControlCandidate("n0_typeText", "Search", "Edit", "", box, "typeText", null);
        var snapshot = new UiSceneSnapshot(new IntPtr(12345), 999, "chrome", "Google - Chrome", new[] { candidate });

        Assert.Equal("chrome", snapshot.ProcessName);
        Assert.Equal("Google - Chrome", snapshot.WindowTitle);
        Assert.Single(snapshot.InteractiveControls);
        Assert.Equal("n0_typeText", snapshot.InteractiveControls[0].Id);
    }
}
