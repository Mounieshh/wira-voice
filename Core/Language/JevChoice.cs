using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Diagnostics;

namespace WiraVoice.Core.Language;

public readonly record struct JevOption(string Id, string Description);

public sealed class JevQuestion
{
    public string Instructions { get; }
    public List<JevOption> Options { get; }
    public string? NoneDescription { get; set; }
    public int OptionBudget { get; set; } = 254;
    public Dictionary<string, string> ObservedControls { get; set; } = [];
    public string? Key { get; set; }

    public JevQuestion(string instructions, IEnumerable<JevOption> options, string? noneDescription = null,
                       int optionBudget = 254, Dictionary<string, string>? observedControls = null, string? key = null)
    {
        Instructions = instructions;
        Options = options.ToList();
        NoneDescription = noneDescription;
        OptionBudget = optionBudget;
        ObservedControls = observedControls ?? [];
        Key = key;
    }
}

public sealed class JevJudgments
{
    public List<string?> Ids { get; }
    public int Requests { get; set; } = 0;
    public List<List<string>> Shortlists { get; set; } = [];
    public Dictionary<string, string> Answers { get; set; } = [];

    public JevJudgments(IEnumerable<string?> ids, List<List<string>>? shortlists = null)
    {
        Ids = ids.ToList();
        Shortlists = shortlists ?? [];
    }

    public string? Answer(string key) => Answers.TryGetValue(key, out var val) ? val : null;
}

public abstract class JevException : Exception
{
    protected JevException(string message) : base(message) { }

    public sealed class Invalid : JevException
    {
        public Invalid(string detail) : base(detail) { }
    }

    public sealed class ContextLimit : JevException
    {
        public ContextLimit() : base("Jev context still exceeds its limit after splitting the options.") { }
    }

    public sealed class Service : JevException
    {
        public int StatusCode { get; }
        public Service(int code) : base($"Jev returned HTTP {code}.") { StatusCode = code; }
    }
}

public sealed class JevSelector
{
    public string ApiKey { get; }
    public string Model { get; }
    public Uri Endpoint { get; set; }
    public string? TraceDirectory { get; set; }
    public ModelUsageTracker Usage { get; } = new();
    public JevCosts? Costs { get; set; }
    public HttpClient HttpClient { get; }

    public JevSelector(string apiKey, string model = "jev-1.13.0", HttpClient? httpClient = null, Uri? endpoint = null)
    {
        ApiKey = apiKey;
        Model = model;
        Endpoint = endpoint ?? new Uri("https://api.typesafe.ai/v1/systemone");
        HttpClient = httpClient ?? new HttpClient();
    }

    private readonly record struct Answer(string Choice, Dictionary<string, double> Probabilities);

    /// <summary>
    /// Independent questions share state and one request. No question consumes another's answer.
    /// Port of Computah's JevSelector.judge.
    /// </summary>
    public async Task<JevJudgments> JudgeAsync(Dictionary<string, object?> state, List<JevQuestion> questions, CancellationToken ct = default)
    {
        var keys = questions.Where(q => !string.IsNullOrEmpty(q.Key)).Select(q => q.Key!).ToList();
        if (keys.Distinct().Count() != keys.Count)
        {
            throw new JevException.Invalid("Duplicate question keys.");
        }

        var callUsage = new ModelUsageTracker();
        var result = await JudgeInternalAsync(state, questions, callUsage, ct).ConfigureAwait(false);

        result.Answers = questions.Zip(result.Ids, (q, ans) => (q.Key, ans))
            .Where(t => !string.IsNullOrEmpty(t.Key) && t.ans != null)
            .ToDictionary(t => t.Key!, t => t.ans!);

        result.Requests = callUsage.Snapshot.Requests;
        return result;
    }

