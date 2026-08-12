# OAuth providers & connector catalog (SQLite)

After deploying a build that includes migration `AddOAuthProvidersAndConnectors`, tables are created automatically via `Database.Migrate()` — **existing Users are not dropped**.

## Backup first

```bash
cp /var/www/wizionic/data/homeserver.db /var/www/wizionic/data/homeserver.db.bak-$(date +%F)
```

## Tables

- **OAuthProviders** — ClientId / ClientSecret / RedirectUri per provider (`github`, `google`, …)
- **Connectors** — Featured marketplace tiles (icons, scopes, link to provider)

Client secrets: insert **plaintext** is OK. The host dual-reads (tries Data Protection decrypt, then plaintext). Prefer protecting via a future admin API.

## Seed GitHub (example)

```sql
INSERT INTO OAuthProviders (
  Id, ProviderId, DisplayName, ClientId, ClientSecretProtected,
  RedirectUri, AuthorizeUrl, TokenUrl, Enabled, CreatedAtUtc, UpdatedAtUtc, Notes
) VALUES (
  '11111111-1111-1111-1111-111111111111',
  'github',
  'GitHub',
  'YOUR_CLIENT_ID',
  'YOUR_CLIENT_SECRET',
  'https://wizionic.com/api/oauth/github/callback',
  NULL, NULL,
  1,
  datetime('now'),
  datetime('now'),
  'manual seed'
);

INSERT INTO Connectors (
  Id, ConnectorId, DisplayName, Description, Kind, OAuthProviderId,
  ScopesJson, DocsUrl, Featured, SortOrder, Enabled,
  IconText, IconBackground, IconImageUrl,
  CreatedAtUtc, UpdatedAtUtc
) VALUES (
  '22222222-2222-2222-2222-222222222222',
  'github',
  'GitHub',
  'Repositories, issues, and pull requests.',
  0,
  'github',
  '["repo","read:user","user:email"]',
  'https://docs.github.com/en/rest',
  1, 10, 1,
  'GH',
  '#24292f',
  NULL,
  datetime('now'),
  datetime('now')
);
```

## Icons

- `IconText` — letters/emoji in the square (`GH`, `M`, `31`)
- `IconBackground` — CSS color or gradient for **inline** style (no CSS deploy needed)
- `IconImageUrl` — optional `https://...` or `data:image/...;base64,...` for a real logo

## Verify

```sql
SELECT ProviderId, ClientId, length(ClientSecretProtected), RedirectUri, Enabled FROM OAuthProviders;
SELECT ConnectorId, DisplayName, Featured, IconText, IconBackground FROM Connectors;
```

```bash
curl -s https://wizionic.com/api/connectors/catalog | head
curl -s https://wizionic.com/api/oauth/status
```

Restart the container after first migrate if the process was already running without the new migration applied (usually Migrate runs on start).
