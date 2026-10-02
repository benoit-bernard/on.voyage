using System.Net;
using System.Net.Http.Json;
using System.Web;
using OnVoyage.Creators.Application;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;
using OnVoyage.Creators.Infrastructure.Social;
using OnVoyage.TestInfrastructure;
using Wolverine;

namespace Creators.IntegrationTests;

/// <summary>
/// T-1207 and T-1208: connecting Instagram and YouTube, importing, renewing, revoking. The platforms are the deterministic adapters; the HTTP
/// shapes of the real ones are tested in the unit tests with recorded answers. The OAuth tokens are encrypted in the table and never come out.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ConnectionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Studio = "/api/creators/v1/studio";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreatorsHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await CreatorsHost.SharedAsync(postgres);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Creator(Guid Account, Guid Id, HttpClient Client, string Handle);

    private async Task<Creator> NewCreatorAsync()
    {
        var account = Guid.NewGuid();
        using var join = _host.Account(account);
        var handle = CreatorsHost.Unique();
        var registration = await CreatorsHost.Read<StudioRegistrationDto>(await join.PostAsJsonAsync($"{Studio}/signup", new StudioSignupRequest(handle, "Marie", "2026-10"), Ct));
        var client = _host.Account(account, "creator");
        await client.PutAsJsonAsync($"{Studio}/profile", CreatorsHost.Profile(handle, "Marie", "history"), Ct);
        return new Creator(account, registration.CreatorId!.Value, client, handle);
    }

    private static string StateOf(ConnectionStartDto start) => HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query)["state"]!;

    private static async Task<HttpResponseMessage> ConnectAsync(Creator creator, string platform, string user, string? state = null)
    {
        var start = await CreatorsHost.Read<ConnectionStartDto>(await creator.Client.GetAsync($"{Studio}/connections/{platform}/start", Ct));
        return await creator.Client.PostAsJsonAsync($"{Studio}/connections/{platform}/callback", new CompleteConnectionRequest(FakeSocialWorld.CodeFor(user), state ?? StateOf(start)), Ct);
    }

    private static RemoteContent Video(string id, string title, string? description = null, int? duration = 600, DateTimeOffset? at = null) =>
        new(id, $"https://www.youtube.com/watch?v={id}", title, description, ContentKinds.Video, at ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), duration, null, ChapterParser.Parse(description, duration));

    private static RemoteContent Post(string code, string caption, DateTimeOffset? at = null) =>
        new($"178{Math.Abs(code.GetHashCode())}", $"https://www.instagram.com/p/{code}/", caption.Split('\n')[0], caption, ContentKinds.Photo, at ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), null, null, []);

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var value = await read();
        for (var attempt = 0; attempt < 100 && !done(value); attempt++)
        {
            await Task.Delay(100, Ct);
            value = await read();
        }

        return value;
    }

    private Task<long> ContentCount(Guid creatorId, string? status = null) =>
        _host.Scalar<long>($"select count(*) from creators.content_item where creator_id = '{creatorId}'{(status is null ? string.Empty : $" and status = '{status}'")}");

    private async Task SyncAsync(Guid creatorId, string platform)
    {
        var id = await _host.Scalar<Guid>($"select id from creators.connected_account where creator_id = '{creatorId}' and platform = '{platform}'");
        await _host.Bus.InvokeAsync(new SyncConnectedAccountCommand(id), Ct);
    }

    // ---- sign-in to the platform

    [Fact]
    public async Task The_connection_routes_are_for_creators_only()
    {
        using var account = _host.Account();

        (await account.GetAsync($"{Studio}/connections", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await account.GetAsync($"{Studio}/connections/instagram/start", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await account.PostAsync($"{Studio}/sync", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Starting_a_connection_gives_the_platform_address_with_a_state_and_a_challenge_and_an_unknown_platform_is_refused()
    {
        var creator = await NewCreatorAsync();

        var start = await CreatorsHost.Read<ConnectionStartDto>(await creator.Client.GetAsync($"{Studio}/connections/youtube/start", Ct));

        start.AuthorizeUrl.ShouldStartWith("https://fake.onvoyage.test/youtube/authorize");
        StateOf(start).Length.ShouldBeGreaterThan(40);
        start.AuthorizeUrl.ShouldContain("code_challenge=");
        (await creator.Client.GetAsync($"{Studio}/connections/tiktok/start", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var list = await CreatorsHost.Read<ConnectionsDto>(await creator.Client.GetAsync($"{Studio}/connections", Ct));
        list.Platforms.Select(platform => (platform.Platform, platform.Enabled, platform.Account)).ShouldBe([("instagram", true, null), ("youtube", true, null)]);
    }

    [Fact]
    public async Task A_professional_instagram_account_is_connected_and_its_posts_are_imported_in_the_background_with_their_tokens_encrypted()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("ig");
        var world = _host.World.Account("instagram", user);
        world.Contents.Add(Post("ABC123", "Coucher de soleil sur les Goudes\n#marseille #publicité"));
        world.Contents.Add(Post("DEF456", "Le Vieux-Port le matin #marseille"));

        var response = await ConnectAsync(creator, "instagram", user);

        var connected = await CreatorsHost.Read<ConnectedAccountDto>(response);
        (connected.Platform, connected.Username, connected.Status).ShouldBe(("instagram", user, "active"));
        (await EventuallyAsync(() => ContentCount(creator.Id), count => count == 2)).ShouldBe(2);
        var contents = (await _host.Detail(creator.Id)).Contents;
        contents.Select(content => content.Permalink).Order().ShouldBe(["https://www.instagram.com/p/ABC123/", "https://www.instagram.com/p/DEF456/"]);
        contents.Single(content => content.Permalink.Contains("ABC123", StringComparison.Ordinal)).IsCommercial.ShouldBeTrue(); // #publicité
        contents.Single(content => content.Permalink.Contains("DEF456", StringComparison.Ordinal)).IsCommercial.ShouldBeFalse();
        (await _host.Scalar<string>($"select external_id from creators.content_item where creator_id = '{creator.Id}' and permalink like '%ABC123%'")).ShouldBe("ABC123"); // the short code, like a URL typed by hand

        // The tokens are in the table, encrypted, and nowhere else.
        var stored = await _host.Scalar<string>($"select access_token_protected from creators.connected_account where creator_id = '{creator.Id}'");
        stored.ShouldNotBeNullOrEmpty();
        stored.ShouldNotContain("access:");
        var list = await creator.Client.GetStringAsync($"{Studio}/connections", Ct);
        list.ShouldNotContain("access", Case.Insensitive);
        list.ShouldNotContain("token", Case.Insensitive);
        (await creator.Client.GetStringAsync($"{Studio}/profile", Ct)).ShouldNotContain("token", Case.Insensitive);
        (await _host.Scalar<long>($"select count(*) from creators.connected_account where creator_id = '{creator.Id}' and status = 'active'")).ShouldBe(1);
        (await _host.Scalar<bool>($"select last_sync_at is not null from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_published_page_shows_a_badge_for_each_connected_platform_by_name_only_and_the_badge_goes_with_the_connection()
    {
        var creator = await NewCreatorAsync();
        await creator.Client.PostAsync($"{Studio}/publish", null, Ct);
        var user = CreatorsHost.Unique("badge");
        _host.World.Account("youtube", user);
        using var traveler = _host.Traveler();
        (await CreatorsHost.Read<CreatorPageDto>(await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct))).ConnectedPlatforms.ShouldBeEmpty();

        await ConnectAsync(creator, "youtube", user);

        var raw = await traveler.GetStringAsync($"/api/creators/v1/creators/{creator.Handle}", Ct);
        (await CreatorsHost.Read<CreatorPageDto>(await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct))).ConnectedPlatforms.ShouldBe(["youtube"]);
        raw.ShouldNotContain(user);
        await creator.Client.DeleteAsync($"{Studio}/connections/youtube", Ct);
        (await CreatorsHost.Read<CreatorPageDto>(await traveler.GetAsync($"/api/creators/v1/creators/{creator.Handle}", Ct))).ConnectedPlatforms.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_personal_instagram_account_is_refused_with_the_explanation_and_nothing_is_stored()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("perso");
        _host.World.Account("instagram", user, isProfessional: false);

        var response = await ConnectAsync(creator, "instagram", user);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("professional_account_required");
        body.ShouldContain("Compte professionnel requis");
        body.ShouldContain("help.instagram.com");
        (await _host.Scalar<long>($"select count(*) from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBe(0);
        _host.World.Find("instagram", user)!.Revocations.ShouldBe(1); // the token that was obtained is given back
    }

    [Fact]
    public async Task A_state_can_only_be_completed_by_the_creator_who_asked_for_it_for_the_platform_it_was_made_for()
    {
        var marie = await NewCreatorAsync();
        var attacker = await NewCreatorAsync();
        var user = CreatorsHost.Unique("victim");
        _host.World.Account("youtube", user);
        var attackerStart = await CreatorsHost.Read<ConnectionStartDto>(await attacker.Client.GetAsync($"{Studio}/connections/youtube/start", Ct));

        // The attacker has the victim open the authorization address; the victim's session then completes it: refused, nothing is linked.
        var stolen = await ConnectAsync(marie, "youtube", user, StateOf(attackerStart));
        var wrongPlatform = await ConnectAsync(marie, "instagram", user, StateOf(attackerStart));
        var forged = await ConnectAsync(marie, "youtube", user, "not-a-state");

        stolen.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await stolen.Content.ReadAsStringAsync(Ct)).ShouldContain("invalid_state");
        wrongPlatform.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        forged.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _host.Scalar<long>($"select count(*) from creators.connected_account where external_user_id like '%{user}'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_platform_account_cannot_be_connected_to_two_creators()
    {
        var first = await NewCreatorAsync();
        var second = await NewCreatorAsync();
        var user = CreatorsHost.Unique("shared");
        _host.World.Account("youtube", user);
        (await ConnectAsync(first, "youtube", user)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await ConnectAsync(second, "youtube", user);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("account_in_use");
    }

    [Fact]
    public async Task Declining_the_authorization_connects_nothing()
    {
        var creator = await NewCreatorAsync();
        var start = await CreatorsHost.Read<ConnectionStartDto>(await creator.Client.GetAsync($"{Studio}/connections/youtube/start", Ct));

        var response = await creator.Client.PostAsJsonAsync($"{Studio}/connections/youtube/callback", new CompleteConnectionRequest(string.Empty, StateOf(start)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("access_denied");
    }

    // ---- imports (YouTube)

    [Fact]
    public async Task A_youtube_channel_is_imported_with_its_chapters_and_a_later_run_adds_new_videos_and_withdraws_deleted_ones()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("yt");
        var world = _host.World.Account("youtube", user);
        var gordes = Video("vid" + CreatorsHost.Unique()[..8], "Provence en 3 jours", "Mes lieux préférés\n00:00 Intro\n02:15 Gordes\n05:40 Roussillon\n#sponsorisé", 900);
        var doomed = Video("vid" + CreatorsHost.Unique()[..8], "Vieille vidéo", "rien");
        world.Contents.AddRange([gordes, doomed]);
        await ConnectAsync(creator, "youtube", user);
        (await EventuallyAsync(() => ContentCount(creator.Id), count => count == 2)).ShouldBe(2);
        var detail = await _host.Detail(creator.Id);
        var imported = detail.Contents.Single(content => content.Title == "Provence en 3 jours");
        imported.Chapters.Select(chapter => (chapter.StartSeconds, chapter.Title)).ShouldBe([(0, "Intro"), (135, "Gordes"), (340, "Roussillon")]);
        imported.IsCommercial.ShouldBeTrue();
        imported.Platform.ShouldBe("youtube");

        // The creator validated a place of the video that will disappear.
        var poi = await _host.PoiAsync("Gordes " + CreatorsHost.Unique());
        var link = await CreatorsHost.Read<AdminPlaceLinkDto>(await creator.Client.PostAsJsonAsync($"{Studio}/place-links", new AddPlaceLinkRequest(poi, detail.Contents.Single(content => content.Title == "Vieille vidéo").Id, null, null), Ct));
        link.Status.ShouldBe("validated");
        var hidden = detail.Contents.Single(content => content.Title == "Provence en 3 jours");
        await creator.Client.PutAsJsonAsync($"{Studio}/contents/{hidden.Id}", new UpdateContentRequest(hidden.Title, null, null, hidden.DurationSeconds, true, hidden.Chapters, "hidden"), Ct);

        // Next day: a new video, one deleted on YouTube, one whose title changed.
        world.Contents.Remove(doomed);
        world.Contents.Add(Video("vid" + CreatorsHost.Unique()[..8], "Nouvelle vidéo"));
        world.Contents[0] = gordes with { Title = "Provence en 3 jours (v2)" };
        await SyncAsync(creator.Id, "youtube");

        var after = (await _host.Detail(creator.Id)).Contents;
        after.Single(content => content.Title == "Vieille vidéo").Status.ShouldBe("removed");
        after.Single(content => content.Title == "Nouvelle vidéo").Status.ShouldBe("imported");
        after.Single(content => content.Title == "Provence en 3 jours (v2)").Status.ShouldBe("hidden"); // the creator's choice survives the run
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(2); // validated, then withdrawn with the video
    }

    [Fact]
    public async Task An_empty_answer_from_the_platform_withdraws_nothing()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("quiet");
        var world = _host.World.Account("youtube", user);
        world.Contents.Add(Video("vid" + CreatorsHost.Unique()[..8], "Reste en ligne"));
        await ConnectAsync(creator, "youtube", user);
        await EventuallyAsync(() => ContentCount(creator.Id), count => count == 1);
        world.Contents.Clear();

        await SyncAsync(creator.Id, "youtube");

        (await ContentCount(creator.Id, "imported")).ShouldBe(1);
    }

    [Fact]
    public async Task A_truncated_list_only_withdraws_what_is_newer_than_its_oldest_item()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("long");
        var world = _host.World.Account("youtube", user);
        var recent = Video("rec" + CreatorsHost.Unique()[..8], "Récente", at: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var mid = Video("mid" + CreatorsHost.Unique()[..8], "Au milieu", at: new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
        var tail = Video("tai" + CreatorsHost.Unique()[..8], "Dernière de la liste", at: new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var old = Video("old" + CreatorsHost.Unique()[..8], "Très ancienne", at: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        world.Contents.AddRange([recent, mid, tail, old]);
        await ConnectAsync(creator, "youtube", user);
        await EventuallyAsync(() => ContentCount(creator.Id), count => count == 4);

        world.Truncated = true; // the platform has more pages than the limit lets us read
        world.Contents.Remove(mid);
        world.Contents.Remove(old);
        await SyncAsync(creator.Id, "youtube");

        var after = (await _host.Detail(creator.Id)).Contents.ToDictionary(content => content.Title, content => content.Status);
        after["Très ancienne"].ShouldBe("imported"); // older than the oldest listed: maybe just beyond the page
        after["Au milieu"].ShouldBe("removed"); // newer than the oldest listed and missing: gone
        after["Récente"].ShouldBe("imported");
        after["Dernière de la liste"].ShouldBe("imported");
    }

    [Fact]
    public async Task A_content_added_by_hand_is_not_imported_twice_and_a_content_of_another_creator_is_left_alone()
    {
        var creator = await NewCreatorAsync();
        var other = await NewCreatorAsync();
        var user = CreatorsHost.Unique("dup");
        var world = _host.World.Account("youtube", user);
        var shared = Video("dup" + CreatorsHost.Unique()[..8], "Déjà référencée");
        await other.Client.PostAsJsonAsync($"{Studio}/contents", new AddContentRequest(shared.Permalink, "Par l'autre", null, null, null, null, null, false, null), Ct);
        world.Contents.Add(shared);

        await ConnectAsync(creator, "youtube", user);
        await Task.Delay(500, Ct);
        await SyncAsync(creator.Id, "youtube");

        (await ContentCount(creator.Id)).ShouldBe(0);
        (await ContentCount(other.Id)).ShouldBe(1);
    }

    // ---- tokens

    [Fact]
    public async Task An_access_token_about_to_expire_is_renewed_before_the_import()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("renew");
        var world = _host.World.Account("youtube", user);
        world.Contents.Add(Video("ren" + CreatorsHost.Unique()[..8], "Première"));
        await ConnectAsync(creator, "youtube", user);
        await EventuallyAsync(() => ContentCount(creator.Id), count => count == 1);
        var before = await _host.Scalar<string>($"select access_token_protected from creators.connected_account where creator_id = '{creator.Id}'");
        await _host.Execute($"update creators.connected_account set expires_at = now() + interval '1 minute' where creator_id = '{creator.Id}'");
        world.Contents.Add(Video("ren" + CreatorsHost.Unique()[..8], "Seconde"));

        await SyncAsync(creator.Id, "youtube");

        world.Refreshes.ShouldBe(1);
        (await ContentCount(creator.Id)).ShouldBe(2);
        (await _host.Scalar<string>($"select access_token_protected from creators.connected_account where creator_id = '{creator.Id}'")).ShouldNotBe(before);
        (await _host.Scalar<bool>($"select expires_at > now() + interval '30 minutes' from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBeTrue();
    }

    [Fact]
    public async Task When_the_platform_revokes_the_access_the_tokens_are_deleted_the_account_asks_to_be_connected_again_and_imports_stop()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("revoked");
        var world = _host.World.Account("youtube", user);
        world.Contents.Add(Video("rev" + CreatorsHost.Unique()[..8], "Avant"));
        await ConnectAsync(creator, "youtube", user);
        await EventuallyAsync(() => ContentCount(creator.Id), count => count == 1);
        world.TokensValid = false;
        await _host.Execute($"update creators.connected_account set expires_at = now() - interval '1 minute' where creator_id = '{creator.Id}'");

        await SyncAsync(creator.Id, "youtube");

        var account = (await CreatorsHost.Read<ConnectionsDto>(await creator.Client.GetAsync($"{Studio}/connections", Ct))).Platforms.Single(platform => platform.Platform == "youtube").Account!;
        account.Status.ShouldBe("needs_reauth");
        (await _host.Scalar<bool>($"select access_token_protected is null and refresh_token_protected is null from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBeTrue();
        var again = await CreatorsHost.Read<SyncRequestedDto>(await creator.Client.PostAsync($"{Studio}/sync", null, Ct), HttpStatusCode.Accepted);
        again.Accounts.ShouldBe(0);
        (await ContentCount(creator.Id, "imported")).ShouldBe(1); // what was imported stays

        world.TokensValid = true;
        (await ConnectAsync(creator, "youtube", user)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _host.Scalar<bool>($"select access_token_protected is not null and status = 'active' from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_platform_that_does_not_answer_changes_nothing_and_the_next_run_tries_again()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("down");
        var world = _host.World.Account("youtube", user);
        world.Contents.Add(Video("dow" + CreatorsHost.Unique()[..8], "Toujours là"));
        await ConnectAsync(creator, "youtube", user);
        await EventuallyAsync(() => ContentCount(creator.Id), count => count == 1);
        world.Unavailable = true;

        var id = await _host.Scalar<Guid>($"select id from creators.connected_account where creator_id = '{creator.Id}'");
        var result = await _host.Bus.InvokeAsync<Result<SyncReportDto>>(new SyncConnectedAccountCommand(id), Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("provider_error");

        (await ContentCount(creator.Id, "imported")).ShouldBe(1);
        (await _host.Scalar<string>($"select status from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBe("active");
        (await _host.Scalar<string>($"select last_error from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBe("http_503");
    }

    // ---- disconnecting

    [Fact]
    public async Task Disconnecting_revokes_deletes_the_tokens_and_keeps_the_contents_but_not_the_copied_thumbnails()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("bye");
        var world = _host.World.Account("youtube", user);
        world.Contents.Add(Video("bye" + CreatorsHost.Unique()[..8], "Reste"));
        await ConnectAsync(creator, "youtube", user);
        await EventuallyAsync(() => ContentCount(creator.Id), count => count == 1);
        var mediaRoot = Path.Combine(_host.Exports, "media", "creators", creator.Id.ToString("N"));
        var contentId = await _host.Scalar<Guid>($"select id from creators.content_item where creator_id = '{creator.Id}'");
        var cover = $"creators/{creator.Id:N}/{contentId:N}.jpg";
        Directory.CreateDirectory(mediaRoot);
        await File.WriteAllBytesAsync(Path.Combine(mediaRoot, $"{contentId:N}.jpg"), [1, 2, 3], Ct);
        await _host.Execute($"update creators.content_item set cover_path = '{cover}' where id = '{contentId}'");

        var response = await creator.Client.DeleteAsync($"{Studio}/connections/youtube", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        world.Revocations.ShouldBe(1);
        (await _host.Scalar<long>($"select count(*) from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBe(0); // no token exists any more
        (await ContentCount(creator.Id, "imported")).ShouldBe(1);
        (await _host.Scalar<bool>($"select connected_account_id is null and cover_path is null from creators.content_item where id = '{contentId}'")).ShouldBeTrue();
        File.Exists(Path.Combine(mediaRoot, $"{contentId:N}.jpg")).ShouldBeFalse();
        (await creator.Client.DeleteAsync($"{Studio}/connections/youtube", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var request = await creator.Client.PostAsync($"{Studio}/sync", null, Ct);
        (await CreatorsHost.Read<SyncRequestedDto>(request, HttpStatusCode.Accepted)).Accounts.ShouldBe(0); // scheduled calls for this account stop
    }

    [Fact]
    public async Task Disconnecting_with_the_option_also_removes_the_imported_contents_and_withdraws_their_places()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("wipe");
        var world = _host.World.Account("youtube", user);
        world.Contents.Add(Video("wip" + CreatorsHost.Unique()[..8], "À retirer"));
        await ConnectAsync(creator, "youtube", user);
        await EventuallyAsync(() => ContentCount(creator.Id), count => count == 1);
        var poi = await _host.PoiAsync("Calanque " + CreatorsHost.Unique());
        var contentId = await _host.Scalar<Guid>($"select id from creators.content_item where creator_id = '{creator.Id}'");
        await creator.Client.PostAsJsonAsync($"{Studio}/place-links", new AddPlaceLinkRequest(poi, contentId, null, null), Ct);

        var response = await creator.Client.DeleteAsync($"{Studio}/connections/youtube?deleteContents=true", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ContentCount(creator.Id, "removed")).ShouldBe(1);
        (await _host.Queued("discovery", "CreatorPlaceLinkChangedV1", poi)).ShouldBe(2); // validated, then removed
    }

    // ---- rights over the data

    [Fact]
    public async Task Deleting_the_account_revokes_the_tokens_and_the_export_lists_the_connections_without_any_token()
    {
        var creator = await NewCreatorAsync();
        var user = CreatorsHost.Unique("rights");
        var world = _host.World.Account("youtube", user);
        world.Contents.Add(Video("rig" + CreatorsHost.Unique()[..8], "Une vidéo"));
        await ConnectAsync(creator, "youtube", user);
        await EventuallyAsync(() => ContentCount(creator.Id), count => count == 1);

        var export = await _host.Bus.InvokeAsync<OnVoyage.Platform.Contracts.TravelerExportPartReadyV1>(new OnVoyage.Platform.Contracts.TravelerExportRequestedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), creator.Account), Ct);
        var json = await File.ReadAllTextAsync(Path.Combine(_host.Exports, export.Path), Ct);
        json.ShouldContain("connectedAccounts");
        json.ShouldContain(user);
        json.ShouldNotContain("access", Case.Insensitive);
        json.ShouldNotContain("protected", Case.Insensitive);

        await _host.Bus.InvokeAsync<OnVoyage.Platform.Contracts.TravelerDataDeletedV1>(new OnVoyage.Platform.Contracts.TravelerDeletionRequestedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, creator.Account, DateTimeOffset.UtcNow), Ct);

        world.Revocations.ShouldBe(1);
        (await _host.Scalar<long>($"select count(*) from creators.connected_account where creator_id = '{creator.Id}'")).ShouldBe(0);
        (await _host.Scalar<long>($"select count(*) from creators.creator where id = '{creator.Id}'")).ShouldBe(0);
    }

    [Fact]
    public async Task The_schema_stores_tokens_only_in_the_two_protected_columns()
    {
        var columns = await _host.Scalar<string>("select string_agg(column_name, ',' order by column_name) from information_schema.columns where table_schema = 'creators' and column_name like '%token%'");

        columns.ShouldBe("access_token_protected,refresh_token_protected");
    }
}
