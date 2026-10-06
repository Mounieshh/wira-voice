using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core;
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

    [Fact]
    public async Task JevSelector_HandlesOpenRouterResponseFormat()
    {
        var options = new List<JevOption>
        {
            new("n0_click", "Click Submit"),
            new("n1_click", "Click Cancel")
        };
        var q = new JevQuestion("Select action", options, key: "action");

        var innerAnswers = new
        {
            action = new
            {
                type = "choice",
                choice = "n0_click",
                probabilities = new Dictionary<string, double>
                {
                    ["n0_click"] = 0.9,
                    ["n1_click"] = 0.05,
                    ["none_here"] = 0.05
                }
            }
        };

        var openRouterResponse = JsonSerializer.Serialize(new
        {
            model = "typesafe/jev-1.13",
            answers = innerAnswers,
            usage = new { input_tokens = 42 }
        });

        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(openRouterResponse, System.Text.Encoding.UTF8, "application/json")
            });
        });

        using var http = new HttpClient(fakeHandler);
        // sk-or- triggers OpenRouter mode to use /api/alpha/decisions
        var selector = new JevSelector("sk-or-v1-fake-key", endpoint: new Uri("https://openrouter.ai/api/alpha/decisions"), httpClient: http);

        var judgments = await selector.JudgeAsync([], [q]);

        Assert.NotNull(judgments);
        Assert.Equal("n0_click", judgments.Ids[0]);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", capturedRequest.RequestUri?.ToString());
    }

    [Fact]
    public void IsSameApp_FuzzyAndAliasMatching_ResolvesWindowsApps()
    {
        var chrome = new InstalledApplication("Google Chrome", "google chrome", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Google Chrome.lnk");
        var edge = new InstalledApplication("Microsoft Edge", "msedge", "msedge.exe", ["Registered default browser"]);
        var code = new InstalledApplication("Visual Studio Code", "code", @"C:\Program Files\Microsoft VS Code\Code.exe");

        // Chrome process name "chrome" matches "Google Chrome"
        Assert.True(WiraVoice.Core.AppRouting.IsSameApp("chrome", "New Tab - Google Chrome", chrome));
        Assert.True(WiraVoice.Core.AppRouting.IsSameApp("chrome", null, chrome));

        // Edge process name "msedge" matches "Microsoft Edge"
        Assert.True(WiraVoice.Core.AppRouting.IsSameApp("msedge", "Start", edge));

        // Code process name "Code" matches "Visual Studio Code"
        Assert.True(WiraVoice.Core.AppRouting.IsSameApp("Code", "WiraVoice - Visual Studio Code", code));

        // Distinct apps do NOT match
        Assert.False(WiraVoice.Core.AppRouting.IsSameApp("notepad", "Untitled - Notepad", chrome));
        Assert.False(WiraVoice.Core.AppRouting.IsSameApp("cmd", "Command Prompt", edge));
    }

    [Fact]
    public void TopBarViewModel_StrictlyShowsSpokenSentenceWithoutStatusNoise()
    {
        var vm = new WiraVoice.UI.TopBarViewModel();

        // 1. Initial idle state
        Assert.Equal("Say a command or click to listen…", vm.DisplayText);
        Assert.True(vm.IsPlaceholder);

        // 2. User speaks and transcript arrives
        vm.Transcript = "Open anti-gravity.";
        vm.Status = "Transcribing speech locally…";
        // Must show purely the transcript, NO parenthetical status tags
        Assert.Equal("Open anti-gravity.", vm.DisplayText);
        Assert.False(vm.IsPlaceholder);

        // 3. Status changes to decision/workflow step
        vm.Status = "No clear action requested.";
        Assert.Equal("Open anti-gravity.", vm.DisplayText);

        vm.Status = "Finding an action…";
        Assert.Equal("Open anti-gravity.", vm.DisplayText);
    }

    [Fact]
    public void NormalizeCandidateValue_AddressBarAndSearch_NormalizesValues()
    {
        var addressCandidate = new AXCandidate("n1", 1, AXOperation.TypeText, "AXTextField: Address Bar [typeText]", 0);
        var searchCandidate = new AXCandidate("n2", 2, AXOperation.TypeText, "AXSearchField: Search Box [typeText]", 0);

        // Drive / Volume single letter -> D:\
        Assert.Equal(@"D:\", CommandEngine.NormalizeCandidateValue("D", addressCandidate, "Go to new volume D."));
        Assert.Equal(@"D:\", CommandEngine.NormalizeCandidateValue("d", addressCandidate, "Go to volume d"));
        Assert.Equal(@"D:\", CommandEngine.NormalizeCandidateValue("D:", addressCandidate, "Go to D:"));
        Assert.Equal(@"D:\", CommandEngine.NormalizeCandidateValue("volume D", addressCandidate, "Go to volume D"));
        Assert.Equal(@"C:\", CommandEngine.NormalizeCandidateValue("C", addressCandidate, "Go to C drive"));

        // Search Box query cleanup
        Assert.Equal("resume", CommandEngine.NormalizeCandidateValue("the file resume", searchCandidate, "search for the file resume"));
        Assert.Equal("resume", CommandEngine.NormalizeCandidateValue("file resume", searchCandidate, "search for file resume"));
        Assert.Equal("budget report.xlsx", CommandEngine.NormalizeCandidateValue("the file budget report.xlsx", searchCandidate, "find the file budget report.xlsx"));
    }

    [Fact]
    public void AppRouting_Installed_IncludesFileExplorer()
    {
        var installed = AppRouting.Installed();
        Assert.Contains(installed, app => app.Name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase) ||
                                          app.BundleID.Equals("explorer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WindowsAppIdentity_ResolvesFriendlyName_FromExecutablePath()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorerExe = System.IO.Path.Combine(winDir, "explorer.exe");
        if (System.IO.File.Exists(explorerExe))
        {
            var friendly = WindowsAppIdentity.GetFriendlyAppNameFromPath(explorerExe);
            Assert.Equal("Windows Explorer", friendly);
        }
    }

    [Fact]
    public void WindowsAppIdentity_IsSameApplication_MatchesDynamically()
    {
        var chromeApp = new InstalledApplication("Google Chrome", "Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", ["chrome", "browser"]);

        // Match via friendly name
        Assert.True(WindowsAppIdentity.IsSameApplication("Google Chrome", "chrome", "New Tab - Google Chrome", chromeApp));

        // Match via process name
        Assert.True(WindowsAppIdentity.IsSameApplication("chrome", "chrome", "Google", chromeApp));

        // Match via window title
        Assert.True(WindowsAppIdentity.IsSameApplication(null, null, "Inbox (5) - Google Chrome", chromeApp));

        // Negative match
        var notepadApp = new InstalledApplication("Notepad", "Notepad", @"C:\Windows\notepad.exe");
        Assert.False(WindowsAppIdentity.IsSameApplication("Google Chrome", "chrome", "New Tab - Google Chrome", notepadApp));
    }

    [Fact]
    public void InstalledApplication_ModelsAppUserModelID_AndForwardsBundleID()
    {
        var app = new InstalledApplication("Antigravity", "electron.app.Antigravity", @"C:\App\Antigravity.exe");
        Assert.Equal("electron.app.Antigravity", app.AppUserModelID);
        Assert.Equal("electron.app.Antigravity", app.BundleID);
        Assert.Contains("Antigravity", app.Aliases);
        Assert.Contains("electron.app.Antigravity", app.Aliases);
    }

    [Fact]
    public void NormalizeCandidateValue_SupportsNewVolumeD()
    {
        var addressCandidate = new AXCandidate("n1", 1, AXOperation.TypeText, "AXTextField: Address Bar [typeText]", 0);
        Assert.Equal(@"D:\", CommandEngine.NormalizeCandidateValue("new volume D", addressCandidate, "Go to new volume D."));
        Assert.Equal(@"D:\", CommandEngine.NormalizeCandidateValue("new drive D", addressCandidate, "Go to new drive D."));
    }

    [Fact]
    public void AXSnapshot_HasObservedChange_DetectsWindowTitleChange()
    {
        var snapshot1 = new AXSnapshot(1234, "Windows Explorer", "Home - File Explorer", [], false);
        var snapshot2 = new AXSnapshot(1234, "Windows Explorer", "New Volume (D:) - File Explorer", [], false);

        Assert.True(snapshot2.HasObservedChange(snapshot1));
    }

    [Fact]
    public void WindowsAppIdentity_IsSameApplication_MatchesUwpApplicationFrameHost()
    {
        var cameraApp = new InstalledApplication("Camera", "Microsoft.WindowsCamera_8wekyb3d8bbwe!App", "shell:AppsFolder\\Microsoft.WindowsCamera_8wekyb3d8bbwe!App");
        var calcApp = new InstalledApplication("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        // Positive UWP match: process is ApplicationFrameHost and title is Camera
        Assert.True(WindowsAppIdentity.IsSameApplication("Application Frame Host", "ApplicationFrameHost", "Camera", cameraApp));
        Assert.True(WindowsAppIdentity.IsSameApplication("Application Frame Host", "ApplicationFrameHost", "Calculator", calcApp));

        // Negative UWP match: title mismatch
        Assert.False(WindowsAppIdentity.IsSameApplication("Application Frame Host", "ApplicationFrameHost", "Camera", calcApp));
    }

    [Fact]
    public void WindowsAppIdentity_IsShellInfrastructureWindow_IdentifiesShellClasses()
    {
        Assert.True(WindowsAppIdentity.IsShellInfrastructureWindow(IntPtr.Zero, "Shell_TrayWnd"));
        Assert.True(WindowsAppIdentity.IsShellInfrastructureWindow(IntPtr.Zero, "Shell_SecondaryTrayWnd"));
        Assert.True(WindowsAppIdentity.IsShellInfrastructureWindow(IntPtr.Zero, "Progman"));
        Assert.True(WindowsAppIdentity.IsShellInfrastructureWindow(IntPtr.Zero, "WorkerW"));
        Assert.False(WindowsAppIdentity.IsShellInfrastructureWindow(IntPtr.Zero, "CabinetWClass"));
        Assert.False(WindowsAppIdentity.IsShellInfrastructureWindow(IntPtr.Zero, "Chrome_WidgetWin_1"));
    }

    [Fact]
    public void WindowsAppIdentity_IsFileExplorerFolderWindow_IdentifiesCabinetWClass()
    {
        Assert.True(WindowsAppIdentity.IsFileExplorerFolderWindow(IntPtr.Zero, "CabinetWClass"));
        Assert.True(WindowsAppIdentity.IsFileExplorerFolderWindow(IntPtr.Zero, "ExploreWClass"));
        Assert.False(WindowsAppIdentity.IsFileExplorerFolderWindow(IntPtr.Zero, "Shell_TrayWnd"));
        Assert.False(WindowsAppIdentity.IsFileExplorerFolderWindow(IntPtr.Zero, "Progman"));
    }

    [Fact]
    public void AppRouting_Installed_PinsFileExplorerToExplorerExeDirectly()
    {
        var apps = AppRouting.Installed();
        var explorer = apps.FirstOrDefault(a => a.Name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(explorer);
        Assert.EndsWith("explorer.exe", explorer.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(explorer.Aliases, a => a.Equals("explorer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(explorer.Aliases, a => a.Equals("windows explorer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(explorer.Aliases, a => a.Equals("folder", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(explorer.Aliases, a => a.Equals("folders", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(explorer.Aliases, a => a.Equals("volume", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(explorer.Aliases, a => a.Equals("volumes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CommandLanguage_IsExplicitAppLaunchIntent_DetectsExplicitLaunchPatterns()
    {
        var explorerApp = new InstalledApplication("File Explorer", "explorer", "explorer.exe", ["windows explorer", "explorer"]);
        var cameraApp = new InstalledApplication("Camera", "Microsoft.WindowsCamera", "camera.exe");

        Assert.True(CommandLanguage.IsExplicitAppLaunchIntent("Open File Explorer", explorerApp));
        Assert.True(CommandLanguage.IsExplicitAppLaunchIntent(", open camera.", cameraApp));
        Assert.True(CommandLanguage.IsExplicitAppLaunchIntent("launch camera", cameraApp));
        Assert.True(CommandLanguage.IsExplicitAppLaunchIntent("switch to file explorer", explorerApp));

        // Non-launch utterances and greedy false-positive prevention
        Assert.False(CommandLanguage.IsExplicitAppLaunchIntent("search for the file resume", explorerApp));
        Assert.False(CommandLanguage.IsExplicitAppLaunchIntent("click on the button", cameraApp));
        Assert.False(CommandLanguage.IsExplicitAppLaunchIntent("open c", cameraApp));
        Assert.False(CommandLanguage.IsExplicitAppLaunchIntent("start c", cameraApp));
    }

    [Fact]
    public void InstalledApplication_MatchesNameAndWindowTitle_WorksCorrectly()
    {
        var app = new InstalledApplication("Camera", "Microsoft.WindowsCamera", "camera.exe", ["webcam"]);
        Assert.True(app.MatchesName("Camera"));
        Assert.True(app.MatchesName("webcam"));
        Assert.False(app.MatchesName("c"));

        Assert.True(app.MatchesWindowTitle("Camera"));
        Assert.True(app.MatchesWindowTitle("My Webcam Preview"));
        Assert.False(app.MatchesWindowTitle("Calculator"));
    }
}
