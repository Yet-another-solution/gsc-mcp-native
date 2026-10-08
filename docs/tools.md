# Tool reference

Every tool returns JSON as text. `siteUrl` is always the property exactly as `list_sites`
returns it: `https://example.com/` for URL-prefix properties, `sc-domain:example.com` for domain properties.

## list_sites

No arguments.

```json
[{ "siteUrl": "sc-domain:example.com", "permissionLevel": "siteOwner" }]
```

## get_site

| Argument | Required |
|---|---|
| `siteUrl` | yes |

```json
{ "siteUrl": "sc-domain:example.com", "permissionLevel": "siteOwner" }
```

Permission levels: `siteOwner`, `siteFullUser`, `siteRestrictedUser`, `siteUnverifiedUser`.

## query_search_analytics

| Argument | Required | Notes |
|---|---|---|
| `siteUrl` | yes | |
| `startDate` | no | `yyyy-MM-dd`. Default: 28 days ending at `endDate` |
| `endDate` | no | `yyyy-MM-dd`. Default: 3 days ago, the newest finalized data |
| `dimensions` | no | Any of `query`, `page`, `country`, `device`, `date`, `searchAppearance`, `hour`. Omit for totals |
| `filters` | no | Array of `{dimension, expression, operator}`, all must match |
| `type` | no | `web` (default), `image`, `video`, `news`, `discover`, `googleNews` |
| `rowLimit` | no | 1 to 25000, default 100 |
| `startRow` | no | Offset for paging |
| `dataState` | no | `final` (default), `all` to include fresh data, `hourly_all` for hourly data (use with the `hour` dimension) |
| `aggregationType` | no | `auto` (default), `byProperty`, `byPage`, `byNewsShowcasePanel` |

Filter operators: `contains` (default), `equals`, `notContains`, `notEquals`, `includingRegex`, `excludingRegex`.

Example: top queries landing on the blog.

```json
{
  "siteUrl": "sc-domain:example.com",
  "dimensions": ["query"],
  "filters": [{ "dimension": "page", "expression": "/blog/" }],
  "rowLimit": 20
}
```

```json
{
  "rows": [{ "keys": ["how to bake bread"], "clicks": 42, "impressions": 910, "ctr": 0.046, "position": 6.3 }],
  "responseAggregationType": "byPage"
}
```

`keys` lines up with `dimensions`, in order. A `metadata` object with `firstIncompleteDate` appears
when the range includes data Google is still processing. To compare periods, call the tool once per date range.

## inspect_url

| Argument | Required |
|---|---|
| `siteUrl` | yes |
| `url` | yes, fully qualified and inside the property |
| `languageCode` | no, BCP-47 language for translated issue messages, default `en-US` |

Returns Google's URL Inspection result unchanged: `indexStatusResult` (verdict, coverage state,
last crawl time, Google and user canonical, robots state), plus mobile usability and rich results when present.
Google limits this API to about 2,000 inspections per property per day.

## list_sitemaps

| Argument | Required |
|---|---|
| `siteUrl` | yes |
| `sitemapIndex` | no, URL of a sitemap index, to list only the sitemaps inside it |

```json
[{
  "path": "https://example.com/sitemap.xml",
  "lastSubmitted": "2026-09-01T08:00:00+00:00",
  "isPending": false, "isSitemapsIndex": false,
  "warnings": 0, "errors": 0,
  "contents": [{ "type": "web", "submitted": 120, "indexed": 0 }]
}]
```

Google has deprecated `indexed`; expect 0.

## get_sitemap

Takes `siteUrl` and `sitemapUrl`, returns one object of the shape above.

## Write tools

Registered only when `GSC_ALLOW_WRITE=true`.

| Tool | Arguments | Effect |
|---|---|---|
| `add_site` | `siteUrl` | Adds the property to the account. It stays unverified until you prove ownership in Search Console |
| `delete_site` | `siteUrl` | Removes the property from the account. Google keeps the data; adding it again restores access |
| `submit_sitemap` | `siteUrl`, `sitemapUrl` | Submits or resubmits a sitemap |
| `delete_sitemap` | `siteUrl`, `sitemapUrl` | Removes the sitemap from Search Console, not from your site |

## Not covered

Google's API description still lists a Mobile-Friendly Test method. It is left out: it takes an API
key instead of these credentials, and Google announced the retirement of that tool in 2023.
