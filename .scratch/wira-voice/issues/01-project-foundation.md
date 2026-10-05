# 01: Project Foundation & Environment Bootstrap

**What to build:**  
Install all core NuGet dependencies (`FlaUI.UIA3`, `NAudio`, `Whisper.net`, `DotNetEnv`), load `.env` variables cleanly on startup, copy the battle-tested `language.json` prompt templates from `computah` into `Core/Prompts/language.json`, and ensure the C# .NET 8 WPF project compiles cleanly with 0 errors.

**Blocked by:** None (can start immediately).

**Status:** completed

- [x] Add NuGet packages to `WiraVoice.csproj`: `FlaUI.UIA3`, `NAudio`, `Whisper.net`, `DotNetEnv`
- [x] Copy `language.json` from `d:/Vibe_/computah/Sources/ComputahCore/Prompts/language.json` into `d:/Vibe_/wira-voice/Core/Prompts/language.json`
- [x] Configure `language.json` as a copied resource in `WiraVoice.csproj`
- [x] Implement `EnvironmentConfig.cs` to safely load and validate `.env` keys (`DEEPGRAM_API_KEY`, `OPENROUTER_API_KEY`, `WHISPER_MODEL_PATH`)
- [x] Verify `dotnet restore` and `dotnet build` succeed with 0 errors
