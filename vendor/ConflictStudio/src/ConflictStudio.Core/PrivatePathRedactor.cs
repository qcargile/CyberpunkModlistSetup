using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConflictStudio.Core;

public static class PrivatePathRedactor
{
    private static readonly Regex QuotedPath = new("(?i)(['\"])(?:[a-z]:[\\\\/]|\\\\\\\\).*?\\1", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnquotedPath = new("(?i)(?<![a-z0-9_])(?:[a-z]:[\\\\/]|\\\\\\\\).*?(?=:\\s|[\\r\\n\\t\"']|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return UnquotedPath.Replace(QuotedPath.Replace(value, "[private path]"), "[private path]");
    }

    public static string RelativeLabel(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Path.IsPathRooted(value) ? Path.GetFileName(value) : value;
    }

    public static T RedactObject<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        JsonNode node = JsonSerializer.SerializeToNode(value) ?? throw new JsonException("The support data could not be serialized.");
        RedactNode(node);
        return node.Deserialize<T>() ?? throw new JsonException("The support data could not be restored after privacy filtering.");
    }

    private static void RedactNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (KeyValuePair<string, JsonNode?> property in jsonObject.ToArray())
            {
                if (property.Value is JsonValue value && value.TryGetValue(out string? text)) jsonObject[property.Key] = Redact(text);
                else if (property.Value is not null) RedactNode(property.Value);
            }
            return;
        }

        if (node is not JsonArray jsonArray) return;
        for (int index = 0; index < jsonArray.Count; index++)
        {
            if (jsonArray[index] is JsonValue value && value.TryGetValue(out string? text)) jsonArray[index] = Redact(text);
            else if (jsonArray[index] is not null) RedactNode(jsonArray[index]!);
        }
    }
}
