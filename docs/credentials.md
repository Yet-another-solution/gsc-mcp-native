# Credentials

`gsc-mcp` reads one JSON file, set by `GSC_CREDENTIALS_PATH` (default `/credentials.json`).
It accepts two shapes and detects which one it got.

## Option 1: service account (recommended)

Best for containers, since there is no browser step and nothing expires.

1. In [Google Cloud Console](https://console.cloud.google.com/), create or pick a project.
2. Enable the **Google Search Console API** for it.
3. Under **IAM & Admin → Service Accounts**, create a service account. It needs no project roles.
4. On the account, open **Keys → Add key → JSON** and download the file.
5. In [Search Console](https://search.google.com/search-console), open the property, go to
   **Settings → Users and permissions → Add user**, and add the service account's email
   (`...@...iam.gserviceaccount.com`). *Restricted* is enough for reading; the write tools need *Full* or *Owner*.

Repeat step 5 for every property the server should see.

## Option 2: OAuth user (refresh token)

Any JSON file with these three fields works:

```json
{
  "client_id": "....apps.googleusercontent.com",
  "client_secret": "...",
  "refresh_token": "..."
}
```

The `token.json` that [mcp-gsc](https://github.com/AminForou/mcp-gsc) writes after its browser login has this shape.
The server does not run a browser login itself, so obtain the refresh token outside the container.

The scope was fixed when the refresh token was issued. If it was issued read-only, the write tools will fail even with `GSC_ALLOW_WRITE=true`.

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
