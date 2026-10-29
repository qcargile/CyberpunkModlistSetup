using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromeBlood.Contracts.Serialization;

internal sealed class FileAttributesJsonConverter : JsonConverter<FileAttributes>
{
    private const uint ReadOnly = 0x00000001;
    private const uint Hidden = 0x00000002;
    private const uint System = 0x00000004;
    private const uint Directory = 0x00000010;
    private const uint Archive = 0x00000020;
    private const uint Device = 0x00000040;
    private const uint Normal = 0x00000080;
    private const uint Temporary = 0x00000100;
    private const uint SparseFile = 0x00000200;
    private const uint ReparsePoint = 0x00000400;
    private const uint Compressed = 0x00000800;
    private const uint Offline = 0x00001000;
    private const uint NotContentIndexed = 0x00002000;
    private const uint Encrypted = 0x00004000;
    private const uint IntegrityStream = 0x00008000;
    private const uint Virtual = 0x00010000;
    private const uint NoScrubData = 0x00020000;
    private const uint EaOrRecallOnOpen = 0x00040000;
    private const uint Pinned = 0x00080000;
    private const uint Unpinned = 0x00100000;
    private const uint RecallOnDataAccess = 0x00400000;
    private const uint AllowedNativeMask = ReadOnly | Hidden | System | Directory | Archive | Device | Normal | Temporary | SparseFile | ReparsePoint | Compressed | Offline | NotContentIndexed | Encrypted | IntegrityStream | Virtual | NoScrubData | EaOrRecallOnOpen | Pinned | Unpinned | RecallOnDataAccess;
    private static readonly uint BclMask = Enum.GetValues<FileAttributes>().Aggregate(0u, static (mask, value) => mask | unchecked((uint)(int)value));

    public override FileAttributes Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string value = reader.GetString() ?? throw new JsonException("File attributes cannot be null.");
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '+' or '-' or >= '0' and <= '9' || !Enum.TryParse(value, true, out FileAttributes attributes))
                throw new JsonException("File attributes must use named BCL values.");
            uint raw = unchecked((uint)(int)attributes);
            if ((raw & ~BclMask) != 0) throw new JsonException("File attributes contain an unknown named value.");
            return attributes;
        }

        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt32(out uint native))
            throw new JsonException("File attributes must be a named string or native integer.");
        if ((native & ~AllowedNativeMask) != 0 || (native & ~BclMask) == 0)
            throw new JsonException("File attributes contain unsupported native bits.");
        return (FileAttributes)(int)native;
    }

    public override void Write(Utf8JsonWriter writer, FileAttributes value, JsonSerializerOptions options)
    {
        uint raw = unchecked((uint)(int)value);
        if ((raw & ~AllowedNativeMask) != 0) throw new JsonException("File attributes contain unsupported native bits.");
        if ((raw & ~BclMask) != 0)
        {
            writer.WriteNumberValue(raw);
            return;
        }

        string named = value.ToString();
        if (named.Length == 0 || named.All(character => char.IsAsciiDigit(character))) throw new JsonException("File attributes do not have a canonical named representation.");
        string canonical = string.Join(", ", named.Split(", ", StringSplitOptions.None).Select(JsonNamingPolicy.CamelCase.ConvertName));
        writer.WriteStringValue(canonical);
    }
}
