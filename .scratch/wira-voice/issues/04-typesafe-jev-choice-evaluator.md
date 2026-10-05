# 04: TypeSafe Jev Choice Evaluator via OpenRouter

**What to build:**  
A decision client implementing the `IDecisionEngine` seam. Takes the user's spoken request and a `UiSceneSnapshot`, constructs a constrained multiple-choice prompt using `language.json`, queries `typesafe/jev-1.13` via OpenRouter, and returns an `ActionDecision` containing the selected action ID and sliced lexical text arguments.

**Blocked by:** 03: Foreground Window UI Inspector & Control Grounding

**Status:** completed

- [x] Implement `IDecisionEngine.cs` interface and `ActionDecision.cs` record
- [x] Implement `PromptTemplateLoader.cs` to load and expand `language.json` prompts
- [x] Implement `LexicalTokenizer.cs` to tokenize utterances with UTF-16 character offsets
- [x] Implement `SensitiveTextFilter.cs` to redact credentials and sensitive text before sending
- [x] Implement `JevOpenRouterClient.cs` using `HttpClient` to call OpenRouter `typesafe/jev-1.13`
- [x] Parse returned probability distributions and map selected choice to control ID
- [x] Unit test prompt payload generation and response parsing with mock HTTP handler
