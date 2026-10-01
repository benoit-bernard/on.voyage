using System.Reflection;
using System.Text.RegularExpressions;

namespace OnVoyage.Factory.Infrastructure.Llm;

/// <summary>
/// A versioned prompt file of <c>prompts/</c> (§8.10): YAML-style header (<c>id</c>, <c>version</c>, <c>model</c> setting, <c>schema</c>), then the
/// sections "Système", "Utilisateur" and "Schéma". The version is stored with every draft so a text can be traced to the prompt that made it.
/// </summary>
internal sealed partial record PromptTemplate(string Id, int Version, string ModelSetting, string SchemaName, string SystemPrompt, string UserPrompt, string SchemaJson)
{
    public const string TaxonomyPlaceholder = "__TAXONOMY__";

    [GeneratedRegex("^## (?<name>.+?)\\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    public static PromptTemplate LoadEmbedded(string fileName)
    {
        using var stream = typeof(PromptTemplate).Assembly.GetManifestResourceStream($"prompts/{fileName}")
            ?? throw new InvalidOperationException($"Prompt '{fileName}' is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static PromptTemplate Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new FormatException("A prompt starts with a '---' header.");
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException("The prompt header is not closed.");
        }

        var header = normalized[4..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);

        var body = normalized[(end + 5)..];
        var matches = Heading().Matches(body);
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var stop = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            sections[matches[i].Groups["name"].Value] = body[start..stop].Trim();
        }

        string Section(string name) => sections.TryGetValue(name, out var value) ? value : throw new FormatException($"The prompt has no '{name}' section.");

        var schema = Section("Schéma");
        var open = schema.IndexOf('{', StringComparison.Ordinal);
        var close = schema.LastIndexOf('}');
        return new PromptTemplate(
            header["id"],
            int.Parse(header["version"], System.Globalization.CultureInfo.InvariantCulture),
            header["model"],
            header["schema"],
            Section("Système"),
            Section("Utilisateur"),
            schema[open..(close + 1)]);
    }

    /// <summary>Replaces <c>{name}</c> placeholders. Unknown braces are left alone.</summary>
    public (string SystemPrompt, string UserPrompt) Render(IReadOnlyDictionary<string, string> values)
    {
        static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
            values.Aggregate(template, (current, pair) => current.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal));

        return (Fill(SystemPrompt, values), Fill(UserPrompt, values));
    }

    /// <summary>Schema with the taxonomy enumeration filled in: the model cannot answer with a code outside the list.</summary>
    public string SchemaWith(IEnumerable<string> taxonomyCodes) =>
        SchemaJson.Replace($"\"{TaxonomyPlaceholder}\"", string.Join(", ", taxonomyCodes.Select(code => $"\"{code}\"")), StringComparison.Ordinal);

    internal static Assembly Assembly => typeof(PromptTemplate).Assembly;
}
