using OnVoyage.Catalog.Contracts;

namespace OnVoyage.Discovery.Application.IntegrationEvents;

/// <summary>
/// The Catalog announces its view of a place to the <c>discovery</c> queue (§13). Discovery still builds its projection from Factory's
/// events (ADR-0011) until T-502 switches to this one; the event is acknowledged here so it is not parked as an unhandled message.
/// </summary>
public static class PoiProjectionChangedHandler
{
    public static void Handle(PoiProjectionChangedV1 changed)
    {
        // Intentionally empty: the same data already arrives from Factory (PoiPublishedV1 / PoiUnpublishedV1).
    }
}
