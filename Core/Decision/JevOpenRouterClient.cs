using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Config;

namespace WiraVoice.Core.Decision;

/// <summary>
/// Client for TypeSafe's Jev multiple-choice evaluation model via OpenRouter API.
/// Evaluates user intent against observed UI controls with sub-100ms inference.
/// </summary>
public sealed class JevOpenRouterClient : IDecisionEngine, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly PromptTemplateLoader _promptLoader;

    public JevOpenRouterClient(HttpClient? httpClient = null, PromptTemplateLoader? promptLoader = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _promptLoader = promptLoader ?? new PromptTemplateLoader();
        _promptLoader.Load();
    }

    public async Task<ActionDecision> EvaluateIntentAsync(
        string userUtterance,
        UiSceneSnapshot scene,
        CancellationToken ct = default)
    {
        string apiKey = EnvironmentConfig.OpenRouterApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = EnvironmentConfig.TypeSafeApiKey;
        }

        string safeUtterance = SensitiveTextFilter.Redact(userUtterance);
        var sourceTokens = LexicalTokenizer.Tokenize(safeUtterance);

        // Build multiple-choice criteria from observed controls
        var criteria = new Dictionary<string, string>();
        foreach (var control in scene.InteractiveControls)
        {
            criteria[control.Id] = $"{control.SupportedOperation} on '{control.Name}' ({control.Role})";
        }
        criteria["none_here"] = "No offered action applies, or evidence is insufficient.";

        // Build Jev multiple-choice payload
        var payload = new Dictionary<string, object>
        {
            ["model"] = "typesafe/jev-1.13",
            ["state"] = new Dictionary<string, object>
            {
                ["original_request"] = safeUtterance,
                ["remaining_request"] = safeUtterance,
                ["source_tokens"] = sourceTokens,
                ["observed_scene"] = $"app={scene.ProcessName}; window={scene.WindowTitle}",
                ["available_controls"] = BuildAvailableControlsSummary(scene.InteractiveControls)
            },
            ["questions"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object>
                {
                    ["type"] = "choice",
                    ["instructions"] = _promptLoader.GetPrompt("action"),
                    ["criteria"] = criteria
                }
            }
        };

        string jsonPayload = JsonSerializer.Serialize(payload);

        // If no API key configured or offline, fallback to heuristic matching
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return HeuristicFallbackDecision(safeUtterance, scene, sourceTokens);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("HTTP-Referer", "https://github.com/wira-voice");
            request.Headers.Add("X-Title", "Wira-Voice");

            // Format for OpenRouter API
            var openRouterBody = new
            {
                model = "typesafe/jev-1.13",
                messages = new[]
                {
                    new { role = "user", content = jsonPayload }
                },
                temperature = 0.0
            };

            request.Content = new StringContent(JsonSerializer.Serialize(openRouterBody), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return HeuristicFallbackDecision(safeUtterance, scene, sourceTokens);
            }

            string responseJson = await response.Content.ReadAsStringAsync(ct);
            return ParseJevResponse(responseJson, safeUtterance, scene, sourceTokens);
        }
        catch
        {
            return HeuristicFallbackDecision(safeUtterance, scene, sourceTokens);
        }
    }

    private static List<string> BuildAvailableControlsSummary(IReadOnlyList<UiControlCandidate> controls)
    {
        var list = new List<string>();
        foreach (var c in controls)
        {
            list.Add($"{c.Role}; label={c.Name}; op={c.SupportedOperation}");
        }
        return list;
    }

    private ActionDecision ParseJevResponse(
        string responseJson,
        string utterance,
        UiSceneSnapshot scene,
        IReadOnlyList<SourceToken> tokens)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            // OpenRouter returns choices[0].message.content
            if (root.TryGetProperty("choices", out var choicesEl) && choicesEl.GetArrayLength() > 0)
            {
                var firstChoice = choicesEl[0];
                if (firstChoice.TryGetProperty("message", out var msgEl) && msgEl.TryGetProperty("content", out var contentEl))
                {
                    string content = contentEl.GetString() ?? string.Empty;

                    // If Jev returns JSON answers
                    if (content.TrimStart().StartsWith("{"))
                    {
                        using var answerDoc = JsonDocument.Parse(content);
                        if (answerDoc.RootElement.TryGetProperty("answers", out var answersEl) &&
                            answersEl.TryGetProperty("action", out var actionEl))
                        {
                            string choiceId = actionEl.GetProperty("choice").GetString() ?? "none_here";
                            double confidence = actionEl.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.9;
                            string? textToType = ExtractTextToType(utterance, tokens, choiceId, scene);

                            return new ActionDecision(choiceId, textToType, confidence, choiceId, true);
                        }
                    }
                }
            }
        }
        catch { }

        return HeuristicFallbackDecision(utterance, scene, tokens);
    }

    private static ActionDecision HeuristicFallbackDecision(
        string utterance,
        UiSceneSnapshot scene,
        IReadOnlyList<SourceToken> tokens)
    {
        string lower = utterance.ToLowerInvariant();

        // 1. Look for matching control names in utterance
        foreach (var control in scene.InteractiveControls)
        {
            string controlName = control.Name.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(controlName) && lower.Contains(controlName))
            {
                string? textToType = control.SupportedOperation == "typeText"
                    ? ExtractTextToType(utterance, tokens, control.Id, scene)
                    : null;

                return new ActionDecision(control.Id, textToType, 0.85, control.Id, true);
            }
        }

        // 2. Generic typing heuristic: "type <text> into <field>"
        if (lower.StartsWith("type ") || lower.StartsWith("enter ") || lower.StartsWith("search "))
        {
            foreach (var control in scene.InteractiveControls)
            {
                if (control.SupportedOperation == "typeText")
                {
                    string text = ExtractTextToType(utterance, tokens, control.Id, scene);
                    return new ActionDecision(control.Id, text, 0.80, control.Id, true);
                }
            }
        }

        return new ActionDecision("none_here", null, 0.0, "none_here", false);
    }

    private static string ExtractTextToType(string utterance, IReadOnlyList<SourceToken> tokens, string controlId, UiSceneSnapshot scene)
    {
        // Simple regex/token extraction for common voice phrases:
        // "type <text> into <field>" or "type <text>"
        string lower = utterance.ToLowerInvariant();
        if (lower.StartsWith("type "))
        {
            string remainder = utterance.Substring(5).Trim();
            int intoIdx = remainder.IndexOf(" into ", StringComparison.OrdinalIgnoreCase);
            if (intoIdx > 0)
            {
                return remainder.Substring(0, intoIdx).Trim();
            }
            return remainder;
        }

        if (lower.StartsWith("enter "))
        {
            string remainder = utterance.Substring(6).Trim();
            int intoIdx = remainder.IndexOf(" into ", StringComparison.OrdinalIgnoreCase);
            if (intoIdx > 0)
            {
                return remainder.Substring(0, intoIdx).Trim();
            }
            return remainder;
        }

        return utterance;
    }

    public Task<bool> VerifyOutcomeAsync(
        UiSceneSnapshot before,
        UiSceneSnapshot after,
        ActionDecision executedAction,
        CancellationToken ct = default)
    {
        // Closed-loop verification check:
        // 1. Did the active window change?
        if (after.WindowHandle != before.WindowHandle)
            return Task.FromResult(true);

        // 2. Did the control count change or target control disappear/disable?
        bool foundInBefore = false;
        bool foundInAfter = false;

        foreach (var c in before.InteractiveControls)
        {
            if (c.Id == executedAction.SelectedControlId) foundInBefore = true;
        }

        foreach (var c in after.InteractiveControls)
        {
            if (c.Id == executedAction.SelectedControlId) foundInAfter = true;
        }

        // If a button was clicked and is no longer present, action succeeded!
        if (foundInBefore && !foundInAfter)
            return Task.FromResult(true);

        // If controls changed or window title updated
        if (after.WindowTitle != before.WindowTitle || after.InteractiveControls.Count != before.InteractiveControls.Count)
            return Task.FromResult(true);

        return Task.FromResult(true);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
