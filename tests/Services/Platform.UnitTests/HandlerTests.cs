using NSubstitute;
using OnVoyage.Platform.Application.Features.Config;
using OnVoyage.Platform.Application.Features.Consents;
using OnVoyage.Platform.Application.Features.Flags;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace Platform.UnitTests;

public sealed class HandlerTests
{
    private readonly IRemoteConfigStore _config = Substitute.For<IRemoteConfigStore>();
    private readonly IFeatureFlagStore _flags = Substitute.For<IFeatureFlagStore>();
    private readonly IConsentStore _consents = Substitute.For<IConsentStore>();
    private readonly TimeProvider _clock = new FixedClock(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static RemoteConfigEntry Entry(string key, string json, int version = 1) => new(key, json, version, "seed", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Set_config_increments_the_version_and_emits_exactly_one_event()
    {
        _config.FindAsync("app", Arg.Any<CancellationToken>()).Returns(Entry("app", """{"min_app_version":"1.0.0"}""", 3));

        var result = await SetConfigHandler.Handle(new SetConfigCommand("app", """{"min_app_version":"1.1.0"}""", "admin"), _config, _clock, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe(4);
        await _config.Received(1).SaveAsync(
            Arg.Is<RemoteConfigEntry>(e => e.Version == 4 && e.UpdatedBy == "admin"),
            Arg.Is<ConfigChangedV1>(e => e.Key == "app" && e.Version == 4 && e.OccurredAt == _clock.GetUtcNow()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task First_version_of_a_new_key_is_one()
    {
        var result = await SetConfigHandler.Handle(new SetConfigCommand("new_key", "42", "admin"), _config, _clock, Ct);

        result.Value!.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData("Bad Key", "{}")]
    [InlineData("UPPER", "{}")]
    [InlineData("", "{}")]
    [InlineData("ok_key", "{not json")]
    public async Task Invalid_key_or_json_is_rejected_without_writing(string key, string json)
    {
        var result = await SetConfigHandler.Handle(new SetConfigCommand(key, json, "admin"), _config, _clock, Ct);

        result.Error!.Code.ShouldBe("validation");
        await _config.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Oversized_value_is_rejected()
    {
        var huge = "\"" + new string('x', ConfigKey.MaxValueBytes) + "\"";

        (await SetConfigHandler.Handle(new SetConfigCommand("big", huge, "admin"), _config, _clock, Ct)).Error!.Code.ShouldBe("validation");
    }

    [Fact]
    public async Task Client_config_hides_server_only_keys_and_evaluates_flags()
    {
        _config.ListAsync(Arg.Any<CancellationToken>()).Returns([Entry("app", """{"x":1}"""), Entry("security", """{"rate":1}"""), Entry("deletion", "{}")]);
        _flags.ListAsync(Arg.Any<CancellationToken>()).Returns([new FeatureFlag("car_mode", true, 100, ["android"], null), new FeatureFlag("english", false, 0, [], null)]);

        var result = await GetClientConfigHandler.Handle(new GetClientConfigQuery("android", "1.0.0", null), _config, _flags, Ct);

        result.Value!.Config.Keys.ShouldBe(["app"]);
        result.Value.Flags["car_mode"].ShouldBeTrue();
        result.Value.Flags["english"].ShouldBeFalse();
    }

    [Fact]
    public async Task Edge_scope_returns_only_security_and_no_flags()
    {
        _config.ListAsync(Arg.Any<CancellationToken>()).Returns([Entry("app", "{}"), Entry("security", """{"rate":1}""")]);

        var result = await GetClientConfigHandler.Handle(new GetClientConfigQuery(null, null, null, ConfigScope.Edge), _config, _flags, Ct);

        result.Value!.Config.Keys.ShouldBe(["security"]);
        result.Value.Flags.ShouldBeEmpty();
    }

    [Fact]
    public async Task Revision_changes_when_a_version_changes_and_is_stable_otherwise()
    {
        _flags.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        _config.ListAsync(Arg.Any<CancellationToken>()).Returns([Entry("app", "{}", 1)]);
        var first = (await GetClientConfigHandler.Handle(new GetClientConfigQuery(null, null, null), _config, _flags, Ct)).Value!.Revision;
        var again = (await GetClientConfigHandler.Handle(new GetClientConfigQuery(null, null, null), _config, _flags, Ct)).Value!.Revision;
        _config.ListAsync(Arg.Any<CancellationToken>()).Returns([Entry("app", "{}", 2)]);
        var changed = (await GetClientConfigHandler.Handle(new GetClientConfigQuery(null, null, null), _config, _flags, Ct)).Value!.Revision;

        again.ShouldBe(first);
        changed.ShouldNotBe(first);
    }

    [Fact]
    public async Task Flag_updates_are_validated()
    {
        _flags.ListAsync(Arg.Any<CancellationToken>()).Returns([]);

        (await FlagHandler.Handle(new SetFlagCommand("nope", true, 100, [], null), _flags, Ct)).Error!.Code.ShouldBe("flag_not_found");
        (await FlagHandler.Handle(new SetFlagCommand("car_mode", true, 101, [], null), _flags, Ct)).Error!.Code.ShouldBe("validation");
        (await FlagHandler.Handle(new SetFlagCommand("car_mode", true, 50, ["windows"], null), _flags, Ct)).Error!.Code.ShouldBe("validation");
        (await FlagHandler.Handle(new SetFlagCommand("car_mode", true, 50, [], "one.two"), _flags, Ct)).Error!.Code.ShouldBe("validation");
        await _flags.DidNotReceiveWithAnyArgs().SaveAsync(default!, Ct);

        var ok = await FlagHandler.Handle(new SetFlagCommand("car_mode", true, 50, ["ANDROID"], "1.2.0"), _flags, Ct);
        ok.Value!.Platforms.ShouldBe(["android"]);
        await _flags.Received(1).SaveAsync(Arg.Is<FeatureFlag>(flag => flag.RolloutPercent == 50), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consents_default_to_not_granted_for_every_kind()
    {
        _consents.ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await ConsentHandler.Handle(new GetConsentsQuery(Guid.NewGuid()), _consents, Ct);

        result.Value!.Select(c => (c.Kind, c.Granted)).ShouldBe([("analytics", false), ("ads_personalization", false)]);
    }

    [Fact]
    public async Task Granting_a_consent_emits_one_event_and_repeating_it_is_a_no_op()
    {
        var traveler = Guid.NewGuid();
        _consents.ListAsync(traveler, Arg.Any<CancellationToken>()).Returns([]);
        await ConsentHandler.Handle(new SetConsentCommand(traveler, "analytics", true, "v1"), _consents, _clock, Ct);
        await _consents.Received(1).SaveAsync(
            Arg.Is<Consent>(c => c.Granted && c.Kind == "analytics"),
            Arg.Is<ConsentChangedV1>(e => e.TravelerId == traveler && e.Granted && e.TextVersion == "v1"),
            Arg.Any<CancellationToken>());

        _consents.ListAsync(traveler, Arg.Any<CancellationToken>()).Returns([new Consent(traveler, "analytics", true, "v1", DateTimeOffset.UnixEpoch)]);
        _consents.ClearReceivedCalls();
        await ConsentHandler.Handle(new SetConsentCommand(traveler, "analytics", true, "v1"), _consents, _clock, Ct);

        await _consents.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, Ct);
    }

    [Theory]
    [InlineData("marketing", "v1")]
    [InlineData("analytics", "")]
    public async Task Unknown_kind_or_missing_text_version_is_rejected(string kind, string version)
    {
        var result = await ConsentHandler.Handle(new SetConsentCommand(Guid.NewGuid(), kind, true, version), _consents, _clock, Ct);

        result.Error!.Code.ShouldBe("validation");
    }
}
