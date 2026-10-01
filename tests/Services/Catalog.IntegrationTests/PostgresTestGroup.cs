using OnVoyage.TestInfrastructure;

namespace Catalog.IntegrationTests;

// xUnit discovers collection definitions in the test assembly itself, so each integration test project declares the shared group.
[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
