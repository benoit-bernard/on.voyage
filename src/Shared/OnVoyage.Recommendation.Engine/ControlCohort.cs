using System.Security.Cryptography;
using System.Text;

namespace OnVoyage.Recommendation.Engine;

/// <summary>Stable assignment of a traveler to the 20 % control cohort: <c>hash(traveler_id) mod 100 &lt; percent</c>.</summary>
public static class ControlCohort
{
    public static bool Contains(Guid travelerId, int percent = 20)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(travelerId.ToString("D")));
        var bucket = BitConverter.ToUInt32(digest, 0) % 100u;
        return bucket < percent;
    }
}
