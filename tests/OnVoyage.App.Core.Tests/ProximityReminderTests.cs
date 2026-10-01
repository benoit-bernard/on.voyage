using NSubstitute;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Wishes;

namespace OnVoyage.App.Core.Tests;

public sealed class ProximityReminderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<Guid, DateTimeOffset> Nothing = [];

    // Fort Saint-Nicolas and a point about 600 m / 900 m away from it.
    private const double FortLat = 43.2905;
    private const double FortLng = 5.3640;

    private static SavedPlace Fort(DateTimeOffset? savedAt = null) => new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"), "Fort Saint-Nicolas", "fort-saint-nicolas", FortLat, FortLng, savedAt ?? Now.AddDays(-3));

    private static (double Lat, double Lng) North(double meters) => (FortLat + (meters / 111_320d), FortLng);

    [Fact]
    public void On_foot_within_800_metres_the_place_is_recalled_with_its_distance()
    {
        var (lat, lng) = North(600);
        var reminder = ProximityReminderRule.Evaluate(lat, lng, 1.2, [Fort()], Nothing, Now);

        reminder.ShouldNotBeNull();
        Math.Abs(reminder.DistanceMeters - 600).ShouldBeLessThan(20);
        reminder.Message.ShouldBe($"Vous êtes à {reminder.DistanceMeters} m de Fort Saint-Nicolas que vous aviez enregistré. Un détour ?");
    }

    [Fact]
    public void Beyond_800_metres_on_foot_nothing_is_said()
    {
        var (lat, lng) = North(900);
        ProximityReminderRule.Evaluate(lat, lng, 1.2, [Fort()], Nothing, Now).ShouldBeNull();
    }

    [Fact]
    public void A_place_saved_in_an_earlier_year_mentions_that_year()
    {
        var (lat, lng) = North(600);
        var reminder = ProximityReminderRule.Evaluate(lat, lng, 1.2, [Fort(new DateTimeOffset(2025, 4, 2, 9, 0, 0, TimeSpan.Zero))], Nothing, Now);
        reminder!.Message.ShouldContain("que vous aviez ajouté lors de votre voyage de 2025");
    }

    [Fact]
    public void A_place_saved_this_year_does_not_mention_a_year()
    {
        var (lat, lng) = North(600);
        ProximityReminderRule.Evaluate(lat, lng, 1.2, [Fort(new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero))], Nothing, Now)!.Message.ShouldNotContain("voyage de");
    }

    [Fact]
    public void A_reminder_ten_days_ago_blocks_a_new_one_and_thirty_days_frees_it()
    {
        var (lat, lng) = North(600);
        var fort = Fort();
        ProximityReminderRule.Evaluate(lat, lng, 1.2, [fort], new Dictionary<Guid, DateTimeOffset> { [fort.PoiId] = Now.AddDays(-10) }, Now).ShouldBeNull();
        ProximityReminderRule.Evaluate(lat, lng, 1.2, [fort], new Dictionary<Guid, DateTimeOffset> { [fort.PoiId] = Now.AddDays(-30) }, Now).ShouldNotBeNull();
    }

    [Fact]
    public void Three_reminders_a_day_is_the_ceiling()
    {
        var (lat, lng) = North(600);
        var others = Enumerable.Range(0, 3).ToDictionary(_ => Guid.NewGuid(), i => Now.AddHours(-1 - i));
        ProximityReminderRule.Evaluate(lat, lng, 1.2, [Fort()], others, Now).ShouldBeNull();

        // Reminders of yesterday do not count.
        var yesterday = Enumerable.Range(0, 3).ToDictionary(_ => Guid.NewGuid(), i => Now.AddDays(-1).AddHours(-i));
        ProximityReminderRule.Evaluate(lat, lng, 1.2, [Fort()], yesterday, Now).ShouldNotBeNull();
    }

    [Fact]
    public void In_a_vehicle_the_estimated_detour_decides_not_the_distance()
    {
        // 50 km/h = 13.9 m/s. 3 km away: detour = 2 × 3000 × 1.3 / 13.9 = 561 s = 9.4 min → recalled.
        var near = North(3_000);
        var reminder = ProximityReminderRule.Evaluate(near.Lat, near.Lng, 13.9, [Fort()], Nothing, Now);
        reminder.ShouldNotBeNull();
        reminder.Message.ShouldContain("min de détour de Fort Saint-Nicolas");

        // 6 km away: 18.7 min → too long.
        var far = North(6_000);
        ProximityReminderRule.Evaluate(far.Lat, far.Lng, 13.9, [Fort()], Nothing, Now).ShouldBeNull();
    }

    [Fact]
    public void The_detour_formula_is_twice_the_distance_times_one_point_three_over_the_speed()
    {
        ProximityReminderRule.DetourMinutes(3_000, 13.9, new ReminderSettings()).ShouldBe(2 * 3000 * 1.3 / 13.9 / 60, 1e-9);
        ProximityReminderRule.DetourMinutes(3_000, 0, new ReminderSettings()).ShouldBe(double.PositiveInfinity);
    }

    [Fact]
    public void The_nearest_eligible_place_wins()
    {
        var closer = new SavedPlace(Guid.NewGuid(), "Pharo", "pharo", FortLat + (200 / 111_320d), FortLng, Now.AddYears(-1));
        var (lat, lng) = (FortLat + (250 / 111_320d), FortLng);
        ProximityReminderRule.Evaluate(lat, lng, 1.0, [Fort(), closer], Nothing, Now)!.PoiId.ShouldBe(closer.PoiId);
    }

    private sealed class FakeNotifier : OnVoyage.App.Core.Feedback.ILocalNotifier
    {
        public List<string> Bodies { get; } = [];

        public Task NotifyAsync(string title, string body, CancellationToken cancellationToken)
        {
            Bodies.Add(body);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_service_reminds_once_from_the_position_stream_and_remembers_the_date()
    {
        var fort = new OnVoyage.Catalog.Contracts.PoiSummaryDto(Guid.NewGuid(), "fort", "Fort Saint-Nicolas", "history", FortLat, FortLng, 0.8, 0.8, 2, false, null, 90, new Dictionary<string, double>());
        var catalog = Substitute.For<OnVoyage.App.Core.Catalog.ICatalogClient>();
        catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([fort]);
        var profiles = new OnVoyage.App.Core.Profile.InMemoryProfileStore();
        await profiles.SaveAsync(new OnVoyage.App.Core.Profile.LocalProfile { Saved = [fort.Id], SavedAt = new() { [fort.Id] = Now.AddYears(-2) } }, CancellationToken.None);
        var location = new SimulatedLocationSource();
        var store = new InMemoryReminderStore();
        var notifier = new FakeNotifier();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now);
        using var service = new ProximityReminderService(location, profiles, catalog, store, notifier, clock);
        await location.StartAsync(CancellationToken.None);
        service.Start();

        var (lat, lng) = North(500);
        location.Emit(new LocationFix(lat, lng, 10, 1.1, null, Now));
        await service.LastHandling;
        location.Emit(new LocationFix(lat, lng, 10, 1.1, null, Now.AddSeconds(30)));
        await service.LastHandling;

        service.Current!.PoiId.ShouldBe(fort.Id);
        service.Current.Message.ShouldContain("2024");
        notifier.Bodies.Count.ShouldBe(1);
        (await store.LoadAsync(CancellationToken.None)).ShouldContainKey(fort.Id);
    }
}
