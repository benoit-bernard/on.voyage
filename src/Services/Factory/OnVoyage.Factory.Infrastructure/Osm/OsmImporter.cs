using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Ports;

namespace OnVoyage.Factory.Infrastructure.Osm;

/// <summary>
/// Runs osm2pgsql in flex mode as a child process (ADR-0004 of the specification: no access to the Docker socket) and loads the
/// named objects of <c>onvoyage.lua</c> into a dated table of <c>factory_raw</c>. The extract is downloaded unless a local file is configured.
/// </summary>
internal sealed class OsmImporter(HttpClient http, IConfiguration configuration, TimeProvider clock, ILogger<OsmImporter> logger) : IOsmImporter
{
    private const int KeepRawTables = 2;

    public async Task<OsmImportResult> ImportAsync(DestinationConfig destination, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("onvoyage") ?? throw new InvalidOperationException("Connection string 'onvoyage' is missing.");
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var workDirectory = Directory.CreateDirectory(configuration["Factory:DataDirectory"] ?? Path.Combine(Path.GetTempPath(), "onvoyage-factory")).FullName;

        var extract = destination.OsmExtractFile ?? await DownloadAsync(destination, workDirectory, cancellationToken);
        var style = StylePath(workDirectory);
        var table = $"osm_place_{clock.GetUtcNow():yyyyMMddHHmm}";

        var arguments = new List<string>
        {
            "--create", "--slim", "--drop", "--middle-schema=factory_raw", "--output=flex", $"--style={style}", "--extra-attributes",
            "--bbox", string.Join(',', new[] { destination.MinLongitude, destination.MinLatitude, destination.MaxLongitude, destination.MaxLatitude }.Select(value => value.ToString("0.######", CultureInfo.InvariantCulture))),
            "-H", connection.Host ?? "localhost", "-P", connection.Port.ToString(CultureInfo.InvariantCulture), "-U", connection.Username ?? string.Empty, "-d", connection.Database ?? string.Empty,
            extract,
        };

        var timeout = TimeSpan.FromMinutes(configuration.GetValue("Factory:Osm:TimeoutMinutes", 60));
        await RunAsync(configuration["Factory:Osm:Osm2pgsqlPath"] ?? "osm2pgsql", arguments, table, connection.Password, timeout, cancellationToken);

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var rows = await CountAsync(dataSource, table, cancellationToken);
        await DropOldTablesAsync(dataSource, table, cancellationToken);

        logger.LogInformation("OSM import for {Destination}: {Rows} rows in {Table}.", destination.Slug, rows, table);
        return new OsmImportResult(table, rows, Path.GetFileName(extract));
    }

    private async Task<string> DownloadAsync(DestinationConfig destination, string workDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workDirectory, Path.GetFileName(new Uri(destination.OsmExtractUrl).AbsolutePath));
        var maxAge = TimeSpan.FromDays(configuration.GetValue("Factory:Osm:ReuseDownloadDays", 1));
        if (File.Exists(path) && clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) < maxAge)
        {
            return path;
        }

        logger.LogInformation("Downloading the OSM extract for {Destination}.", destination.Slug);
        var partial = path + ".part";
        using (var response = await http.GetAsync(destination.OsmExtractUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(partial);
            await source.CopyToAsync(target, cancellationToken);
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    private static string StylePath(string workDirectory)
    {
        var path = Path.Combine(workDirectory, "onvoyage.lua");
        using var stream = typeof(OsmImporter).Assembly.GetManifestResourceStream("onvoyage.lua") ?? throw new InvalidOperationException("onvoyage.lua is missing.");
        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    private static async Task RunAsync(string executable, List<string> arguments, string table, string? password, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Passed by environment, not by argument: command lines are visible to every process on the machine.
        start.Environment["ONVOYAGE_OSM_TABLE"] = table;
        if (!string.IsNullOrEmpty(password))
        {
            start.Environment["PGPASSWORD"] = password;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("osm2pgsql could not be started.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var tail = new Queue<string>();
        void Collect(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 20)
                {
                    tail.Dequeue();
                }
            }
        }

        process.OutputDataReceived += (_, args) => Collect(args.Data);
        process.ErrorDataReceived += (_, args) => Collect(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            string lastLines;
            lock (tail)
            {
                lastLines = string.Join(" | ", tail);
            }

            throw new InvalidOperationException($"osm2pgsql failed with exit code {process.ExitCode}: {lastLines}");
        }
    }

    private static async Task<int> CountAsync(NpgsqlDataSource dataSource, string table, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"select count(*) from factory_raw.\"{table}\"");
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task DropOldTablesAsync(NpgsqlDataSource dataSource, string current, CancellationToken cancellationToken)
    {
        await using var list = dataSource.CreateCommand("select tablename from pg_tables where schemaname = 'factory_raw' and tablename ~ '^osm_place_[0-9]{12}$' order by tablename desc");
        var names = new List<string>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
            }
        }

        foreach (var old in names.Where(name => name != current).Skip(KeepRawTables - 1))
        {
            await using var drop = dataSource.CreateCommand($"drop table if exists factory_raw.\"{old}\"");
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
