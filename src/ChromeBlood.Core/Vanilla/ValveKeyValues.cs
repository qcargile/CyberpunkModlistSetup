namespace ChromeBlood.Core.Vanilla;

internal sealed class ValveKeyValues
{
    private readonly Dictionary<string, object> _entries = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _entries.Count;

    public string Value(string key) => _entries.TryGetValue(key, out object? value) && value is string text
        ? text : throw new InvalidDataException("The Steam manifest is missing a required value.");
    public string OptionalValue(string key) => !_entries.ContainsKey(key) ? string.Empty : Value(key);
    public ValveKeyValues Object(string key) => _entries.TryGetValue(key, out object? value) && value is ValveKeyValues child
        ? child : throw new InvalidDataException("The Steam manifest is missing a required section.");
    public ValveKeyValues? OptionalObject(string key) => !_entries.ContainsKey(key) ? null : Object(key);

    public static ValveKeyValues Parse(string text)
    {
        if (text is null || text.Length > 1048576) throw new InvalidDataException("The Steam manifest exceeds its size limit.");
        int offset = 0;
        ValveKeyValues result = ReadObject(false, 0);
        return result;

        ValveKeyValues ReadObject(bool nested, int depth)
        {
            if (depth > 16) throw new InvalidDataException("The Steam manifest is nested too deeply.");
            ValveKeyValues node = new();
            while (true)
            {
                SkipWhitespace();
                if (offset == text.Length)
                {
                    if (nested) throw new InvalidDataException("The Steam manifest section is incomplete.");
                    return node;
                }
                if (text[offset] == '}')
                {
                    if (!nested) throw new InvalidDataException("The Steam manifest has an unexpected closing brace.");
                    offset++;
                    return node;
                }
                string key = Quoted();
                SkipWhitespace();
                object value;
                if (offset < text.Length && text[offset] == '{')
                {
                    offset++;
                    value = ReadObject(true, depth + 1);
                }
                else value = Quoted();
                if (!node._entries.TryAdd(key, value)) throw new InvalidDataException("The Steam manifest contains duplicate keys.");
            }
        }

        void SkipWhitespace()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset]) || text[offset] == '\uFEFF') offset++;
                else if (offset + 1 < text.Length && text[offset] == '/' && text[offset + 1] == '/')
                {
                    while (offset < text.Length && text[offset] != '\n') offset++;
                }
                else break;
            }
        }

        string Quoted()
        {
            SkipWhitespace();
            if (offset >= text.Length || text[offset++] != '"') throw new InvalidDataException("The Steam manifest requires a quoted token.");
            System.Text.StringBuilder value = new();
            while (offset < text.Length)
            {
                char current = text[offset++];
                if (current == '"') return value.ToString();
                if (current == '\\')
                {
                    if (offset >= text.Length || text[offset] is not ('\\' or '"')) throw new InvalidDataException("The Steam manifest has an invalid escape.");
                    current = text[offset++];
                }
                if (char.IsControl(current)) throw new InvalidDataException("The Steam manifest token contains a control character.");
                value.Append(current);
            }
            throw new InvalidDataException("The Steam manifest token is incomplete.");
        }
    }
}
