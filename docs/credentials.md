# Credentials

`gsc-mcp` reads one JSON file, set by `GSC_CREDENTIALS_PATH` (default `/credentials.json`).
It accepts two shapes and detects which one it got.

## Option 1: service account

Best for unattended use, since there is no browser step and nothing expires. If key creation fails with
"Service account key creation is disabled", your organisation blocks keys: use option 2.

1. In [Google Cloud Console](https://console.cloud.google.com/), create or pick a project.
2. Enable the **Google Search Console API** for it.
3. Under **IAM & Admin → Service Accounts**, create a service account. It needs no project roles.
4. On the account, open **Keys → Add key → JSON** and download the file.
5. In [Search Console](https://search.google.com/search-console), open the property, go to
   **Settings → Users and permissions → Add user**, and add the service account's email
   (`...@...iam.gserviceaccount.com`). *Restricted* is enough for reading; the write tools need *Full* or *Owner*.

Repeat step 5 for every property the server should see.

## Option 2: sign in with your own Google account

Use this when your organisation blocks service account keys (the default for Google Cloud
organisations created since 2024), or when you simply prefer no key file. The server then acts
as you, and sees the properties you can see.

1. In [Google Cloud Console](https://console.cloud.google.com/), pick a project and enable the
   **Google Search Console API**.
2. Under **APIs & Services → OAuth consent screen**, configure the app. With Google Workspace choose
   the **Internal** user type: no review is needed and sign-ins do not expire. With a personal
   account choose External and add yourself as a test user; Google then expires the sign-in after 7 days
   until you publish the app.
3. Under **Clients** (or **Credentials**), create an OAuth client of type **Desktop app** and download its JSON.
4. Sign in once. This prints a URL, waits for you to approve access in the browser, and writes `credentials.json`:

```bash
docker run --rm -p 8085:8085 --user "$(id -u):$(id -g)" -v "$PWD:/out" \
  gsc-mcp login /out/client_secret.json /out/credentials.json
```

Sign in as an account that can see the property in Search Console. If the property belongs to a
different account, have its owner add you under **Settings → Users and permissions** first.

Notes:

- Port 8085 must be free. To use another, set `-e GSC_LOGIN_PORT=9000` and publish that port instead.
- Add `-e GSC_ALLOW_WRITE=true` to the login command if you want the write tools. The scope is fixed
  at sign-in, so a read-only sign-in cannot write later.
- Any JSON file with `client_id`, `client_secret` and `refresh_token` works, including the
  `token.json` written by [mcp-gsc](https://github.com/AminForou/mcp-gsc).
- To revoke access, remove the app at [myaccount.google.com/permissions](https://myaccount.google.com/permissions).

## Mounting the file

```bash
docker run -i --rm -v /absolute/path/credentials.json:/credentials.json:ro gsc-mcp
```

The container runs as a non-root user, so the file must be readable by others (`chmod 644`).
Keep it out of version control.

## Troubleshooting

| Message | Cause |
|---|---|
| `Credentials file not found` | Wrong mount path, or `GSC_CREDENTIALS_PATH` points elsewhere |
| `invalid_grant` | Deleted service account or key, revoked refresh token, or a clock that is far off |
| `403 ... sufficient permission` | The account is not a user on that property (step 5) |
| `list_sites` returns `[]` | Same as above: authenticated, but added to no property |