    private async Task<JevJudgments> JudgeInternalAsync(
        Dictionary<string, object?> state,
        List<JevQuestion> questions,
        ModelUsageTracker callUsage,
        CancellationToken ct)
    {
        if (questions.Count == 0) return new JevJudgments([]);

        foreach (var q in questions)
        {
            if (q.Options.Count == 0 || q.Options.Select(o => o.Id).Distinct().Count() != q.Options.Count ||
                q.Options.Any(o => o.Id == "none_here"))
            {
                throw new JevException.Invalid("Invalid semantic question options.");
            }
            if (q.OptionBudget is < 1 or > 254)
            {
                throw new JevException.Invalid("Invalid choice budget.");
            }
        }

        // 254-option budgeting chunking
        if (questions.Any(q => q.Options.Count > q.OptionBudget))
        {
            var expanded = new List<JevQuestion>();
            var groups = new List<List<int>>();

            foreach (var question in questions)
            {
                var indexes = new List<int>();
                for (int start = 0; start < question.Options.Count; start += question.OptionBudget)
                {
                    indexes.Add(expanded.Count);
                    var count = Math.Min(question.OptionBudget, question.Options.Count - start);
                    var subset = question.Options.GetRange(start, count);
                    var partKey = question.Key != null ? $"{question.Key}_part{indexes.Count - 1}" : null;
                    var noneDesc = (question.NoneDescription ?? "No offered answer applies.") +
                        " This question shows one subset. The answer may be in another subset; do not select an inapplicable option.";

                    expanded.Add(new JevQuestion(
                        question.Instructions,
                        subset,
                        noneDesc,
                        question.OptionBudget,
                        question.ObservedControls,
                        partKey));
                }
                groups.Add(indexes);
            }

            var first = await JudgeInternalAsync(state, expanded, callUsage, ct).ConfigureAwait(false);
            var ids = new string?[questions.Count];
            var shortlists = new List<string>[questions.Count];
            var finalists = new List<JevQuestion>();
            var destinations = new List<int>();

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Count == 1)
                {
                    ids[i] = first.Ids[group[0]];
                    shortlists[i] = first.Shortlists[group[0]];
                }
                else if (group.All(idx => first.Ids[idx] == null))
                {
                    // Preserve unanimous abstention
                    ids[i] = null;
                    shortlists[i] = group.SelectMany(idx => first.Shortlists[idx]).ToList();
                }
                else
                {
                    var retained = new HashSet<string>(group.SelectMany(idx => first.Shortlists[idx]));
                    finalists.Add(new JevQuestion(
                        questions[i].Instructions,
                        questions[i].Options.Where(o => retained.Contains(o.Id)),
                        questions[i].NoneDescription,
                        questions[i].OptionBudget,
                        questions[i].ObservedControls,
                        questions[i].Key));
                    destinations.Add(i);
                }
            }

            var finalResult = await JudgeInternalAsync(state, finalists, callUsage, ct).ConfigureAwait(false);
            for (int i = 0; i < destinations.Count; i++)
            {
                var dest = destinations[i];
                ids[dest] = finalResult.Ids[i];
                shortlists[dest] = finalResult.Shortlists[i];
            }

