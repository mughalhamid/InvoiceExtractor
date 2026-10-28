using System.Text.Json.Nodes;

namespace InvoiceExtractor.Ai;

/// <summary>
/// The only thing the rest of the app knows about "AI".
/// Today it's Ollama; tomorrow it could be Azure OpenAI or another provider.
/// Swapping providers = writing one new class that implements this interface.
/// </summary>
public interface IVisionModelClient
{
    /// <param name="onChunk">Called for every piece of text as the model generates it (streaming).</param>
    Task<ModelResponse> ExtractAsync(
        string prompt,
        byte[] image,
        JsonNode jsonSchema,
        Action<StreamChunk>? onChunk = null,
        CancellationToken ct = default);

    /// <summary>Frees the model's memory (RAM/VRAM) once we're done.</summary>
    Task UnloadAsync();
}

public record StreamChunk(string Text, bool IsThinking);

public record ModelResponse(
    string Json,               // the model's answer, shaped by the schema
    string Model,
    TimeSpan Duration,
    int? PromptTokens,
    int? OutputTokens,
    double? TokensPerSecond,
    string? DoneReason)        // "stop" = finished normally, "length" = hit the max-token cap
{
    public bool WasTruncated => DoneReason == "length";
}
