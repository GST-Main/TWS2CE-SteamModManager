using System.Text;
using System.Text.RegularExpressions;

namespace TWS2CESteamModManager.Core;

public sealed record ScriptDiagnostic(int LineNumber, string Line, string Message);
public sealed record UserScriptParseResult(IReadOnlyList<string> Mods, IReadOnlyList<ScriptDiagnostic> Diagnostics);
public sealed record UserScriptDocument(string FilePath, string Content, Encoding Encoding,
    bool HasByteOrderMark, bool WasCreated, IReadOnlyList<string> Mods, IReadOnlyList<ScriptDiagnostic> Diagnostics);

public static class UserScriptService
{
    private static readonly Regex ModLine = new(
        "^\\s*mod\\s+(?:\"(?<quotedFile>[^\"]+)\"|(?<file>[^;\"]+?))\\s*;\\s*(?<comment>(?://|#).*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ModPrefix = new("^mod(?:\\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static UserScriptDocument LoadOrCreate(string appDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataPath);
        string folder = Path.GetFullPath(appDataPath);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Select an existing game app-data folder first.");
        string path = GameConstants.GetUserScriptPath(folder);
        bool created = false;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileStream? newFile = null;
            try
            {
                newFile = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process created the script. Load it without replacing it.
            }
            if (newFile is not null)
            {
                using (newFile)
                {
                    // The game's generated script uses UTF-16 LE with a BOM.
                    newFile.Write(Encoding.Unicode.GetPreamble());
                }
                created = true;
            }
        }

        byte[] bytes = File.ReadAllBytes(path);
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: true);
        string content = reader.ReadToEnd();
        Encoding encoding = reader.CurrentEncoding;
        byte[] preamble = encoding.GetPreamble();
        bool hasBom = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble);
        UserScriptParseResult parsed = Parse(content);
        return new UserScriptDocument(path, content, encoding, hasBom, created, parsed.Mods, parsed.Diagnostics);
    }

    /// <summary>Reads one mod declaration per line, retaining order, case, and duplicate entries.</summary>
    public static UserScriptParseResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var mods = new List<string>();
        var diagnostics = new List<ScriptDiagnostic>();
        using var reader = new StringReader(content);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;
            Match match = ModLine.Match(line);
            string name = match.Success ? FileNameFromMatch(match) : "";
            if (match.Success && IsPackFileName(name))
                mods.Add(name);
            else if (ModPrefix.IsMatch(trimmed))
                diagnostics.Add(new ScriptDiagnostic(lineNumber, line, "Expected mod <filename>.pack; with a filename, not a path."));
            // Other game settings are retained in Content and are not interpreted as mods.
        }
        return new UserScriptParseResult(mods.AsReadOnly(), diagnostics.AsReadOnly());
    }

    public static void UpdateEnabledMods(string appDataPath, IEnumerable<string> enabledFileNames)
    {
        UserScriptDocument existing = LoadOrCreate(appDataPath);
        byte[] bytes = RenderEnabledMods(existing, enabledFileNames);
        if (!File.ReadAllBytes(existing.FilePath).AsSpan().SequenceEqual(bytes))
            AtomicFile.Write(existing.FilePath, bytes);
    }

    /// <summary>Replaces valid mod declarations, retaining other settings, comments, encoding and BOM.</summary>
    public static byte[] RenderEnabledMods(UserScriptDocument existing, IEnumerable<string> enabledFileNames)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(enabledFileNames);
        string[] names = enabledFileNames.ToArray();
        if (names.Any(name => !IsPackFileName(name)))
            throw new ArgumentException("Enabled mods must be pack filenames, not paths.", nameof(enabledFileNames));

        string newline = Regex.Match(existing.Content, "\\r\\n|\\r|\\n").Value;
        if (newline.Length == 0) newline = "\r\n";
        string block = string.Concat(names.Select(name =>
            $"mod {(name.Any(char.IsWhiteSpace) ? "\"" + name + "\"" : name)};{newline}"));
        var content = new StringBuilder();
        bool inserted = false;
        foreach (Match segment in Regex.Matches(existing.Content, "[^\\r\\n]+(?:\\r\\n|\\r|\\n)?|\\r\\n|\\r|\\n"))
        {
            string line = segment.Value.TrimEnd('\r', '\n');
            Match declaration = ModLine.Match(line);
            if (declaration.Success && IsPackFileName(FileNameFromMatch(declaration)))
            {
                if (!inserted) { content.Append(block); inserted = true; }
                string comment = declaration.Groups["comment"].Value;
                if (comment.Length > 0)
                    content.Append(comment).Append(segment.Value[line.Length..]);
            }
            else content.Append(segment.Value);
        }
        if (!inserted && block.Length > 0)
        {
            if (content.Length > 0 && content[^1] is not ('\r' or '\n')) content.Append(newline);
            content.Append(block);
        }
        byte[] body = existing.Encoding.GetBytes(content.ToString());
        byte[] preamble = existing.HasByteOrderMark ? existing.Encoding.GetPreamble() : Array.Empty<byte>();
        return preamble.Concat(body).ToArray();
    }

    internal static bool IsPackFileName(string? name) =>
        name is not null && name.Length > ".pack".Length && name.EndsWith(".pack", StringComparison.OrdinalIgnoreCase)
        && !name.Any(character => char.IsControl(character) || "<>:\"/\\|?*;".Contains(character));

    private static string FileNameFromMatch(Match match) => match.Groups["quotedFile"].Success
        ? match.Groups["quotedFile"].Value : match.Groups["file"].Value.Trim();
}
