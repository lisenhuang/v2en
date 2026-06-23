# 🌐 v2en

Mirrors the [V2EX](https://www.v2ex.com) front-page feed, translated to English by [OpenRouter](https://openrouter.ai) free AI models.

---

## 🧭 Architecture

```
every 5 min (Cache-Control: max-age=300)
              │
   ┌──────────▼──────────┐
   │     FeedWorker      │   BackgroundService
   └──────────┬──────────┘
              │
   ┌──────────▼──────────┐  conditional GET (ETag)
   │  V2exFeedClient     │──────────────────────────► V2EX Atom feed
   └──────────┬──────────┘
              │ upsert by V2exId
   ┌──────────▼──────────────────────────────────────┐
   │              SQLite  (EF Core, WAL)              │
   │   Post · FeedState                               │
   └────────────┬──────────────────────────┬─────────┘
                │                          │
   ┌────────────▼────────────┐  ┌──────────▼──────────────┐
   │   Razor Pages           │  │   GET /index.xml         │
   │   / (list)  /t/{id}     │  │   Atom 1.0 (English)     │
   └─────────────────────────┘  └──────────────────────────┘
                    │
                    └── Cloudflare Tunnel → your domain (HTTPS)
                        app listens on HTTP :8080 internally
```

---

## 🚀 Quick Start (local dev)

| Step | Command |
|---|---|
| 1. Set API key | `dotnet user-secrets set "OpenRouter:ApiKey" "sk-or-..."` |
| 2. Apply migration | `dotnet ef database update` |
| 3. Run | `dotnet run` |

App starts at **http://localhost:8080**. The first feed fetch and translations happen on startup.

---

## 🔑 Configuration

| Key | Env var (Docker) | Default |
|---|---|---|
| `OpenRouter:ApiKey` | `OpenRouter__ApiKey` | _(required)_ |
| `Site:BaseUrl` | `Site__BaseUrl` | _(empty — uses request host)_ |
| `Feed:PollIntervalSeconds` | `Feed__PollIntervalSeconds` | `300` |
| `Translation:DailyQuota` | `Translation__DailyQuota` | `200` |
| `Translation:MaxPerTick` | `Translation__MaxPerTick` | `8` |
| `Translation:MinDelaySecondsBetweenCalls` | `Translation__MinDelaySecondsBetweenCalls` | `4` |

---

## 🌍 Endpoints

| Path | Description |
|---|---|
| `/` | Translated posts, newest first |
| `/t/{id}` | Post detail (same URL shape as V2EX) |
| `/index.xml` | English Atom 1.0 feed |
| `/healthz` | Health check |

---

## 🐳 Deploy (Docker)

```bash
# 1. Build image
docker compose build

# 2. Set required env vars (or add to .env file)
export OPENROUTER_API_KEY=sk-or-...
export SITE_BASE_URL=https://your-domain.example

# 3. Start
docker compose up -d
```

SQLite data is stored in a named Docker volume (`v2en-data`) mounted at `/data`.
Cloudflare Tunnel routes your domain → `http://<host>:8080`. The app never handles TLS.

---

## ⏱️ How translation works

1. **Fetch** — conditional GET every 5 min. V2EX's `Cache-Control: max-age=300` means polling faster returns the same cached bytes; 304 = skip.
2. **Upsert** — deduplicated by the numeric post ID from the Atom `<id>` tag.
3. **Translate** — one OpenRouter API call per post. Newest posts first. Falls back through a chain of `:free` models on errors. Long posts that get truncated (`finish_reason=length`) are retried with the next model.
4. **Pace** — ≤ 8 posts/tick, 4 s minimum between calls, daily cap of 200.

### ⚠️ Free-tier limits

| Limit | Value |
|---|---|
| Rate | 20 req/min (we stay at ≤ 15) |
| Daily (pure free) | ~50 req/day |
| Daily (after $10 credit) | ~1000 req/day (still `$0/call` on `:free` models) |

V2EX publishes ~50 new posts/day. The default `DailyQuota` of **200** comfortably keeps up **if your account has the ~1000/day free-model limit** (i.e. you've added the one-time $10 credit — still `$0/call` on `:free` models). On a **pure-free account** the real cap is ~50/day, so lower `DailyQuota` to ~48 to avoid hammering a limit you can't exceed. Either way the site stays freshest-first and shows pending counts on the homepage.

---

## ⚠️ Disclaimer

This is an unofficial translated mirror. Content belongs to [V2EX](https://www.v2ex.com) and respective authors. Translations are machine-generated and may be inaccurate. Not affiliated with V2EX.
