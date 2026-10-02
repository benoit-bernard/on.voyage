using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Infrastructure.Social;

namespace Creators.UnitTests;

/// <summary>
/// The HTTP shapes of the two platforms, against answers written from their public documentation (no network here, and the applications are not yet
/// reviewed: H-008). They fix what the code assumes; the first real connection is what confirms it.
/// </summary>
public sealed class SocialProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Script : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpStatusCode Status, string Body)> _routes = [];

        public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Calls { get; } = [];

        public Script On(string urlPart, string body, HttpStatusCode status = HttpStatusCode.OK, HttpMethod? method = null)
        {
            _routes.Add((request => request.RequestUri!.ToString().Contains(urlPart, StringComparison.Ordinal) && (method is null || request.Method == method), status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var route = _routes.FirstOrDefault(route => route.Match(request));
            return route.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
        }
    }

    private static PlatformOptions Options(bool pkce = true) => new() { Enabled = true, ClientId = "client", ClientSecret = "secret", RedirectUri = "https://studio.on.voyage/studio/connections/x/callback", UsePkce = pkce };

    private static InstagramProvider Instagram(Script script, bool pkce = true, string? version = null)
    {
        var options = Options(pkce);
        options.ApiVersion = version;
        return new InstagramProvider(new HttpClient(script), options, new FakeTimeProvider(Now));
    }

    private static YouTubeProvider YouTube(Script script) => new(new HttpClient(script), Options(), new FakeTimeProvider(Now));

    private static TokenSet Tokens(string access = "tok", string? refresh = null) => new(access, refresh, Now.AddHours(1), []);

    // ---- Instagram

    [Fact]
    public void The_instagram_authorization_asks_for_the_single_business_scope_and_carries_the_state_and_the_challenge()
    {
        var url = Instagram(new Script()).AuthorizeUrl("the-state", "the-challenge");

        url.GetLeftPart(UriPartial.Path).ShouldBe("https://www.instagram.com/oauth/authorize");
        url.Query.ShouldContain("scope=instagram_business_basic");
        url.Query.ShouldContain("response_type=code");
        url.Query.ShouldContain("state=the-state");
        url.Query.ShouldContain("code_challenge=the-challenge&code_challenge_method=S256");
        url.Query.ShouldNotContain("secret");
        Instagram(new Script(), pkce: false).UsesPkce.ShouldBeFalse();
    }

    [Theory]
    [InlineData("{\"access_token\":\"SHORT\",\"user_id\":17841400000000000,\"permissions\":\"instagram_business_basic\"}")]
    [InlineData("{\"data\":[{\"access_token\":\"SHORT\",\"user_id\":\"17841400000000000\",\"permissions\":\"instagram_business_basic\"}]}")]
    public async Task The_short_lived_token_is_exchanged_for_a_long_lived_one_whatever_the_wrapping_of_the_answer(string shortLived)
    {
        var script = new Script()
            .On("api.instagram.com/oauth/access_token", shortLived)
            .On("graph.instagram.com/access_token?grant_type=ig_exchange_token", "{\"access_token\":\"LONG\",\"token_type\":\"bearer\",\"expires_in\":5184000}");

        var tokens = await Instagram(script).ExchangeCodeAsync("the-code", "the-verifier", Ct);

        tokens.AccessToken.ShouldBe("LONG");
        tokens.ExpiresAt.ShouldBe(Now.AddSeconds(5184000));
        tokens.Scopes.ShouldBe(["instagram_business_basic"]);
        var form = script.Calls[0].Body!;
        form.ShouldContain("grant_type=authorization_code");
        form.ShouldContain("code=the-code");
        form.ShouldContain("code_verifier=the-verifier");
        script.Calls[1].Url.ShouldContain("access_token=SHORT");
    }

    [Fact]
    public async Task A_business_or_creator_account_is_professional_and_a_personal_one_is_not()
    {
        async Task<SocialProfile> Profile(string type) => await Instagram(new Script().On("/me?fields=user_id,username,account_type", $"{{\"user_id\":\"178\",\"username\":\"marie\",\"account_type\":\"{type}\"}}")).GetProfileAsync(Tokens(), Ct);

        (await Profile("BUSINESS")).IsProfessional.ShouldBeTrue();
        (await Profile("MEDIA_CREATOR")).IsProfessional.ShouldBeTrue();
        (await Profile("PERSONAL")).IsProfessional.ShouldBeFalse();
        var profile = await Profile("BUSINESS");
        (profile.ExternalUserId, profile.Username).ShouldBe(("178", "marie"));
    }

    [Fact]
    public async Task The_media_are_listed_page_after_page_with_the_token_in_the_header_and_never_followed_to_another_host()
    {
        var script = new Script()
            .On("/me/media?fields=", """
              {"data":[
                {"id":"1","caption":"Coucher de soleil\n#marseille","media_type":"IMAGE","permalink":"https://www.instagram.com/p/AAA/","media_url":"https://scontent.cdninstagram.com/a.jpg","timestamp":"2026-09-30T18:00:00+0000"},
                {"id":"2","media_type":"VIDEO","permalink":"https://www.instagram.com/reel/BBB/","thumbnail_url":"https://scontent.cdninstagram.com/b.jpg","timestamp":"2026-09-29T08:00:00+0000"}],
               "paging":{"next":"https://graph.instagram.com/me/media?after=CURSOR&access_token=LEAKED"}}
              """)
            .On("after=CURSOR", """
              {"data":[{"id":"3","caption":"Carrousel","media_type":"CAROUSEL_ALBUM","permalink":"https://www.instagram.com/p/CCC/","media_url":"https://scontent.cdninstagram.com/c.jpg","timestamp":"2026-09-01T10:00:00+0000"}],
               "paging":{"next":"https://evil.example/steal?access_token=x"}}
              """);

        var fetch = await Instagram(script).ListAsync(Tokens("SECRET"), "178", 200, Ct);

        fetch.Status.ShouldBe(FetchStatus.Ok);
        fetch.Complete.ShouldBeTrue();
        fetch.Items.Select(item => (item.ExternalId, item.Kind)).ShouldBe([("1", "photo"), ("2", "video"), ("3", "carousel")]);
        fetch.Items[0].Title.ShouldBe("Coucher de soleil");
        fetch.Items[0].ThumbnailUrl.ShouldBe("https://scontent.cdninstagram.com/a.jpg");
        fetch.Items[1].ThumbnailUrl.ShouldBe("https://scontent.cdninstagram.com/b.jpg");
        fetch.Items[1].Title.ShouldStartWith("Publication Instagram du 29/09/2026");
        fetch.Items[0].PublishedAt.ShouldBe(new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero));
        script.Calls.Count.ShouldBe(2); // the foreign next address was not called
        script.Calls.ShouldAllBe(call => call.Authorization == "Bearer SECRET");
    }

    [Fact]
    public async Task A_listing_stops_at_the_limit_and_says_it_is_incomplete()
    {
        var script = new Script().On("/me/media?fields=", """{"data":[{"id":"1","media_type":"IMAGE","permalink":"https://www.instagram.com/p/AAA/"},{"id":"2","media_type":"IMAGE","permalink":"https://www.instagram.com/p/BBB/"}],"paging":{"next":"https://graph.instagram.com/me/media?after=X"}}""");

        var fetch = await Instagram(script).ListAsync(Tokens(), "178", 1, Ct);

        fetch.Items.Count.ShouldBe(1);
        fetch.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task An_expired_instagram_token_is_reported_as_needing_a_new_authorization()
    {
        var script = new Script().On("/me/media", """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}""", HttpStatusCode.BadRequest);

        var thrown = await Should.ThrowAsync<ProviderException>(() => Instagram(script).ListAsync(Tokens(), "178", 10, Ct));

        thrown.ReauthorizationRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task The_instagram_token_is_renewed_with_its_own_refresh_endpoint()
    {
        var script = new Script().On("refresh_access_token?grant_type=ig_refresh_token&access_token=OLD", """{"access_token":"NEW","token_type":"bearer","expires_in":5184000}""");

        var tokens = await Instagram(script).RefreshAsync(Tokens("OLD"), Ct);

        tokens.AccessToken.ShouldBe("NEW");
        tokens.ExpiresAt.ShouldBe(Now.AddSeconds(5184000));
    }

    [Fact]
    public async Task The_api_version_prefix_is_used_when_configured()
    {
        var script = new Script().On("/v23.0/me?fields=", """{"user_id":"1","username":"m","account_type":"BUSINESS"}""");

        await Instagram(script, version: "v23.0").GetProfileAsync(Tokens(), Ct);

        script.Calls.Single().Url.ShouldStartWith("https://graph.instagram.com/v23.0/me?");
    }

    // ---- YouTube

    [Fact]
    public void The_youtube_authorization_asks_for_offline_access_with_the_single_readonly_scope()
    {
        var url = YouTube(new Script()).AuthorizeUrl("the-state", "the-challenge");

        url.GetLeftPart(UriPartial.Path).ShouldBe("https://accounts.google.com/o/oauth2/v2/auth");
        Uri.UnescapeDataString(url.Query).ShouldContain("scope=https://www.googleapis.com/auth/youtube.readonly");
        url.Query.ShouldContain("access_type=offline");
        url.Query.ShouldContain("code_challenge_method=S256");
        url.Query.ShouldNotContain("youtube%20");
    }

    [Fact]
    public async Task The_code_is_exchanged_with_the_verifier_and_a_grant_without_the_readonly_scope_is_refused()
    {
        var script = new Script().On("oauth2.googleapis.com/token", """{"access_token":"A","expires_in":3599,"refresh_token":"R","scope":"https://www.googleapis.com/auth/youtube.readonly","token_type":"Bearer"}""");

        var tokens = await YouTube(script).ExchangeCodeAsync("the-code", "the-verifier", Ct);

        (tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt).ShouldBe(("A", "R", Now.AddSeconds(3599)));
        script.Calls[0].Body!.ShouldContain("code_verifier=the-verifier");
        script.Calls[0].Body!.ShouldContain("grant_type=authorization_code");

        var narrow = new Script().On("oauth2.googleapis.com/token", """{"access_token":"A","expires_in":3599,"refresh_token":"R","scope":"openid","token_type":"Bearer"}""");
        (await Should.ThrowAsync<ProviderException>(() => YouTube(narrow).ExchangeCodeAsync("c", null, Ct))).Code.ShouldBe("scope_missing");
    }

    [Fact]
    public async Task A_revoked_refresh_token_is_reported_as_needing_a_new_authorization_and_a_good_one_keeps_the_refresh_token()
    {
        var revoked = new Script().On("oauth2.googleapis.com/token", """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest);
        (await Should.ThrowAsync<ProviderException>(() => YouTube(revoked).RefreshAsync(Tokens("A", "R"), Ct))).ReauthorizationRequired.ShouldBeTrue();

        var good = new Script().On("oauth2.googleapis.com/token", """{"access_token":"B","expires_in":3599,"scope":"https://www.googleapis.com/auth/youtube.readonly","token_type":"Bearer"}""");
        var tokens = await YouTube(good).RefreshAsync(Tokens("A", "R"), Ct);
        (tokens.AccessToken, tokens.RefreshToken).ShouldBe(("B", "R"));
        good.Calls[0].Body!.ShouldContain("grant_type=refresh_token");
    }

    [Fact]
    public async Task The_channel_of_the_account_is_its_identity_and_an_account_without_channel_is_refused()
    {
        var script = new Script().On("channels?part=snippet&mine=true", """{"items":[{"id":"UCabc123","snippet":{"title":"Marie en Provence","customUrl":"@marieenprovence"}}]}""");
        var profile = await YouTube(script).GetProfileAsync(Tokens(), Ct);
        (profile.ExternalUserId, profile.Username, profile.IsProfessional).ShouldBe(("UCabc123", "@marieenprovence", true));

        var none = new Script().On("channels?part=snippet&mine=true", """{"pageInfo":{"totalResults":0}}""");
        (await Should.ThrowAsync<ProviderException>(() => YouTube(none).GetProfileAsync(Tokens(), Ct))).Code.ShouldBe("no_channel");
    }

    [Fact]
    public async Task The_uploads_are_listed_then_described_with_their_chapters_duration_and_thumbnail()
    {
        var script = new Script()
            .On("playlistItems?part=contentDetails&playlistId=UUabc123&maxResults=50&pageToken=P2", """{"items":[{"contentDetails":{"videoId":"vid00000003"}}]}""")
            .On("playlistItems?part=contentDetails&playlistId=UUabc123", """{"items":[{"contentDetails":{"videoId":"vid00000001"}},{"contentDetails":{"videoId":"vid00000002"}}],"nextPageToken":"P2"}""")
            .On("videos?part=snippet,contentDetails", """
              {"items":[
                {"id":"vid00000001","snippet":{"title":"Provence en 3 jours","description":"Mes étapes\n00:00 Intro\n02:15 Gordes\n05:40 Roussillon","publishedAt":"2026-09-20T10:00:00Z","thumbnails":{"medium":{"url":"https://i.ytimg.com/vi/1/mqdefault.jpg"},"high":{"url":"https://i.ytimg.com/vi/1/hqdefault.jpg"}}},"contentDetails":{"duration":"PT12M34S"}},
                {"id":"vid00000003","snippet":{"title":"Un live à venir","description":"#ad","publishedAt":"2026-09-21T10:00:00Z","thumbnails":{"default":{"url":"https://i.ytimg.com/vi/3/default.jpg"}}},"contentDetails":{"duration":"P0D"}}]}
              """);

        var fetch = await YouTube(script).ListAsync(Tokens("SECRET"), "UCabc123", 200, Ct);

        fetch.Complete.ShouldBeTrue();
        fetch.Items.Select(item => item.ExternalId).ShouldBe(["vid00000001", "vid00000003"]); // 00000002 is private or deleted: videos.list does not return it
        var first = fetch.Items[0];
        (first.Title, first.DurationSeconds, first.Permalink, first.Kind).ShouldBe(("Provence en 3 jours", 754, "https://www.youtube.com/watch?v=vid00000001", "video"));
        first.ThumbnailUrl.ShouldBe("https://i.ytimg.com/vi/1/hqdefault.jpg");
        first.Chapters.Select(chapter => (chapter.StartSeconds, chapter.Title)).ShouldBe([(0, "Intro"), (135, "Gordes"), (340, "Roussillon")]);
        fetch.Items[1].DurationSeconds.ShouldBeNull();
        fetch.Items[1].ThumbnailUrl.ShouldBe("https://i.ytimg.com/vi/3/default.jpg");
        script.Calls.ShouldAllBe(call => call.Authorization == "Bearer SECRET");
        script.Calls.ShouldAllBe(call => !call.Url.Contains("SECRET", StringComparison.Ordinal)); // the token never travels in an address
    }

    [Fact]
    public async Task Revoking_a_youtube_grant_posts_the_refresh_token_to_google()
    {
        var script = new Script().On("oauth2.googleapis.com/revoke", "{}");

        await YouTube(script).RevokeAsync(Tokens("A", "R"), Ct);

        script.Calls.Single().Body.ShouldBe("token=R");
    }

    // ---- thumbnails

    [Theory]
    [InlineData("i.ytimg.com", true)]
    [InlineData("I.YTIMG.COM", true)]
    [InlineData("scontent-cdg4-1.cdninstagram.com", true)]
    [InlineData("scontent.xx.fbcdn.net", true)]
    [InlineData("cdninstagram.com", false)]
    [InlineData("evil-cdninstagram.com", false)]
    [InlineData("i.ytimg.com.evil.example", false)]
    [InlineData("localhost", false)]
    public void A_thumbnail_is_only_fetched_from_a_known_image_host(string host, bool allowed) =>
        ThumbnailStore.HostAllowed(host, new SocialOptions().ThumbnailHosts).ShouldBe(allowed);
}
