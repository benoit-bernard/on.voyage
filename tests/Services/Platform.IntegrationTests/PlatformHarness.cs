using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OnVoyage.Platform.Api;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;

namespace Platform.IntegrationTests;

internal sealed class CapturingEmailSender : IEmailSender
{
    public List<(string Email, string Code)> Sent { get; } = [];

    public bool Fail { get; set; }

    public Task SendOtpAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new EmailDeliveryException("simulated outage");
        }

        lock (Sent)
        {
            Sent.Add((email, code));
        }

        return Task.CompletedTask;
    }

    public string LastCodeFor(string email)
    {
        lock (Sent)
        {
            return Sent.Last(item => item.Email == email).Code;
        }
    }
}

/// <summary>The Platform API on a fresh database, with a capturing e-mail sender and a controllable clock.</summary>
internal sealed class PlatformHarness : IAsyncDisposable
{
    private readonly WebApplicationFactory<PlatformApiMarker> _factory;

    private PlatformHarness(WebApplicationFactory<PlatformApiMarker> factory, string connection, CapturingEmailSender email, FakeTimeProvider clock)
    {
        _factory = factory;
        Connection = connection;
        Email = email;
        Clock = clock;
        Client = factory.CreateClient();
    }

    public string Connection { get; }

    public CapturingEmailSender Email { get; }

    public FakeTimeProvider Clock { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => _factory.Services;

    public static async Task<PlatformHarness> StartAsync(PostgresFixture postgres, string? connection = null, params string[] adminEmails)
    {
        connection ??= await postgres.CreateDatabaseAsync();
        var email = new CapturingEmailSender();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var factory = new WebApplicationFactory<PlatformApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            for (var i = 0; i < adminEmails.Length; i++)
            {
                builder.UseSetting($"Auth:BootstrapAdminEmails:{i}", adminEmails[i]);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(email);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });

        return new PlatformHarness(factory, connection, email, clock);
    }

    public async Task<AuthSessionDto> AnonymousAsync()
    {
        var response = await Client.PostAsync("/api/platform/v1/auth/anonymous", null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthSessionDto>(TestContext.Current.CancellationToken))!;
    }

    public static HttpRequestMessage Request(HttpMethod method, string url, AuthSessionDto? session = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (session is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, AuthSessionDto? session = null, object? body = null) =>
        Client.SendAsync(Request(method, url, session, body), TestContext.Current.CancellationToken);

    public async Task<HttpResponseMessage> RequestCodeAsync(string email, AuthSessionDto? session = null) =>
        await SendAsync(HttpMethod.Post, "/api/platform/v1/auth/otp/request", session, new { email });

    public async Task<HttpResponseMessage> VerifyAsync(string email, string code, AuthSessionDto? session = null) =>
        await SendAsync(HttpMethod.Post, "/api/platform/v1/auth/otp/verify", session, new { email, code });

    /// <summary>Requests a code, waits out the resend cooldown for the next call, and verifies it.</summary>
    public async Task<AuthSessionDto> SignInAsync(string email, AuthSessionDto? from = null)
    {
        (await RequestCodeAsync(email, from)).EnsureSuccessStatusCode();
        var response = await VerifyAsync(email, Email.LastCodeFor(email), from);
        response.EnsureSuccessStatusCode();
        Clock.Advance(TimeSpan.FromSeconds(61));
        return (await response.Content.ReadFromJsonAsync<AuthSessionDto>(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }
}
