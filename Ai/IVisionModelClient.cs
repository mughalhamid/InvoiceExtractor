using System.Text.Json.Nodes;

namespace InvoiceExtractor.Ai;

/// <summary>
/// The only thing the rest of the app knows about "AI".
/// Today it's Ollama; tomorrow it could be Azure OpenAI or another provider.
/// Swapping providers = writing one new class that implements this interface.
/// </summary>
public interface IVisionModelClient
{
    Task<ModelResponse> ExtractAsync(
        string prompt,
        byte[] image,
        JsonNode jsonSchema,
        CancellationToken ct = default);
}

public record ModelResponse(
    string Json,               // the model's answer, shaped by the schema
    string Model,
    TimeSpan Duration,
    int? PromptTokens,
    int? OutputTokens);
