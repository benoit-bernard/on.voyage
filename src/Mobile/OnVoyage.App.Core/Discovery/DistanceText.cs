namespace OnVoyage.App.Core.Discovery;

/// <summary>Distances as the screens write them: "450 m", "4 km", "1,2 km" (rounded to 100 m above a kilometre).</summary>
public static class DistanceText
{
    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public static string Format(int meters) =>
        meters < 1000 ? $"{Math.Max(0, meters)} m" : $"{Math.Round(meters / 1000d, 1, MidpointRounding.AwayFromZero).ToString("0.#", French)} km";
}
