using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using InvoiceExtractor.Models;

namespace InvoiceExtractor.Extraction;

/// <summary>
/// Turns the C# records into a JSON schema, so the records stay the single source of truth.
/// </summary>
public static class InvoiceSchema
{
    // PascalCase property names: the JSON keys match the C# records exactly.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static JsonNode Build()
    {
        var schema = JsonOptions.GetJsonSchemaAsNode(typeof(InvoiceExtraction));
        MakeAllPropertiesRequired(schema);
        return schema;
    }

    /// <summary>
    /// Forces the model to output every key (it can still use null where allowed).
    /// Without this, a model may silently skip fields it finds hard.
    /// </summary>
    private static void MakeAllPropertiesRequired(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject props)
                    obj["required"] = new JsonArray(props.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
                foreach (var child in obj.Select(kv => kv.Value).ToList())
                    MakeAllPropertiesRequired(child);
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    MakeAllPropertiesRequired(child);
                break;
        }
    }
}
