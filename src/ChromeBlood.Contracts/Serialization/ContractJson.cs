using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeBlood.Contracts.Serialization;

public static class ContractJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new ContractFormatException($"Contract {typeof(T).Name} was null.");
        }
        catch (ContractFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ContractFormatException($"Contract {typeof(T).Name} is invalid JSON.", exception);
        }
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, Options)
                ?? throw new ContractFormatException($"Contract {typeof(T).Name} was null.");
        }
        catch (ContractFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ContractFormatException($"Contract {typeof(T).Name} is invalid JSON.", exception);
        }
    }

    public static byte[] SerializeUtf8<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            WriteIndented = true,
            NewLine = "\n"
        };
        options.Converters.Add(new FileAttributesJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
}
