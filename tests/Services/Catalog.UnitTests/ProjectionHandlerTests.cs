using NSubstitute;
using OnVoyage.Catalog.Application.IntegrationEvents;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Factory.Contracts;

namespace Catalog.UnitTests;

public sealed class ProjectionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IPoiProjectionWriter _writer = Substitute.For<IPoiProjectionWriter>();

    private static PoiProjectionChangedV1 Changed(Guid poi, int version, bool published) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, poi, version, Guid.NewGuid(), "marseille", "fort", "Fort", null, [], "Marseille", 43.29, 5.36, 80, false, 0.9f, 2, new Dictionary<string, float>(), [], published);

    [Fact]
    public async Task A_publication_returns_the_projection_the_writer_holds_for_the_place()
    {
        var poi = Guid.NewGuid();
        var projection = Changed(poi, 2, true);
        _writer.ProjectionAsync(poi, Arg.Any<CancellationToken>()).Returns(projection);
        var published = new PoiPublishedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, poi, 2, new PoiDestinationV1("marseille", "Marseille", 43.3, 5.4), "fort", "Fort", null, 43.29, 5.36, 80, 10, false, 0.9f, 1, [], new PoiCrowdProfileV1(1, 1, 2), false, false);

        var announced = await PoiPublishedHandler.Handle(published, _writer, Ct);

        announced.ShouldBe(projection);
        await _writer.Received(1).ApplyPublishedAsync(published, Ct);
    }

    [Fact]
    public async Task A_withdrawal_returns_the_projection_marked_unpublished_and_an_unknown_place_announces_nothing()
    {
        var poi = Guid.NewGuid();
        _writer.ProjectionAsync(poi, Arg.Any<CancellationToken>()).Returns(Changed(poi, 3, false));

        var announced = await PoiUnpublishedHandler.Handle(new PoiUnpublishedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, poi, 3, "erreur"), _writer, Ct);
        var nothing = await PoiUnpublishedHandler.Handle(new PoiUnpublishedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), 1, "inconnu"), _writer, Ct);

        announced!.IsPublished.ShouldBeFalse();
        nothing.ShouldBeNull();
    }
}