            return new JevJudgments(ids, shortlists.ToList());
        }

        var wireKeys = questions.Select((q, i) => q.Key ?? $"q{i}").ToList();
        if (wireKeys.Distinct().Count() != wireKeys.Count)
        {
            throw new JevException.Invalid("Duplicate wire question keys.");
        }

        var wire = new Dictionary<string, object?>();
        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var criteria = new Dictionary<string, string>();
            foreach (var opt in q.Options)
            {
                criteria[opt.Id] = opt.Description;
            }
            criteria["none_here"] = q.NoneDescription ?? "Insufficient information, ambiguity, or no applicable offered answer.";

            wire[wireKeys[i]] = new Dictionary<string, object?>
            {
                ["type"] = "choice",
                ["instructions"] = q.Instructions,
                ["criteria"] = criteria
            };
        }

        var requestState = new Dictionary<string, object?>(state);
        var observedIds = new HashSet<string>();
        var observedControls = new List<string>();

        foreach (var q in questions)
        {
            foreach (var opt in q.Options)
            {
                if (q.ObservedControls.TryGetValue(opt.Id, out var obs) && observedIds.Add(opt.Id))
                {
                    observedControls.Add(obs);
                }
            }
        }

        if (observedControls.Count > 0)
        {
            requestState["available_controls"] = observedControls;
        }

        try
        {
            var answers = await SendAsync(requestState, wire, questions.Select(q => q.Options).ToList(), wireKeys, callUsage, ct).ConfigureAwait(false);
            var ids = answers.Select(a => a.Choice == "none_here" ? null : a.Choice).ToList();
            var shortlists = questions.Zip(answers, (q, a) =>
            {
                return q.Options
                    .OrderByDescending(o => a.Probabilities.GetValueOrDefault(o.Id, 0.0))
                    .Take(3)
                    .Select(o => o.Id)
                    .ToList();
            }).ToList();

            return new JevJudgments(ids, shortlists);
        }
        catch (JevException.ContextLimit)
        {
            if (questions.Count == 1)
            {
                var q = questions[0];
                if (q.Options.Count <= 6) throw;
                q.OptionBudget = (q.Options.Count + 1) / 2;
                return await JudgeInternalAsync(state, [q], callUsage, ct).ConfigureAwait(false);
            }

            var mid = questions.Count / 2;
            var left = await JudgeInternalAsync(state, questions.Take(mid).ToList(), callUsage, ct).ConfigureAwait(false);
            var right = await JudgeInternalAsync(state, questions.Skip(mid).ToList(), callUsage, ct).ConfigureAwait(false);

            return new JevJudgments(
                left.Ids.Concat(right.Ids).ToList(),
                left.Shortlists.Concat(right.Shortlists).ToList());
        }
    }

    private async Task<List<Answer>> SendAsync(
        Dictionary<string, object?> state,
        Dictionary<string, object?> questions,
        List<List<JevOption>> chunks,
        List<string> keys,
        ModelUsageTracker callUsage,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(ApiKey))
        {
            throw new JevException.Invalid("Add TYPESAFE_API_KEY (or OPENROUTER_API_KEY) to .env file.");
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["state"] = state,
            ["questions"] = questions
        };

        var redactedBody = SensitiveText.Json(body);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(redactedBody, jsonOptions);

        if (jsonBytes.Length >= 180_000)
        {
            throw new JevException.ContextLimit();
        }

        bool isOpenRouter = Endpoint.Host.Contains("openrouter.ai") || (!string.IsNullOrEmpty(ApiKey) && ApiKey.StartsWith("sk-or-"));
        Uri targetUri = isOpenRouter ? new Uri("https://openrouter.ai/api/alpha/decisions") : Endpoint;

        Console.WriteLine($"[Jev] Evaluating {questions.Count} question(s) via {(isOpenRouter ? "OpenRouter" : "TypeSafe")} ({Model})...");

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var costTicket = Costs?.BeginRequest();
            Usage.BeginRequest();
            callUsage.BeginRequest();

            using var request = new HttpRequestMessage(HttpMethod.Post, targetUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            if (isOpenRouter)
            {
                request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/wira-voice");
                request.Headers.TryAddWithoutValidation("X-Title", "Wira-Voice");
            }
            request.Content = new ByteArrayContent(jsonBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response;
            byte[] responseData;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                response = await HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                responseData = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt == 0) continue;
                Console.WriteLine($"[Jev] Request error: {ex.Message}");
                throw new JevException.Invalid($"Jev interpretation request failed after {attempt + 1} attempt(s): {ex.Message}");
            }

            int? reportedTokens = null;
            string? reportedModel = null;
            try
            {
                using var doc = JsonDocument.Parse(responseData);
                if (doc.RootElement.TryGetProperty("usage", out var u) &&
                    u.TryGetProperty("input_tokens", out var it))
                {
                    reportedTokens = it.GetInt32();
                }
                if (doc.RootElement.TryGetProperty("model", out var m))
                {
                    reportedModel = m.GetString();
                }
            }
            catch { }

            Costs?.Received(costTicket, reportedModel ?? Model, reportedTokens);
            Usage.Received(reportedTokens);
            callUsage.Received(reportedTokens);

            var status = (int)response.StatusCode;

            if (!string.IsNullOrEmpty(TraceDirectory))
            {
                try
                {
                    Directory.CreateDirectory(TraceDirectory);
                    var traceFile = Path.Combine(TraceDirectory, $"{Guid.NewGuid()}.json");
                    File.WriteAllBytes(traceFile, responseData);
                }
                catch { }
            }

            if (status != 200)
            {
                var bodyStr = Encoding.UTF8.GetString(responseData);
                Console.WriteLine($"[Jev] HTTP {status} error: {bodyStr}");
                if (status == 400 && bodyStr.Contains("max_tokens_exceeded"))
                {
                    throw new JevException.ContextLimit();
                }
                throw new JevException.Service(status);
            }

            try
            {
                var decoded = Decode(responseData, chunks, keys);
                Console.WriteLine($"[Jev] Judgment completed ({decoded.Count} answers resolved)");
                for (int k = 0; k < decoded.Count && k < keys.Count; k++)
                {
                    Console.WriteLine($"  [Jev Answer {keys[k]}]: {decoded[k].Choice}");
                }
                return decoded;
            }
            catch (JevException.Invalid detail)
            {
                if (attempt == 0) continue;
                Console.WriteLine($"[Jev] Invalid response: {detail.Message}");
                throw new JevException.Invalid($"Jev returned two invalid Choice replies. Last: {detail.Message}");
            }
        }

        throw new JevException.Invalid("Jev response validation did not finish.");
    }

    private static List<Answer> Decode(byte[] responseData, List<List<JevOption>> chunks, List<string> keys)
    {
        using var doc = JsonDocument.Parse(responseData);
        JsonElement root = doc.RootElement;

        // If wrapped in OpenRouter / OpenAI chat completion format
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentProp))
            {
                var contentStr = contentProp.GetString() ?? string.Empty;
                if (contentStr.Contains("```"))
                {
                    int start = contentStr.IndexOf('{');
                    int end = contentStr.LastIndexOf('}');
                    if (start >= 0 && end > start)
                    {
                        contentStr = contentStr.Substring(start, end - start + 1);
                    }
                }
                using var innerDoc = JsonDocument.Parse(contentStr);
                return DecodeAnswers(innerDoc.RootElement, chunks, keys);
            }
        }

        return DecodeAnswers(root, chunks, keys);
    }

    private static List<Answer> DecodeAnswers(JsonElement root, List<List<JevOption>> chunks, List<string> keys)
    {
        if (!root.TryGetProperty("answers", out var rawAnswers) ||
            rawAnswers.ValueKind != JsonValueKind.Object)
        {
            throw new JevException.Invalid("Jev response has missing answers.");
        }

        var answers = new List<Answer>();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var key = keys[i];
            var validIds = new HashSet<string>(chunk.Select(c => c.Id)) { "none_here" };

            if (!rawAnswers.TryGetProperty(key, out var raw) ||
                !raw.TryGetProperty("type", out var typeProp) ||
                typeProp.GetString() != "choice")
            {
                throw new JevException.Invalid($"Question {i} has no Choice answer.");
            }

            if (!raw.TryGetProperty("choice", out var choiceProp) ||
                choiceProp.GetString() is not { } choice ||
                !validIds.Contains(choice))
            {
                throw new JevException.Invalid($"Question {i} selected an unknown option.");
            }

            if (!raw.TryGetProperty("probabilities", out var probsProp) ||
                probsProp.ValueKind != JsonValueKind.Object)
            {
                throw new JevException.Invalid($"Question {i} omitted or added probability options.");
            }

            var probabilities = new Dictionary<string, double>();
            double sum = 0.0;
            double maxProb = double.MinValue;

            foreach (var prop in probsProp.EnumerateObject())
            {
                if (!validIds.Contains(prop.Name))
                {
                    throw new JevException.Invalid($"Question {i} omitted or added probability options.");
                }
                var p = prop.Value.GetDouble();
                if (double.IsNaN(p) || double.IsInfinity(p) || p is < 0 or > 1)
                {
                    throw new JevException.Invalid($"Question {i} returned invalid probabilities.");
                }
                probabilities[prop.Name] = p;
                sum += p;
                if (p > maxProb) maxProb = p;
            }

            if (probabilities.Count != validIds.Count)
            {
                throw new JevException.Invalid($"Question {i} omitted or added probability options.");
            }

            if (Math.Abs(sum - 1.0) >= 0.03)
            {
                // If sum is reasonably close, normalize to 1.0 instead of aborting the workflow
                if (sum >= 0.85 && sum <= 1.15 && sum > 0)
                {
                    foreach (var keyName in probabilities.Keys.ToList())
                    {
                        probabilities[keyName] /= sum;
                    }
                    sum = 1.0;
                }
                else
                {
                    throw new JevException.Invalid($"Question {i} returned invalid probabilities.");
                }
            }

            var choiceProb = probabilities.GetValueOrDefault(choice, 0.0);
            double recomputedMax = probabilities.Values.Max();
            if (choiceProb < recomputedMax - 0.000001)
            {
                throw new JevException.Invalid($"Question {i} selected a non-leading option.");
            }

            answers.Add(new Answer(choice, probabilities));
        }

        return answers;
    }
}
