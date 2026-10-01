using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Domain;

namespace Platform.UnitTests;

/// <summary>In-memory implementation of every identity port, so handlers can be tested end to end without a database.</summary>
internal sealed class AuthWorld
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    public Dictionary<Guid, Account> Accounts { get; } = [];
    public List<OtpChallenge> Challenges { get; } = [];
    public List<RefreshTokenRecord> Refresh { get; } = [];
    public List<(string Email, string Code)> Sent { get; } = [];
    public bool EmailFails { get; set; }
    public AuthSettings Settings { get; set; } = new(TimeSpan.FromMinutes(10), 5, TimeSpan.FromSeconds(60), 5, TimeSpan.FromHours(1), TimeSpan.FromDays(90), []);
    public MutableClock Clock { get; } = new(Start);
    public int CodeCounter { get; set; }

    public IAccountStore AccountStore { get; }
    public IOtpStore OtpStore { get; }
    public IRefreshTokenStore RefreshStore { get; }
    public ITokenIssuer Tokens { get; } = new FakeTokens();
    public ICredentialService Credentials { get; }
    public IAuthSettingsProvider SettingsProvider { get; }
    public IEmailSender Email { get; }

    public AuthWorld()
    {
        AccountStore = new AccountsPort(this);
        OtpStore = new OtpsPort(this);
        RefreshStore = new RefreshPort(this);
        Credentials = new FakeCredentials(this);
        SettingsProvider = new SettingsPort(this);
        Email = new Sender(this);
    }

    public string LastCode => Sent[^1].Code;

    public sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeTokens : ITokenIssuer
    {
        public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Account account, TimeSpan lifetime, DateTimeOffset now) =>
            ($"jwt:{account.Id}:{(account.IsAnonymous ? "anon" : "verified")}", now + lifetime);
    }

    private sealed class FakeCredentials(AuthWorld world) : ICredentialService
    {
        public string NewOtpCode() => (100000 + ++world.CodeCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string NewRefreshToken() => $"refresh-{Guid.NewGuid():N}";

        public string HashOtp(Guid challengeId, string email, string code) => Sha($"{challengeId}|{email}|{code}");

        public string HashRefreshToken(string token) => Sha(token);

        private static string Sha(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

        public bool FixedTimeEquals(string left, string right) => left == right;
    }

    private sealed class SettingsPort(AuthWorld world) : IAuthSettingsProvider
    {
        public Task<AuthSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(world.Settings);
    }

    private sealed class Sender(AuthWorld world) : IEmailSender
    {
        public Task SendOtpAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            if (world.EmailFails)
            {
                throw new EmailDeliveryException("down");
            }

            world.Sent.Add((email, code));
            return Task.CompletedTask;
        }
    }

    private sealed class AccountsPort(AuthWorld world) : IAccountStore
    {
        public Task<Account?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(world.Accounts.GetValueOrDefault(id));

        public Task<Account?> FindByEmailAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult(world.Accounts.Values.FirstOrDefault(account => account.Email == email));

        public Task SaveAsync(Account account, CancellationToken cancellationToken)
        {
            world.Accounts[account.Id] = account;
            return Task.CompletedTask;
        }
    }

    private sealed class OtpsPort(AuthWorld world) : IOtpStore
    {
        public Task<OtpChallenge?> LatestAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult(world.Challenges.Where(c => c.Email == email).OrderByDescending(c => c.CreatedAt).FirstOrDefault());

        public Task<OtpChallenge?> FindActiveAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult(world.Challenges.Where(c => c.Email == email && c.ConsumedAt is null).OrderByDescending(c => c.CreatedAt).FirstOrDefault());

        public Task<int> CountSinceAsync(string email, DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult(world.Challenges.Count(c => c.Email == email && c.CreatedAt >= since));

        public Task AddAsync(OtpChallenge challenge, DateTimeOffset now, CancellationToken cancellationToken)
        {
            for (var i = 0; i < world.Challenges.Count; i++)
            {
                if (world.Challenges[i].Email == challenge.Email && world.Challenges[i].ConsumedAt is null)
                {
                    world.Challenges[i] = world.Challenges[i] with { ConsumedAt = now };
                }
            }

            world.Challenges.Add(challenge);
            return Task.CompletedTask;
        }

        public Task SaveAsync(OtpChallenge challenge, CancellationToken cancellationToken)
        {
            world.Challenges[world.Challenges.FindIndex(c => c.Id == challenge.Id)] = challenge;
            return Task.CompletedTask;
        }
    }

    private sealed class RefreshPort(AuthWorld world) : IRefreshTokenStore
    {
        public Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken)
        {
            world.Refresh.Add(record);
            return Task.CompletedTask;
        }

        public Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult(world.Refresh.FirstOrDefault(r => r.TokenHash == tokenHash));

        public Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var index = world.Refresh.FindIndex(r => r.Id == id && r.UsedAt is null);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            world.Refresh[index] = world.Refresh[index] with { UsedAt = now };
            return Task.FromResult(true);
        }

        public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            for (var i = 0; i < world.Refresh.Count; i++)
            {
                if (world.Refresh[i].FamilyId == familyId && world.Refresh[i].RevokedAt is null)
                {
                    world.Refresh[i] = world.Refresh[i] with { RevokedAt = now };
                }
            }

            return Task.CompletedTask;
        }

        public Task RevokeAllForAccountAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            for (var i = 0; i < world.Refresh.Count; i++)
            {
                if (world.Refresh[i].AccountId == accountId && world.Refresh[i].RevokedAt is null)
                {
                    world.Refresh[i] = world.Refresh[i] with { RevokedAt = now };
                }
            }

            return Task.CompletedTask;
        }
    }
}
