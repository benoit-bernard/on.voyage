using System.Xml.Linq;

namespace OnVoyage.ArchitectureTests;

/// <summary>Enforces copilot-instructions "Architecture (obligatoire)" on the project graph and sources.</summary>
public sealed class ArchitectureTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OnVoyage.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> Projects() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories);

    private static string[] References(string csproj) =>
        [.. XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string)e.Attribute("Include")!).Replace('\\', '/')))];

    private static string[] Packages(string csproj) =>
        [.. XDocument.Load(csproj).Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!)];

    private static string Layer(string project) => project.Split('.').Last();

    private static string? Service(string project) => project.StartsWith("OnVoyage.", StringComparison.Ordinal) && project.Split('.') is { Length: 3 } parts
        && new[] { "Domain", "Application", "Infrastructure", "Api", "Contracts", "Worker" }.Contains(parts[2]) ? parts[1] : null;

    [Fact]
    public void Domain_depends_on_nothing_external()
    {
        foreach (var project in Projects().Where(p => Layer(Path.GetFileNameWithoutExtension(p)) == "Domain" && Service(Path.GetFileNameWithoutExtension(p)) is not null))
        {
            Packages(project).ShouldBeEmpty(project);
            References(project).ShouldAllBe(reference => reference.EndsWith(".Domain") || reference == "OnVoyage.Taxonomy", project);
        }
    }

    [Fact]
    public void Application_never_references_infrastructure_or_api()
    {
        foreach (var project in Projects().Where(p => Layer(Path.GetFileNameWithoutExtension(p)) == "Application"))
        {
            References(project).ShouldAllBe(reference => !reference.EndsWith(".Infrastructure") && !reference.EndsWith(".Api"), project);
        }
    }

    [Fact]
    public void Infrastructure_never_references_api()
    {
        foreach (var project in Projects().Where(p => Layer(Path.GetFileNameWithoutExtension(p)) == "Infrastructure" && Service(Path.GetFileNameWithoutExtension(p)) is not null))
        {
            References(project).ShouldAllBe(reference => !reference.EndsWith(".Api"), project);
        }
    }

    [Fact]
    public void Services_only_reference_other_services_through_contracts()
    {
        foreach (var project in Projects().Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(p => Service(p) is not null))
        {
            var own = Service(project);
            var path = Projects().Single(p => Path.GetFileNameWithoutExtension(p) == project);
            References(path)
                .Where(reference => Service(reference) is { } other && other != own)
                .ShouldAllBe(reference => reference.EndsWith(".Contracts"), project);
        }
    }

    [Fact]
    public void Endpoints_and_application_never_touch_infrastructure_sources()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "src", "Services"), "*.cs", SearchOption.AllDirectories)
            .Where(file => file.Contains("/Endpoints/") || file.Contains(".Application/"))
            .Where(file => File.ReadAllText(file).Contains(".Infrastructure", StringComparison.Ordinal))
            .ToArray();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Database_packages_live_in_infrastructure_only()
    {
        foreach (var project in Projects().Where(p => Service(Path.GetFileNameWithoutExtension(p)) is not null && Layer(Path.GetFileNameWithoutExtension(p)) != "Infrastructure"))
        {
            // The Api keeps the EF Core Design package (PrivateAssets=all) for `dotnet ef` only.
            Packages(project)
                .Where(package => package.StartsWith("Npgsql", StringComparison.Ordinal) || package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
                .ShouldAllBe(package => package == "Microsoft.EntityFrameworkCore.Design", project);
        }
    }

    [Fact]
    public void Gateway_is_a_pure_proxy_without_domain_references()
    {
        var gateway = Projects().Single(p => Path.GetFileNameWithoutExtension(p) == "OnVoyage.Gateway");

        References(gateway).ShouldBe(["OnVoyage.ServiceDefaults"]);
    }

    [Fact]
    public void Shared_ui_and_client_core_do_not_depend_on_maui()
    {
        foreach (var name in new[] { "OnVoyage.UI.Components", "OnVoyage.App.Core", "OnVoyage.App.Infrastructure", "OnVoyage.Recommendation.Engine" })
        {
            var project = Projects().Single(p => Path.GetFileNameWithoutExtension(p) == name);
            Packages(project).ShouldAllBe(package => !package.Contains("Maui", StringComparison.OrdinalIgnoreCase), name);
            XDocument.Load(project).Descendants("UseMaui").ShouldBeEmpty(name);
        }
    }

    [Fact]
    public void Recommendation_engine_and_taxonomy_are_pure()
    {
        Packages(Projects().Single(p => Path.GetFileNameWithoutExtension(p) == "OnVoyage.Recommendation.Engine")).ShouldBeEmpty();
        Packages(Projects().Single(p => Path.GetFileNameWithoutExtension(p) == "OnVoyage.Taxonomy")).ShouldBeEmpty();
    }

    [Fact]
    public void No_third_party_analytics_crash_ads_or_payment_sdk()
    {
        string[] banned = ["Firebase", "Crashlytics", "Sentry", "Mixpanel", "RevenueCat", "AppCenter", "Google.Analytics", "Segment", "Amplitude"];
        var files = Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories).Append(Path.Combine(Root, "Directory.Packages.props"));

        foreach (var file in files.Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")))
        {
            var packages = XDocument.Load(file).Descendants().Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion").Select(e => (string?)e.Attribute("Include") ?? string.Empty);
            packages.ShouldAllBe(package => !banned.Any(b => package.Contains(b, StringComparison.OrdinalIgnoreCase)), file);
        }
    }

    [Fact]
    public void No_type_is_named_helper_utils_or_manager()
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"\b(class|record|struct|interface)\s+\w*(Helpers?|Utils|Utility|Manager)\b");
        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains("/obj/") && pattern.IsMatch(File.ReadAllText(file)))
            .ToArray();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void No_third_party_resources_in_client_html()
    {
        var pages = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.html", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/"));

        foreach (var page in pages)
        {
            var html = File.ReadAllText(page);
            html.ShouldNotContain("youtube.com", Case.Insensitive, page);
            html.ShouldNotContain("googleapis.com", Case.Insensitive, page);
            html.ShouldNotContain("tile.openstreetmap.org", Case.Insensitive, page);
        }
    }
}
