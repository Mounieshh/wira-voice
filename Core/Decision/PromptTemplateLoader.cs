using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WiraVoice.Core.Decision;

/// <summary>
/// Loads and expands prompt templates from language.json.
/// </summary>
public sealed class PromptTemplateLoader
{
    private readonly Dictionary<string, string> _shared = new();
    private readonly Dictionary<string, string> _prompts = new();
    private bool _loaded = false;

    public void Load(string? customPath = null)
    {
        if (_loaded) return;

        string path = customPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", "Prompts", "language.json");
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), "Core", "Prompts", "language.json");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Prompt template file not found: {path}");
        }

        string json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("shared", out var sharedEl))
        {
            foreach (var prop in sharedEl.EnumerateObject())
            {
                _shared[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        if (root.TryGetProperty("prompts", out var promptsEl))
        {
            foreach (var prop in promptsEl.EnumerateObject())
            {
                _prompts[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        _loaded = true;
    }

    public string GetPrompt(string key)
    {
        if (!_loaded) Load();

        if (!_prompts.TryGetValue(key, out var template))
        {
            return string.Empty;
        }

        // Expand {{shared_key}}
        string expanded = template;
        foreach (var (k, v) in _shared)
        {
            expanded = expanded.Replace($"{{{{{k}}}}}", v);
        }

        return expanded;
    }
}
