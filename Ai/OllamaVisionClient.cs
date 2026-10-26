using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceExtractor.Ai;

/// <summary>
/// Calls the local Ollama server (http://localhost:11434) with plain HttpClient,
/// so you can see exactly what goes over the wire. API: POST /api/chat
/// </summary>
public sealed class OllamaVisionClient(HttpClient http, string model, int contextSize, bool think)
    : IVisionModelClient
{
    public async Task<ModelResponse> ExtractAsync(
        string prompt, byte[] image, JsonNode jsonSchema, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            // Structured output: Ollama constrains generation to this JSON schema.
            ["format"] = jsonSchema.DeepClone(),
            // Thinking models reason before answering; slower, sometimes more accurate.
            ["think"] = think,
            ["options"] = new JsonObject
            {
                ["temperature"] = 0,          // extraction should be deterministic
                ["num_ctx"] = contextSize     // images use many tokens; default context is too small
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt,
                    ["images"] = new JsonArray { Convert.ToBase64String(image) }
                }
            }
        };

        var (response, body) = await SendAsync(request, ct);

        // Some vision models (e.g. minicpm-v) have no thinking mode and reject the "think" flag.
        if (!response.IsSuccessStatusCode && body.Contains("does not support thinking"))
        {
            response.Dispose();
            request.Remove("think");
            (response, body) = await SendAsync(request, ct);
        }
        using var _ = response;

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var content = root.GetProperty("message").GetProperty("content").GetString() ?? "";
        var durationNs = root.TryGetProperty("total_duration", out var d) ? d.GetInt64() : 0;

        return new ModelResponse(
            Json: content,
            Model: model,
            Duration: TimeSpan.FromMilliseconds(durationNs / 1_000_000.0),
            PromptTokens: root.TryGetProperty("prompt_eval_count", out var p) ? p.GetInt32() : null,
            OutputTokens: root.TryGetProperty("eval_count", out var e) ? e.GetInt32() : null);
    }

    private async Task<(HttpResponseMessage Response, string Body)> SendAsync(JsonObject request, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/chat", request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return (response, body);
    }
}
