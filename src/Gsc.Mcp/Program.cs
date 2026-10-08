using System.ComponentModel;
using System.Text.Json;
using Gsc;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var credentialsPath = Environment.GetEnvironmentVariable("GSC_CREDENTIALS_PATH") ?? "/credentials.json";
var allowWrite = Environment.GetEnvironmentVariable("GSC_ALLOW_WRITE") is "true" or "1";

if (!File.Exists(credentialsPath))
{
    // stdout is the MCP channel, so diagnostics go to stderr.
    Console.Error.WriteLine($"Credentials file not found: {credentialsPath} (set GSC_CREDENTIALS_PATH)");
    return 1;
}

var http = new HttpClient();
var gsc = new GscClient(http, GoogleAuth.FromFile(http, credentialsPath,
    allowWrite ? GoogleAuth.ReadWriteScope : GoogleAuth.ReadOnlyScope));

var json = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
json.TypeInfoResolverChain.Add(GscJson.Default);

McpServerTool Tool(string name, Delegate handler, bool write = false) => McpServerTool.Create(handler,
    new() { Name = name, ReadOnly = !write, Destructive = write, OpenWorld = true, SerializerOptions = json });

// Surfaces Google's error text to the model instead of the SDK's generic "an error occurred".
static async Task<string> Run(Func<Task<string>> call)
{
    try { return await call(); }
    catch (Exception e) when (e is HttpRequestException or InvalidDataException) { throw new McpException(e.Message); }
}

List<McpServerTool> tools =
[
    Tool("list_sites",
        [Description("List the Search Console properties this account can access, with permission level.")]
        (CancellationToken ct) => Run(async () =>
            JsonSerializer.Serialize(await gsc.ListSitesAsync(ct), GscJson.Default.IReadOnlyListSite))),

    Tool("get_site",
        [Description("Permission level this account has on one property.")]
        ([Description("Property as list_sites returns it.")] string siteUrl, CancellationToken ct) => Run(async () =>
            JsonSerializer.Serialize(await gsc.GetSiteAsync(siteUrl, ct), GscJson.Default.Site))),

    Tool("query_search_analytics",
        [Description("Search performance (clicks, impressions, ctr, position) for a property. " +
            "Call it twice with different date ranges to compare periods.")]
        (
            [Description("Property exactly as list_sites returns it, e.g. https://example.com/ or sc-domain:example.com")] string siteUrl,
            [Description("yyyy-MM-dd. Defaults to 28 days before endDate.")] string? startDate = null,
            [Description("yyyy-MM-dd. Defaults to 3 days ago, the newest finalized data.")] string? endDate = null,
            [Description("Group by any of: query, page, country, device, date, searchAppearance, hour (hour needs dataState hourly_all). Omit for totals.")] string[]? dimensions = null,
            [Description("Filters, all must match. dimension: query|page|country|device|searchAppearance; " +
                "operator: contains|equals|notContains|notEquals|includingRegex|excludingRegex.")] SearchFilter[]? filters = null,
            [Description("web (default), image, video, news, discover or googleNews.")] string? type = null,
            [Description("Rows to return, 1-25000. Default 100.")] int? rowLimit = null,
            [Description("Zero-based offset for paging.")] int? startRow = null,
            [Description("final (default), all (adds fresh, not yet finalized data) or hourly_all (hourly data for recent days).")] string? dataState = null,
            [Description("auto (default), byProperty, byPage or byNewsShowcasePanel.")] string? aggregationType = null,
            CancellationToken ct = default) => Run(async () =>
        {
            var end = endDate is null ? DateTime.UtcNow.Date.AddDays(-3) : ParseDate(endDate);
            var start = startDate is null ? end.AddDays(-27) : ParseDate(startDate);
            var result = await gsc.QuerySearchAnalyticsAsync(siteUrl, new SearchAnalyticsQuery(
                start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), dimensions, type,
                filters is { Length: > 0 } ? [new SearchFilterGroup(filters)] : null,
                aggregationType, rowLimit ?? 100, startRow, dataState), ct);
            return JsonSerializer.Serialize(result, GscJson.Default.SearchAnalyticsResult);
        })),

    Tool("inspect_url",
        [Description("Index status of one URL as Google sees it: coverage, last crawl, canonical, mobile usability, rich results.")]
        (
            [Description("Property the URL belongs to, as list_sites returns it.")] string siteUrl,
            [Description("Fully qualified URL to inspect.")] string url,
            [Description("BCP-47 language for translated issue messages, e.g. en-US (default).")] string? languageCode = null,
            CancellationToken ct = default) => Run(async () => (await gsc.InspectUrlAsync(siteUrl, url, languageCode, ct)).GetRawText())),

    Tool("list_sitemaps",
        [Description("Sitemaps submitted for a property, with last download time, errors, warnings and indexed counts.")]
        (
            [Description("Property as list_sites returns it.")] string siteUrl,
            [Description("URL of a sitemap index, to list only the sitemaps inside it.")] string? sitemapIndex = null,
            CancellationToken ct = default) => Run(async () =>
            JsonSerializer.Serialize(await gsc.ListSitemapsAsync(siteUrl, sitemapIndex, ct), GscJson.Default.IReadOnlyListSitemap))),

    Tool("get_sitemap",
        [Description("Details of one submitted sitemap.")]
        (string siteUrl, [Description("Full URL of the sitemap.")] string sitemapUrl, CancellationToken ct) => Run(async () =>
            JsonSerializer.Serialize(await gsc.GetSitemapAsync(siteUrl, sitemapUrl, ct), GscJson.Default.Sitemap))),
];

if (allowWrite)
{
    tools.Add(Tool("add_site",
        [Description("Add a property to this account. It stays unverified until ownership is proven in Search Console.")]
        ([Description("https://example.com/ for a URL-prefix property, sc-domain:example.com for a domain property.")] string siteUrl,
            CancellationToken ct) => Run(async () =>
        {
            await gsc.AddSiteAsync(siteUrl, ct);
            return $"Added {siteUrl}";
        }), write: true));

    tools.Add(Tool("delete_site",
        [Description("Remove a property from this account. Google keeps the data, adding it again restores access.")]
        ([Description("Property as list_sites returns it.")] string siteUrl, CancellationToken ct) => Run(async () =>
        {
            await gsc.DeleteSiteAsync(siteUrl, ct);
            return $"Removed {siteUrl}";
        }), write: true));

    tools.Add(Tool("submit_sitemap",
        [Description("Submit (or resubmit) a sitemap for a property.")]
        (string siteUrl, [Description("Full URL of the sitemap.")] string sitemapUrl, CancellationToken ct) => Run(async () =>
        {
            await gsc.SubmitSitemapAsync(siteUrl, sitemapUrl, ct);
            return $"Submitted {sitemapUrl}";
        }), write: true));

    tools.Add(Tool("delete_sitemap",
        [Description("Remove a sitemap from a property. Does not delete the file itself.")]
        (string siteUrl, [Description("Full URL of the sitemap.")] string sitemapUrl, CancellationToken ct) => Run(async () =>
        {
            await gsc.DeleteSitemapAsync(siteUrl, sitemapUrl, ct);
            return $"Deleted {sitemapUrl}";
        }), write: true));
}

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "gsc-mcp", Version = "0.1.0" },
    ToolCollection = [.. tools],
};

await using var server = McpServer.Create(new StdioServerTransport(options), options);
await server.RunAsync();
return 0;

static DateTime ParseDate(string value) =>
    DateTime.TryParseExact(value, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)
        ? date
        : throw new McpException($"'{value}' is not a yyyy-MM-dd date");
