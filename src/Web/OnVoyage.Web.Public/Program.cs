using OnVoyage.ServiceDefaults;
using OnVoyage.Web.Public.Components;
using OnVoyage.Web.Public.Seo;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddResponseCompression();
builder.Services.AddSingleton(builder.Configuration.GetSection("Public").Get<PublicSettings>() ?? new PublicSettings());
builder.Services.AddSingleton<BlockedCrawlers>();
builder.Services.AddSingleton<ContentStore>();

var catalog = builder.Configuration["Catalog:BaseAddress"] ?? "https+http://catalog-api";
builder.Services.AddTransient<InternalTokenHandler>();
builder.Services.AddHttpClient<ICatalogPublicClient, CatalogPublicClient>(client =>
{
    client.BaseAddress = new Uri(catalog);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OnVoyage-Web/1.0");
}).AddHttpMessageHandler<InternalTokenHandler>();

// Creator pages (T-1204): the second service Web.Public reads, with the same internal token. Only published creators and validated links come back.
var creators = builder.Configuration["Creators:BaseAddress"] ?? "https+http://creators-api";
builder.Services.AddHttpClient<ICreatorsPublicClient, CreatorsPublicClient>(client =>
{
    client.BaseAddress = new Uri(creators);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OnVoyage-Web/1.0");
}).AddHttpMessageHandler<InternalTokenHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/erreur");
}

app.UseResponseCompression();
app.UseCrawlerBlocking();
app.UseTdmReservation();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapSeoEndpoints();

// D-15: every address of the former blog answers with a permanent redirect to its new place (never a 404).
foreach (var (old, target) in ContentStore.LegacyRedirects)
{
    app.MapGet(old, () => Results.Redirect(target, permanent: true));
}

app.MapRazorComponents<App>();
app.MapDefaultEndpoints();

app.Run();
