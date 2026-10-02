using System.Text.RegularExpressions;

namespace OnVoyage.Creators.Infrastructure.GeoAssociation;

/// <summary>
/// The versioned prompt file <c>prompts/geotag-places.md</c> (§8.10): header (<c>id</c>, <c>version</c>, <c>model</c> setting, <c>schema</c>) and the
/// sections « Système », « Utilisateur » and « Schéma ». Same format as Factory's prompts.
/// </summary>
internal sealed partial record PlaceMentionPrompt(string Id, int Version, string ModelSetting, string SchemaName, string SystemPrompt, string UserPrompt, string SchemaJson)
{
    [GeneratedRegex("^## (?<name>.+?)\\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Heading();

    public static PlaceMentionPrompt LoadEmbedded()
    {
        using var stream = typeof(PlaceMentionPrompt).Assembly.GetManifestResourceStream("prompts/geotag-places.md")
            ?? throw new InvalidOperationException("The prompt 'geotag-places.md' is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static PlaceMentionPrompt Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal) || normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal) is not (> 0 and var end))
        {
            throw new FormatException("A prompt starts with a '---' header and closes it.");
        }

        var header = normalized[4..end].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
        var body = normalized[(end + 5)..];
        var matches = Heading().Matches(body);
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            sections[matches[i].Groups["name"].Value] = body[start..(i + 1 < matches.Count ? matches[i + 1].Index : body.Length)].Trim();
        }

        string Section(string name) => sections.TryGetValue(name, out var value) ? value : throw new FormatException($"The prompt has no '{name}' section.");
        var schema = Section("Schéma");
        return new PlaceMentionPrompt(header["id"], int.Parse(header["version"], System.Globalization.CultureInfo.InvariantCulture), header["model"], header["schema"], Section("Système"), Section("Utilisateur"), schema[schema.IndexOf('{', StringComparison.Ordinal)..(schema.LastIndexOf('}') + 1)]);
    }

    public (string System, string User) Render(IReadOnlyDictionary<string, string> values) =>
        (Fill(SystemPrompt, values), Fill(UserPrompt, values));

    private static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        values.Aggregate(template, (current, pair) => current.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal));
}
