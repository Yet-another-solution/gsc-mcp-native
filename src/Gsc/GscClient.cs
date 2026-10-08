using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Gsc;

/// <summary>Google Search Console REST client. No reflection, safe for NativeAOT.</summary>
public sealed class GscClient(HttpClient http, GoogleAuth auth)
{
    private const string Webmasters = "https://www.googleapis.com/webmasters/v3/sites";
    private const string Inspect = "https://searchconsole.googleapis.com/v1/urlInspection/index:inspect";

    public async Task<IReadOnlyList<Site>> ListSitesAsync(CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get, Webmasters, WireJson.Default.SiteList, ct))?.SiteEntry ?? [];

    public async Task<Site> GetSiteAsync(string siteUrl, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get, SiteUri(siteUrl), GscJson.Default.Site, ct))!;

    /// <summary>Adds the property to the account. It stays unverified until ownership is proven in Search Console.</summary>
    public async Task AddSiteAsync(string siteUrl, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Put, SiteUri(siteUrl), ct)).Dispose();

    /// <summary>Removes the property from the account. Google keeps its data, re-adding restores access.</summary>
    public async Task DeleteSiteAsync(string siteUrl, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Delete, SiteUri(siteUrl), ct)).Dispose();

    /// <summary>Dates are yyyy-MM-dd. Google caps <c>RowLimit</c> at 25,000; page with <c>StartRow</c>.</summary>
    public async Task<SearchAnalyticsResult> QuerySearchAnalyticsAsync(
        string siteUrl, SearchAnalyticsQuery query, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, $"{SiteUri(siteUrl)}/searchAnalytics/query",
            GscJson.Default.SearchAnalyticsResult, ct, JsonContent.Create(query, GscJson.Default.SearchAnalyticsQuery))
        ?? new SearchAnalyticsResult();

    // ponytail: returned as raw JSON, the inspection result is a deep tree nobody here reads field by field.
    // Add typed records when a caller needs them.
    public Task<JsonElement> InspectUrlAsync(
        string siteUrl, string url, string? languageCode = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, Inspect, WireJson.Default.JsonElement, ct,
            JsonContent.Create(new InspectRequest(url, siteUrl, languageCode), WireJson.Default.InspectRequest));

    /// <param name="sitemapIndex">Only list the sitemaps inside this sitemap index.</param>
    public async Task<IReadOnlyList<Sitemap>> ListSitemapsAsync(
        string siteUrl, string? sitemapIndex = null, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get,
            $"{SiteUri(siteUrl)}/sitemaps{(sitemapIndex is null ? "" : "?sitemapIndex=" + Uri.EscapeDataString(sitemapIndex))}",
            WireJson.Default.SitemapList, ct))?.Sitemap ?? [];

    public async Task<Sitemap> GetSitemapAsync(string siteUrl, string sitemapUrl, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Get, SitemapUri(siteUrl, sitemapUrl), GscJson.Default.Sitemap, ct))!;

    public async Task SubmitSitemapAsync(string siteUrl, string sitemapUrl, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Put, SitemapUri(siteUrl, sitemapUrl), ct)).Dispose();

    public async Task DeleteSitemapAsync(string siteUrl, string sitemapUrl, CancellationToken ct = default) =>
        (await SendAsync(HttpMethod.Delete, SitemapUri(siteUrl, sitemapUrl), ct)).Dispose();

    private static string SiteUri(string siteUrl) => $"{Webmasters}/{Uri.EscapeDataString(siteUrl)}";

    private static string SitemapUri(string siteUrl, string sitemapUrl) =>
        $"{SiteUri(siteUrl)}/sitemaps/{Uri.EscapeDataString(sitemapUrl)}";

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string uri, JsonTypeInfo<T> type, CancellationToken ct, HttpContent? body = null)
    {
        using var response = await SendAsync(method, uri, ct, body);
        return await response.Content.ReadFromJsonAsync(type, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string uri, CancellationToken ct, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(ct));
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;
        using (response)
            throw new HttpRequestException(
                $"Search Console API {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
    }
}

public sealed record Site(string SiteUrl, string PermissionLevel);

public sealed record SearchAnalyticsQuery(
    string StartDate,
    string EndDate,
    IReadOnlyList<string>? Dimensions = null,
    string? Type = null,
    IReadOnlyList<SearchFilterGroup>? DimensionFilterGroups = null,
    string? AggregationType = null,
    int? RowLimit = null,
    int? StartRow = null,
    string? DataState = null);

public sealed record SearchFilterGroup(IReadOnlyList<SearchFilter> Filters, string GroupType = "and");

/// <param name="Dimension">query, page, country, device or searchAppearance.</param>
/// <param name="Expression">Value to match.</param>
/// <param name="Operator">contains, equals, notContains, notEquals, includingRegex or excludingRegex.</param>
public sealed record SearchFilter(string Dimension, string Expression, string Operator = "contains");

public sealed record SearchRow(IReadOnlyList<string>? Keys, double Clicks, double Impressions, double Ctr, double Position);

public sealed record SearchAnalyticsResult(
    IReadOnlyList<SearchRow>? Rows = null,
    string? ResponseAggregationType = null,
    SearchAnalyticsMetadata? Metadata = null)
{
    public IReadOnlyList<SearchRow> Rows { get; init; } = Rows ?? [];
}

/// <summary>Present when the range includes data Google is still processing.</summary>
public sealed record SearchAnalyticsMetadata(string? FirstIncompleteDate, string? FirstIncompleteHour);

public sealed record Sitemap(
    string Path,
    DateTimeOffset? LastSubmitted,
    DateTimeOffset? LastDownloaded,
    bool IsPending,
    bool IsSitemapsIndex,
    string? Type,
    long Warnings,
    long Errors,
    IReadOnlyList<SitemapContent>? Contents);

public sealed record SitemapContent(string Type, long Submitted, long Indexed);

internal sealed record SiteList(IReadOnlyList<Site>? SiteEntry);
internal sealed record SitemapList(IReadOnlyList<Sitemap>? Sitemap);
internal sealed record InspectRequest(string InspectionUrl, string SiteUrl, string? LanguageCode);

// Google sends int64 as JSON strings, hence AllowReadingFromString.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(SiteList))]
[JsonSerializable(typeof(SitemapList))]
[JsonSerializable(typeof(InspectRequest))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class WireJson : JsonSerializerContext;

/// <summary>Source-generated metadata for the public types, for callers that serialize them under AOT.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(IReadOnlyList<Site>))]
[JsonSerializable(typeof(SearchAnalyticsResult))]
[JsonSerializable(typeof(IReadOnlyList<Sitemap>))]
[JsonSerializable(typeof(SearchAnalyticsQuery))]
[JsonSerializable(typeof(SearchFilter[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class GscJson : JsonSerializerContext;
