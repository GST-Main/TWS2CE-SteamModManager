using System.Text;

namespace TWS2CESteamModManager.Core;

// Steam's libraryfolders.vdf and appmanifest files use text KeyValues, not JSON.
internal sealed record ValveValue(string? Text, Dictionary<string, ValveValue>? Children)
{
    public ValveValue? Get(string key) => Children?.GetValueOrDefault(key);
}

internal sealed class ValveKeyValues(string text)
{
    private int position;
    private readonly record struct Token(string Text, bool Symbol = false);

    public static ValveValue Parse(string text) => new ValveKeyValues(text).ReadObject(false, 0);

    private ValveValue ReadObject(bool braces, int depth)
    {
        if (depth > 32)
            throw new FormatException("Steam metadata is nested too deeply.");
        var children = new Dictionary<string, ValveValue>(StringComparer.OrdinalIgnoreCase);
        while (Next() is { } key)
        {
            if (key.Symbol)
            {
                if (braces && key.Text == "}")
                    return new ValveValue(null, children);
                throw new FormatException("Unexpected brace in Steam metadata.");
            }
            Token value = Next() ?? throw new FormatException("Missing value in Steam metadata.");
            children[key.Text] = value switch
            {
                { Symbol: true, Text: "{" } => ReadObject(true, depth + 1),
                { Symbol: true } => throw new FormatException("Missing value before closing brace."),
                _ => new ValveValue(value.Text, null)
            };
        }
        if (braces)
            throw new FormatException("Unclosed object in Steam metadata.");
        return new ValveValue(null, children);
    }

    private Token? Next()
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]) || text[position] == '\uFEFF') { position++; continue; }
            if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }
            break;
        }
        if (position == text.Length) return null;
        char first = text[position++];
        if (first is '{' or '}') return new Token(first.ToString(), true);
        if (first == '"')
        {
            var value = new StringBuilder();
            while (position < text.Length)
            {
                char current = text[position++];
                if (current == '"') return new Token(value.ToString());
                if (current == '\\' && position < text.Length && text[position] is '\\' or '"')
                    current = text[position++];
                value.Append(current);
            }
            throw new FormatException("Unclosed string in Steam metadata.");
        }
        int start = position - 1;
        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}'))
            position++;
        return new Token(text[start..position]);
    }
}
