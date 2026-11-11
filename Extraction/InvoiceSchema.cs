using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using InvoiceExtractor.Models;

namespace InvoiceExtractor.Extraction;

/// <summary>
/// Turns the C# records into a JSON schema, so the records stay the single source of truth.
/// </summary>
public static class InvoiceSchema
{
    // PascalCase property names: the JSON keys match the C# records exactly.
    // Enums are written as their names ("FullStrikeout"), so the schema lists them as allowed values.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Maximum length per list. Ollama turns the schema into a grammar, so the model
    /// physically cannot add a 16th handwritten note: this is what stops repetition loops.
    /// Generous for real invoices, tight enough to stop runaway output.
    /// </summary>
    private static readonly Dictionary<string, int> MaxItems = new()
    {
        ["AddressBlocks"] = 6,
        ["Lines"] = 8,
        ["Headers"] = 15,
        ["Rows"] = 60,          // item table and VAT summary rows
        ["Cells"] = 15,
        ["Stamps"] = 5,
        ["HandwrittenNotes"] = 15,
        ["Signatures"] = 6,
        ["OtherCharges"] = 5,
        ["Marks"] = MaxPenMarks
    };

    /// <summary>
    /// Pen-mark pass: most changes listed per stacked image. Kept small on purpose: with 20 the
    /// 4B model looped until it hit the token cap. Reaching it is flagged (there may be more).
    /// </summary>
    public const int MaxPenMarks = 8;

    /// <summary>Schema for the full-page pass.</summary>
    public static JsonNode Build() => Build(typeof(InvoiceExtraction));

    /// <summary>
    /// Schema for the pen-mark pass. Column may only be one of this invoice's printed headers
    /// (or null), and RowNumber only 1..rowCount, so the model cannot invent a column or a row.
    /// </summary>
    public static JsonNode BuildPenMarks(IEnumerable<string> headers, int rowCount)
    {
        var schema = Build(typeof(PenMarkReading));
        if (schema["properties"]?["Marks"]?["items"]?["properties"] is not JsonObject props) return schema;

        var allowed = new JsonArray();
        foreach (var h in headers.Distinct()) allowed.Add(h);
        allowed.Add(null);
        if (props["Column"] is JsonObject column) column["enum"] = allowed;

        if (props["RowNumber"] is JsonObject rowNumber)
        {
            rowNumber["minimum"] = 1;
            rowNumber["maximum"] = Math.Max(1, rowCount);
        }
        return schema;
    }

    private static JsonNode Build(Type type)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (context, node) =>
            {
                if (context.PropertyInfo is { } property &&
                    MaxItems.TryGetValue(property.Name, out var max) &&
                    node is JsonObject obj)
                {
                    obj["maxItems"] = max;
                }
                return node;
            }
        };

        var schema = JsonOptions.GetJsonSchemaAsNode(type, exporterOptions);
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
