using System;
using System.IO;
using DotNetEnv;

namespace WiraVoice.Core.Config;

/// <summary>
/// Thread-safe configuration manager for Wira Voice environment variables and paths.
/// </summary>
public static class EnvironmentConfig
{
    private static bool _loaded = false;
    private static readonly object _lock = new();

    public static string DeepgramApiKey { get; private set; } = string.Empty;
    public static string JevProvider { get; private set; } = "openrouter";
    public static string OpenRouterApiKey { get; private set; } = string.Empty;
    public static string TypeSafeApiKey { get; private set; } = string.Empty;
    public static string WhisperModelPath { get; private set; } = "models/ggml-base.bin";

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_loaded) return;

            // Look for .env in current directory or project root
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string envInBase = Path.Combine(baseDir, ".env");
            string envInProject = Path.Combine(Directory.GetCurrentDirectory(), ".env");

            if (File.Exists(envInBase))
            {
                Env.Load(envInBase);
            }
            else if (File.Exists(envInProject))
            {
                Env.Load(envInProject);
            }
            else
            {
                // Fall back to standard search up directory tree
                Env.TraversePath().Load();
            }

            DeepgramApiKey = Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY") ?? string.Empty;
            JevProvider = Environment.GetEnvironmentVariable("JEV_PROVIDER") ?? "openrouter";
            OpenRouterApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? string.Empty;
            TypeSafeApiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") ?? string.Empty;
            WhisperModelPath = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH") ?? "models/ggml-base.bin";

            _loaded = true;
        }
    }
}
