#:project ../src/Gsc/Gsc.csproj
// Self-check for the Gsc library against a fake Google: `dotnet run test/GscTests.cs`
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gsc;

using var rsa = RSA.Create(2048);
var fake = new FakeGoogle();
var http = new HttpClient(fake);
var auth = new GoogleAuth(http, new GoogleCredentials("sa@test.iam.gserviceaccount.com", rsa.ExportPkcs8PrivateKeyPem()), GoogleAuth.ReadOnlyScope);
var gsc = new GscClient(http, auth);

// Service account: a JWT the public key verifies, exchanged once and then cached.
fake.Reply("""{"siteEntry":[{"siteUrl":"sc-domain:example.com","permissionLevel":"siteOwner"}]}""");
var sites = await gsc.ListSitesAsync();
Check(sites is [{ SiteUrl: "sc-domain:example.com", PermissionLevel: "siteOwner" }], "sites parsed");
var jwt = fake.TokenForm["assertion"].Split('.');
Check(rsa.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Unbase64Url(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "JWT signature");
var claims = JsonDocument.Parse(Unbase64Url(jwt[1])).RootElement;
Check(claims.GetProperty("iss").GetString() == "sa@test.iam.gserviceaccount.com" && claims.GetProperty("scope").GetString() == GoogleAuth.ReadOnlyScope, "JWT claims");
Check(fake.Last.Headers.Authorization?.ToString() == "Bearer tok", "bearer header");

// Search analytics: site URL escaped into one path segment, nulls left out of the body.
fake.Reply("""{"rows":[{"keys":["shoes"],"clicks":3,"impressions":40,"ctr":0.075,"position":4.2}],"responseAggregationType":"byProperty","metadata":{"firstIncompleteDate":"2026-01-27"}}""");
var result = await gsc.QuerySearchAnalyticsAsync("https://example.com/", new("2026-01-01", "2026-01-28", ["query"],
    DimensionFilterGroups: [new([new("page", "/blog")])], RowLimit: 10));
Check(result is { Rows: [{ Keys: ["shoes"], Clicks: 3, Position: 4.2 }], ResponseAggregationType: "byProperty", Metadata.FirstIncompleteDate: "2026-01-27" }, "result parsed");
Check(fake.Last.RequestUri!.AbsoluteUri == "https://www.googleapis.com/webmasters/v3/sites/https%3A%2F%2Fexample.com%2F/searchAnalytics/query", "escaped site url: " + fake.Last.RequestUri.AbsoluteUri);
Check(fake.LastBody == """{"startDate":"2026-01-01","endDate":"2026-01-28","dimensions":["query"],"dimensionFilterGroups":[{"filters":[{"dimension":"page","expression":"/blog","operator":"contains"}],"groupType":"and"}],"rowLimit":10}""", "query body: " + fake.LastBody);
Check(fake.TokenRequests == 1, "token cached");

// Sitemaps: Google's string-encoded int64s.
fake.Reply("""{"sitemap":[{"path":"https://example.com/sitemap.xml","lastSubmitted":"2026-01-02T03:04:05.000Z","isPending":false,"isSitemapsIndex":false,"warnings":"2","errors":"0","contents":[{"type":"web","submitted":"120","indexed":"0"}]}]}""");
var sitemaps = await gsc.ListSitemapsAsync("sc-domain:example.com");
Check(sitemaps is [{ Warnings: 2, Contents: [{ Submitted: 120 }] }], "sitemaps parsed");

// Every other method hits the documented verb and path.
fake.Reply("{}");
Check((await gsc.QuerySearchAnalyticsAsync("sc-domain:example.com", new("2026-01-01", "2026-01-28"))).Rows is [], "no rows is an empty list");
fake.Reply("""{"siteUrl":"sc-domain:example.com","permissionLevel":"siteFullUser"}""");
Check((await gsc.GetSiteAsync("sc-domain:example.com")).PermissionLevel == "siteFullUser" && Sent("GET", "/sites/sc-domain%3Aexample.com"), "get site");
fake.Reply("");
await gsc.AddSiteAsync("sc-domain:example.com"); Check(Sent("PUT", "/sites/sc-domain%3Aexample.com"), "add site");
await gsc.DeleteSiteAsync("sc-domain:example.com"); Check(Sent("DELETE", "/sites/sc-domain%3Aexample.com"), "delete site");
const string sitemap = "/sites/sc-domain%3Aexample.com/sitemaps/https%3A%2F%2Fexample.com%2Fsitemap.xml";
await gsc.SubmitSitemapAsync("sc-domain:example.com", "https://example.com/sitemap.xml"); Check(Sent("PUT", sitemap), "submit sitemap");
await gsc.DeleteSitemapAsync("sc-domain:example.com", "https://example.com/sitemap.xml"); Check(Sent("DELETE", sitemap), "delete sitemap");
fake.Reply("""{"path":"https://example.com/sitemap.xml","errors":"1"}""");
Check((await gsc.GetSitemapAsync("sc-domain:example.com", "https://example.com/sitemap.xml")).Errors == 1 && Sent("GET", sitemap), "get sitemap");
fake.Reply("{}");
await gsc.ListSitemapsAsync("sc-domain:example.com", "https://example.com/index.xml");
Check(Sent("GET", "/sites/sc-domain%3Aexample.com/sitemaps?sitemapIndex=https%3A%2F%2Fexample.com%2Findex.xml"), "sitemap index filter");
fake.Reply("""{"inspectionResult":{"indexStatusResult":{"verdict":"PASS"}}}""");
var inspection = await gsc.InspectUrlAsync("sc-domain:example.com", "https://example.com/", "en-US");
Check(inspection.GetProperty("inspectionResult").GetProperty("indexStatusResult").GetProperty("verdict").GetString() == "PASS", "inspection parsed");
Check(fake.Last.RequestUri!.AbsoluteUri == "https://searchconsole.googleapis.com/v1/urlInspection/index:inspect" && fake.LastBody == """{"inspectionUrl":"https://example.com/","siteUrl":"sc-domain:example.com","languageCode":"en-US"}""", "inspect request: " + fake.LastBody);

// Errors carry Google's message.
fake.Reply("""{"error":{"message":"User does not have sufficient permission"}}""", HttpStatusCode.Forbidden);
try { await gsc.InspectUrlAsync("sc-domain:example.com", "https://example.com/"); Check(false, "403 should throw"); }
catch (HttpRequestException e) { Check(e.StatusCode == HttpStatusCode.Forbidden && e.Message.Contains("sufficient permission"), "error message"); }

// OAuth user credentials use the refresh_token grant.
var userAuth = new GoogleAuth(http, new GoogleCredentials(RefreshToken: "r", ClientId: "id", ClientSecret: "s"), GoogleAuth.ReadOnlyScope);
await userAuth.GetAccessTokenAsync();
Check(fake.TokenForm["grant_type"] == "refresh_token" && fake.TokenForm["refresh_token"] == "r", "refresh grant");

Console.WriteLine("ok");

bool Sent(string method, string pathAndQuery) =>
    fake.Last.Method.Method == method && fake.Last.RequestUri!.AbsoluteUri == "https://www.googleapis.com/webmasters/v3" + pathAndQuery;
static void Check(bool ok, string what) { if (!ok) throw new Exception("FAILED: " + what); }
static byte[] Unbase64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

sealed class FakeGoogle : HttpMessageHandler
{
    private (string Body, HttpStatusCode Status) _next;
    public HttpRequestMessage Last = null!;
    public string? LastBody;
    public Dictionary<string, string> TokenForm = [];
    public int TokenRequests;

    public void Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => _next = (body, status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        if (request.RequestUri!.Host == "oauth2.googleapis.com")
        {
            TokenRequests++;
            TokenForm = body!.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            return Json("""{"access_token":"tok","expires_in":3600,"token_type":"Bearer"}""", HttpStatusCode.OK);
        }
        (Last, LastBody) = (request, body);
        return Json(_next.Body, _next.Status);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
