# Using the `Gsc` library

`src/Gsc` is a plain class library: `net10.0`, no package dependencies, no reflection,
marked `IsAotCompatible`. Reference the project (or copy its two files) into any .NET app.

```xml
<ProjectReference Include="path/to/src/Gsc/Gsc.csproj" />
```

## Auth

```csharp
var http = new HttpClient();

// From a file: service account key or OAuth user JSON, detected automatically.
var auth = GoogleAuth.FromFile(http, "credentials.json");                       // read-only
var rw   = GoogleAuth.FromFile(http, "credentials.json", GoogleAuth.ReadWriteScope);

// Or from values you already hold, such as a refresh token stored in your database.
var user = new GoogleAuth(http,
    new GoogleCredentials(RefreshToken: token, ClientId: id, ClientSecret: secret),
    GoogleAuth.ReadOnlyScope);
```

`GoogleAuth` caches the access token until a minute before it expires and is safe to share across threads.
Use one instance per Google account.

## Client

```csharp
var gsc = new GscClient(http, auth);

IReadOnlyList<Site> sites = await gsc.ListSitesAsync();
Site site = await gsc.GetSiteAsync("sc-domain:example.com");

SearchAnalyticsResult result = await gsc.QuerySearchAnalyticsAsync("sc-domain:example.com",
    new SearchAnalyticsQuery("2026-09-01", "2026-09-28",
        Dimensions: ["page", "device"],
        DimensionFilterGroups: [new([new SearchFilter("query", "pricing")])],
        RowLimit: 1000));

JsonElement inspection = await gsc.InspectUrlAsync("sc-domain:example.com", "https://example.com/pricing");

IReadOnlyList<Sitemap> sitemaps = await gsc.ListSitemapsAsync("sc-domain:example.com");
Sitemap sitemap = await gsc.GetSitemapAsync("sc-domain:example.com", "https://example.com/sitemap.xml");

// These need GoogleAuth.ReadWriteScope.
await gsc.SubmitSitemapAsync("sc-domain:example.com", "https://example.com/sitemap.xml");
await gsc.DeleteSitemapAsync("sc-domain:example.com", "https://example.com/sitemap.xml");
await gsc.AddSiteAsync("https://example.com/");
await gsc.DeleteSiteAsync("https://example.com/");
```

`result.Rows` is never null. `result.Metadata` is set when the range includes data Google is still processing.

Failed calls throw `HttpRequestException` with the status code and Google's error body in the message.

## Serializing results under AOT

`GscJson` carries source-generated metadata for the public types:

```csharp
string json = JsonSerializer.Serialize(result, GscJson.Default.SearchAnalyticsResult);
```

## Notes

- Search analytics returns at most 25,000 rows per call. Page with `StartRow`.
- `InspectUrlAsync` returns raw JSON because the result is a deep tree. Add typed records if you need them.
- With `HttpClientFactory`, register `GoogleAuth` as a singleton per account so the token cache survives.
