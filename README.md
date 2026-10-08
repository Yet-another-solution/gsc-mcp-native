<p align="center">
  <img alt="gsc-mcp-native" src="docs/images/logo.svg" width="96">
</p>

<h1 align="center">gsc-mcp-native</h1>

<p align="center">
  <b>Google Search Console for AI assistants, as a single native binary.</b><br>
  An MCP server written in C# and compiled with NativeAOT. No runtime, no Python, no Node.<br>
  Claude Code · Claude Desktop · Cursor · any MCP client
</p>

<p align="center">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-0f766e">
  <img alt="Written in C#" src="https://img.shields.io/badge/written%20in-C%23%20%C2%B7%20.NET%2010-115e59">
  <img alt="Compiled with NativeAOT" src="https://img.shields.io/badge/compiled-NativeAOT-0f766e">
  <img alt="Docker image: 27 MB" src="https://img.shields.io/badge/docker%20image-27%20MB-115e59">
  <img alt="Read-only by default" src="https://img.shields.io/badge/read--only-by%20default-0f766e">
</p>

<p align="center">
  <a href="#highlights">Highlights</a> ·
  <a href="#get-started">Get started</a> ·
  <a href="#ask-your-search-data-anything">Tools</a> ·
  <a href="#credentials-without-a-browser">Credentials</a> ·
  <a href="#read-only-until-you-say-otherwise">Safety</a> ·
  <a href="#a-library-you-can-lift-out">Library</a> ·
  <a href="#how-its-built">How it's built</a> ·
  <a href="#whats-next">What's next</a>
</p>

<br>

```
  you  ──►  Claude / any MCP client  ──stdio──►  gsc-mcp  ──HTTPS──►  Search Console API
```

> [!NOTE]
> **This is an early release (0.1).** The client is checked against a simulated Google API and the
> container against Google's real token endpoint. If something behaves differently on your
> property, please open an issue.

---

## Highlights

### Small

One statically compiled executable on a distroless base image. The whole Docker image is 27 MB,
and there is no interpreter or runtime inside it to patch, warm up or keep in sync.

### Careful

It starts read-only and asks Google for the read-only scope. The tools that change anything are
not hidden behind a confirmation; they are not registered at all until you opt in.

### Yours

No account, no telemetry, no hosted middleman. Your credentials stay in a file you mount, and
the only host the server talks to is Google.

## Get started

```bash
git clone https://github.com/MatejMa2ur/gsc-mcp-native && cd gsc-mcp-native
docker build -t gsc-mcp .
```

Create a credentials file ([two-minute guide](docs/credentials.md)), then register the server.

**Claude Code**

```bash
claude mcp add gsc -- docker run -i --rm \
  -v /absolute/path/credentials.json:/credentials.json:ro gsc-mcp
```

**Claude Desktop, Cursor and other clients**

```json
{
  "mcpServers": {
    "gsc": {
      "command": "docker",
      "args": ["run", "-i", "--rm",
               "-v", "/absolute/path/credentials.json:/credentials.json:ro",
               "gsc-mcp"]
    }
  }
}
```

## Ask your search data anything

> Which queries lost the most clicks on example.com this month compared to last month?
>
> Is https://example.com/pricing indexed? If not, why?
>
> Which pages rank on page two for queries with more than 1,000 impressions?

The whole Search Console API, one tool per method and nothing invented on top, so the model
spends its attention on your question and not on choosing between near-duplicates.

| Tool | API method | What it does |
|---|---|---|
| `list_sites` | `sites.list` | Properties the account can access, with permission level |
| `get_site` | `sites.get` | Permission level on one property |
| `query_search_analytics` | `searchanalytics.query` | Clicks, impressions, CTR and position, with every dimension, filter, search type, aggregation and data state the API offers |
| `inspect_url` | `urlInspection.index.inspect` | Index status, last crawl, canonical and rich results for one URL |
| `list_sitemaps` | `sitemaps.list` | Submitted sitemaps with errors and warnings, optionally inside one sitemap index |
| `get_sitemap` | `sitemaps.get` | Details of one sitemap |
| `add_site` | `sites.add` | Write mode only |
| `delete_site` | `sites.delete` | Write mode only |
| `submit_sitemap` | `sitemaps.submit` | Write mode only |
| `delete_sitemap` | `sitemaps.delete` | Write mode only |

