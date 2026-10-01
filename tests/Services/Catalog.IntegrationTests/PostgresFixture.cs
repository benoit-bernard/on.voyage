using Npgsql;
using Testcontainers.PostgreSql;

namespace Catalog.IntegrationTests;

/// <summary>
/// One PostGIS server for the whole run. CI and developers with Docker get a Testcontainers container;
/// set <c>ONVOYAGE_TEST_PG</c> (a superuser connection string) to reuse an existing server instead.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _adminConnection = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("ONVOYAGE_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _adminConnection = external;
            return;
        }

        _container = new PostgreSqlBuilder("postgis/postgis:16-3.4").Build();
        await _container.StartAsync();
        _adminConnection = _container.GetConnectionString();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"catalog_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_adminConnection))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"create database {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_adminConnection) { Database = name, Pooling = false }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
