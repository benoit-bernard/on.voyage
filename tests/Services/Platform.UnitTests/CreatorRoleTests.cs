using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OnVoyage.Creators.Contracts;
using OnVoyage.Platform.Application.IntegrationEvents;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Domain;

namespace Platform.UnitTests;

public sealed class CreatorRoleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly IAccountStore _accounts = Substitute.For<IAccountStore>();
    private readonly CreatorTermsAcceptedHandler _handler = new(NullLogger<CreatorTermsAcceptedHandler>.Instance);

    private static CreatorTermsAcceptedV1 Accepted(Guid account) => new(Guid.NewGuid(), Now, account, Guid.NewGuid(), "fondateur");

    private Account Existing(bool verified, params string[] roles)
    {
        var account = new Account(Guid.NewGuid(), verified ? "marie@example.org" : null, verified ? Now : null, roles, Now, Now, null);
        _accounts.FindAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        return account;
    }

    [Fact]
    public async Task A_verified_account_gets_the_creator_role_and_keeps_its_other_roles()
    {
        var account = Existing(verified: true, "admin");

        await _handler.Handle(Accepted(account.Id), _accounts, Ct);

        await _accounts.Received(1).SaveAsync(Arg.Is<Account>(saved => saved.Id == account.Id && saved.Roles.SequenceEqual(new[] { "admin", "creator" })), Ct);
    }

    [Fact]
    public async Task An_anonymous_account_never_gets_the_creator_role()
    {
        var account = Existing(verified: false);

        await _handler.Handle(Accepted(account.Id), _accounts, Ct);

        await _accounts.DidNotReceiveWithAnyArgs().SaveAsync(default!, Ct);
    }

    [Fact]
    public async Task The_role_is_added_once_and_an_unknown_account_is_ignored()
    {
        var account = Existing(verified: true, "creator");

        await _handler.Handle(Accepted(account.Id), _accounts, Ct);
        await _handler.Handle(Accepted(Guid.NewGuid()), _accounts, Ct);

        await _accounts.DidNotReceiveWithAnyArgs().SaveAsync(default!, Ct);
    }
}
