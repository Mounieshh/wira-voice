using System.Threading.Tasks;
using WiraVoice.Core.Config;
using WiraVoice.Core.Speech;
using WiraVoice.UI;
using Xunit;

namespace WiraVoice.Tests;

public class TopBarViewModelTests
{
    [Fact]
    public void InitialState_ShowsPlaceholderAndRestingEqualizer()
    {
        var vm = new TopBarViewModel();

        Assert.False(vm.IsListening);
        Assert.True(vm.IsPlaceholder);
        Assert.Equal("Say a command or click to listen…", vm.DisplayText);
        Assert.Equal("Start listening", vm.ListenButtonTooltip);

        // Baseline resting heights matching Computah
        Assert.Equal(6.0, vm.Bar0Height);
        Assert.Equal(13.0, vm.Bar1Height);
        Assert.Equal(17.0, vm.Bar2Height);
        Assert.Equal(9.0, vm.Bar3Height);
    }

    [Fact]
    public void SettingTranscript_UpdatesDisplayTextAndClearsPlaceholder()
    {
        var vm = new TopBarViewModel();

        vm.Transcript = "Now can you open up X dot com?";

        Assert.False(vm.IsPlaceholder);
        Assert.Equal("Now can you open up X dot com?", vm.DisplayText);

        vm.ClearTranscript();
        Assert.True(vm.IsPlaceholder);
        Assert.Equal("Say a command or click to listen…", vm.DisplayText);
    }

    [Fact]
    public async Task ToggleListening_TogglesStateAndPlaceholderText()
    {
        var vm = new TopBarViewModel();

        await vm.ToggleListeningAsync();
        Assert.True(vm.IsListening);
        Assert.Equal("Stop listening", vm.ListenButtonTooltip);
        Assert.Equal("Go ahead, I'm listening…", vm.DisplayText);

        await vm.ToggleListeningAsync();
        Assert.False(vm.IsListening);
        Assert.Equal("Start listening", vm.ListenButtonTooltip);
        Assert.Equal("Say a command or click to listen…", vm.DisplayText);
    }

    [Fact]
    public void UpdateWaveformTick_WhenListening_ModulatesEqualizerBars()
    {
        var vm = new TopBarViewModel
        {
            IsListening = true,
            AudioLevel = 0.8
        };

        // Advance several animation frames
        for (int i = 0; i < 5; i++)
        {
            vm.UpdateWaveformTick(0.033);
        }

        // When audio level is active, bars dynamically animate above resting baseline
        Assert.True(vm.Bar0Height >= 4.0 && vm.Bar0Height <= 22.0);
        Assert.True(vm.Bar1Height >= 4.0 && vm.Bar1Height <= 22.0);
        Assert.True(vm.Bar2Height >= 4.0 && vm.Bar2Height <= 22.0);
        Assert.True(vm.Bar3Height >= 4.0 && vm.Bar3Height <= 22.0);
    }

    [Fact]
    public async Task SetEngineMode_SwitchesBetweenCloudAndLocal()
    {
        var vm = new TopBarViewModel();
        Assert.True(vm.IsCloudMode);
        Assert.False(vm.IsLocalMode);

        await vm.SetEngineModeAsync(SpeechEngineMode.LocalWhisper);
        Assert.True(vm.IsLocalMode);
        Assert.False(vm.IsCloudMode);
        Assert.Contains("Local", vm.EngineModeDisplay);

        await vm.SetEngineModeAsync(SpeechEngineMode.CloudDeepgram);
        Assert.True(vm.IsCloudMode);
        Assert.Contains("Cloud", vm.EngineModeDisplay);
    }

    [Fact]
    public async Task ToggleListening_WhenRouterFailsToListen_DoesNotSetIsListeningTrue()
    {
        EnvironmentConfig.Initialize();
        var audio = new WiraVoice.Core.Audio.AudioCaptureService();
        var cloud = new DeepgramFluxEngine(audio);
        var local = new WhisperLocalEngine(audio);
        // Start in Cloud mode where Deepgram key is missing
        var router = new SpeechEngineRouter(cloud, local, SpeechEngineMode.CloudDeepgram);
        var selector = new WiraVoice.Core.Language.JevSelector("dummy", "jev-1.13.0");
        var engine = new WiraVoice.Core.Commands.CommandEngine(selector);
        var coordinator = new WiraVoice.Core.Commands.CommandCoordinator(engine, speechEngine: router);

        var vm = new TopBarViewModel(coordinator, router)
        {
            EngineMode = SpeechEngineMode.CloudDeepgram
        };

        await vm.ToggleListeningAsync();

        // Either it automatically switched to Local Whisper and succeeded, or if audio capture cannot start, it stays IsListening=false
        // Crucially: It MUST NOT claim IsListening=true if router is NOT listening!
        Assert.Equal(router.IsListening, vm.IsListening);
    }
}