Dates default to the last 28 days of finalized data, so a bare question works without the model
doing calendar arithmetic. Every argument and a sample response: [docs/tools.md](docs/tools.md).

## Credentials without a browser

The server reads one JSON file and works out what it was given:

- **A service account key.** The right choice for containers: no login step, nothing to refresh by hand.
- **An OAuth user file** with `client_id`, `client_secret` and `refresh_token`.

```bash
docker run -i --rm -v /absolute/path/credentials.json:/credentials.json:ro gsc-mcp
```

When Google refuses a call, its own error text is passed back to the assistant, so "the service
account is not a user on this property" reads as exactly that. Setup steps and a troubleshooting
table: [docs/credentials.md](docs/credentials.md).

## Read-only until you say otherwise

| Variable | Default | Meaning |
|---|---|---|
| `GSC_CREDENTIALS_PATH` | `/credentials.json` | Service account key or OAuth user file |
| `GSC_ALLOW_WRITE` | off | `true` adds `add_site`, `delete_site`, `submit_sitemap` and `delete_sitemap`, and requests the read-write scope |

```bash
docker run -i --rm -e GSC_ALLOW_WRITE=true \
  -v /absolute/path/credentials.json:/credentials.json:ro gsc-mcp
```

Write tools are marked destructive in their MCP annotations, so clients that ask before running
such tools will ask.

## A library you can lift out

The Search Console client lives in its own project, `src/Gsc`: two files, no package
dependencies, no reflection, safe for NativeAOT and trimming. Reference it from any .NET app.

```csharp
using Gsc;

var http = new HttpClient();
var gsc = new GscClient(http, GoogleAuth.FromFile(http, "credentials.json"));

var result = await gsc.QuerySearchAnalyticsAsync("sc-domain:example.com",
    new("2026-09-01", "2026-09-28", Dimensions: ["query"], RowLimit: 10));

foreach (var row in result.Rows)
    Console.WriteLine($"{row.Keys![0]}: {row.Clicks} clicks, position {row.Position:0.0}");
```

Already hold refresh tokens in your own database? Pass them in directly. See [docs/library.md](docs/library.md).

## How it's built

| Path | What it is |
|---|---|
| `src/Gsc` | REST client and Google auth. Service account JWTs are signed with the standard library. |
| `src/Gsc.Mcp` | The MCP server: one `Program.cs` on the official [C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), published with NativeAOT. |
| `test/GscTests.cs` | Self-check of every API method against a simulated Google. |
| `docs/` | Credentials, tool reference, library usage. |
| `Dockerfile` | Multi-stage NativeAOT build onto a chiseled `runtime-deps` image. |

- **No Google client library.** Google's .NET SDK leans on reflection; a few hundred lines of `HttpClient` and source-generated JSON do the same job and compile cleanly.
- **Zero AOT and trim warnings** on publish.
- **Non-root container** with no shell and no package manager.

Needs the .NET 10 SDK to work on:

```bash
dotnet build                       # everything
dotnet run test/GscTests.cs        # self-check, prints "ok"
dotnet run --project src/Gsc.Mcp   # run the server over stdio without Docker
```

## What's next

- Streamable HTTP transport, for running it as a shared remote server
- Typed records for URL inspection results in the library
- A published image, so `docker build` becomes `docker pull`

## License and credits

[MIT](LICENSE).

Inspired by [AminForou/mcp-gsc](https://github.com/AminForou/mcp-gsc), the Python server that set
the bar for this category. This is an independent implementation against Google's public REST API.

Google Search Console is a trademark of Google LLC. This project is not affiliated with or
endorsed by Google.
