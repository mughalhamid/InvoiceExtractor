using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceExtractor.Ai;

public record OllamaSettings(
    string Model,
    int ContextSize,      // total tokens the model can hold: image + prompt + answer
    int MaxOutputTokens,  // hard cap on the answer, so a looping model always stops
    bool Think,
    string KeepAlive);    // how long Ollama keeps the model in memory after a request

/// <summary>
/// Calls the local Ollama server (http://localhost:11434) with plain HttpClient,
/// so you can see exactly what goes over the wire. API: POST /api/chat
///
/// Uses streaming: Ollama sends one JSON line per generated token, so we can show
/// progress live and spot a model that is stuck repeating itself.
/// If we stop reading (Ctrl+C or timeout), the connection closes and Ollama stops generating.
/// </summary>
public sealed class OllamaVisionClient(HttpClient http, OllamaSettings settings) : IVisionModelClient
{
    public async Task<ModelResponse> ExtractAsync(
        string prompt, byte[] image, JsonNode jsonSchema,
        Action<StreamChunk>? onChunk = null, CancellationToken ct = default)
    {
        var request = BuildRequest(prompt, image, jsonSchema);
        try
        {
            return await StreamAsync(request, onChunk, ct);
        }
        catch (OllamaException ex) when (settings.Think && ex.Message.Contains("does not support thinking"))
        {
            // Some vision models (e.g. minicpm-v) have no thinking mode and reject the flag.
            request.Remove("think");
            return await StreamAsync(request, onChunk, ct);
        }
    }

    public async Task UnloadAsync()
    {
        // keep_alive = 0 tells Ollama to unload the model immediately.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var body = new JsonObject { ["model"] = settings.Model, ["keep_alive"] = 0 };
        try
        {
            using var _ = await http.PostAsJsonAsync("/api/generate", body, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Not critical: Ollama unloads idle models by itself after keep_alive expires.
        }
    }

    private JsonObject BuildRequest(string prompt, byte[] image, JsonNode jsonSchema) => new()
    {
        ["model"] = settings.Model,
        ["stream"] = true,
        ["keep_alive"] = settings.KeepAlive,
        // Structured output: Ollama constrains generation to this JSON schema.
        ["format"] = jsonSchema.DeepClone(),
        ["think"] = settings.Think,
        ["options"] = new JsonObject
        {
            ["temperature"] = 0,                          // extraction should be deterministic
            ["num_ctx"] = settings.ContextSize,
            ["num_predict"] = settings.MaxOutputTokens    // never generate forever
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

    private async Task<ModelResponse> StreamAsync(
        JsonObject request, Action<StreamChunk>? onChunk, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request)
        };

        // ResponseHeadersRead: start reading as soon as the first token arrives,
        // instead of waiting for the whole answer.
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new OllamaException($"Ollama returned {(int)response.StatusCode}: {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        int? promptTokens = null, outputTokens = null;
        long totalNs = 0, evalNs = 0;
        string? doneReason = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
                throw new OllamaException(err.GetString() ?? "Unknown Ollama error");

            if (root.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("thinking", out var th) && th.GetString() is { Length: > 0 } thinking)
                    onChunk?.Invoke(new StreamChunk(thinking, IsThinking: true));

                if (msg.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } text)
                {
                    content.Append(text);
                    onChunk?.Invoke(new StreamChunk(text, IsThinking: false));
                }
            }

            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                // The final line carries the statistics.
                promptTokens = root.TryGetProperty("prompt_eval_count", out var p) ? p.GetInt32() : null;
                outputTokens = root.TryGetProperty("eval_count", out var e) ? e.GetInt32() : null;
                totalNs = root.TryGetProperty("total_duration", out var t) ? t.GetInt64() : 0;
                evalNs = root.TryGetProperty("eval_duration", out var ed) ? ed.GetInt64() : 0;
                doneReason = root.TryGetProperty("done_reason", out var dr) ? dr.GetString() : null;
                break;
            }
        }

        return new ModelResponse(
            Json: content.ToString(),
            Model: settings.Model,
            Duration: TimeSpan.FromMilliseconds(totalNs / 1_000_000.0),
            PromptTokens: promptTokens,
            OutputTokens: outputTokens,
            TokensPerSecond: evalNs > 0 && outputTokens is > 0 ? outputTokens / (evalNs / 1e9) : null,
            DoneReason: doneReason);
    }
}

public sealed class OllamaException(string message) : Exception(message);
