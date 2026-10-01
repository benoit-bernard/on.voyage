using System.Globalization;
using System.Text.Json;
using OnVoyage.Insights.Contracts;

namespace OnVoyage.App.Core.Analytics;

/// <summary>
/// Turns what the app reports into what the catalogue allows (§17.3): properties that are not listed are dropped (so a position, a text typed by
/// the traveler or an identifier of the device can never leave through here), values that do not fit are dropped, and the few properties the app
/// reports under another name or unit are renamed. The event itself is kept when a property is dropped.
/// </summary>
internal static class EventShaping
{
    public static IReadOnlyDictionary<string, JsonElement> Shape(EventDefinition definition, IReadOnlyDictionary<string, object?> reported)
    {
        var shaped = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in reported)
        {
            var (name, converted) = Rename(definition.Name, key, value);
            var property = definition.Properties.FirstOrDefault(p => p.Name == name);
            if (property is not null && Convert(property, converted) is { } element)
            {
                shaped[name] = element;
            }
        }

        return shaped;
    }

    /// <summary>The app measures the distance of a trigger in metres and names the origin of a story « discovery »; the catalogue has 50 m buckets and « auto ».</summary>
    private static (string Name, object? Value) Rename(string eventName, string key, object? value) => (eventName, key) switch
    {
        ("story_triggered", "distance_m") when TryDouble(value, out var metres) && metres >= 0 => ("distance_bucket_50m", (long)Math.Floor(metres / 50d)),
        ("audio_started", "origin") => ("trigger", string.Equals(value?.ToString(), "manual", StringComparison.OrdinalIgnoreCase) ? "manual" : "auto"),
        _ => (key, value),
    };

    private static JsonElement? Convert(EventProperty property, object? value)
    {
        switch (property.Kind)
        {
            case PropertyKind.Text:
                var text = value switch
                {
                    string s => s,
                    Guid guid => guid.ToString("D"),
                    Enum e => e.ToString().ToLowerInvariant(),
                    _ => null,
                };
                return text is { Length: > 0 } && text.Length <= property.MaxLength && (property.Allowed is null || property.Allowed.Contains(text))
                    ? JsonSerializer.SerializeToElement(text)
                    : null;

            case PropertyKind.WholeNumber:
                return TryDouble(value, out var whole) && whole >= 0 && whole <= property.Max
                    ? JsonSerializer.SerializeToElement((long)Math.Round(whole, MidpointRounding.AwayFromZero))
                    : null;

            case PropertyKind.Number:
                return TryDouble(value, out var number) && number >= 0 && number <= property.Max ? JsonSerializer.SerializeToElement(number) : null;

            case PropertyKind.Boolean:
                return value is bool flag ? JsonSerializer.SerializeToElement(flag) : null;

            default:
                return null;
        }
    }

    private static bool TryDouble(object? value, out double number)
    {
        number = 0;
        if (value is null or bool or string || value is not IConvertible)
        {
            return false;
        }

        try
        {
            number = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
