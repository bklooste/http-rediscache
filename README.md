# http-rediscache

[![CI](https://github.com/bklooste/http-rediscache/actions/workflows/ci.yml/badge.svg)](https://github.com/bklooste/http-rediscache/actions/workflows/ci.yml)
[![Image](https://img.shields.io/badge/ghcr.io-http--rediscache-blue)](https://github.com/bklooste/http-rediscache/pkgs/container/http-rediscache%2Fcache)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

An **HTTP response cache backed by Redis**, with stale-while-revalidate. Put it in front of a slow or busy
upstream service: callers ask *it* for the upstream URL, and it answers from Redis when it can.

```
your app ──GET /cache?url=http://api/products&cacheVar=brand──▶ http-rediscache ──▶ Redis
                                                                       │ (only on a miss / refresh)
                                                                       ▼
                                                                  http://api/products
```

- **One endpoint, no client library** — any HTTP client can use it; the upstream URL is a query parameter.
- **You choose what makes two requests "the same"** — per call, by naming the query params, body fields and
  headers that matter (`cacheVar`, `cacheHeader`). Nothing else fragments the cache.
- **Stale-while-revalidate** — an expired entry is still served instantly while a background call refreshes it.
- **Honours the upstream** — `Cache-Control: no-store | no-cache | private` are never cached; `max-age` /
  `s-maxage` set the TTL unless the caller overrides it.
- Runs anywhere: a plain container, only needs a Redis it can reach. No Kubernetes node requirements.

It is deliberately small — one endpoint fronting whatever upstream you name. It is not a CDN, an edge proxy or a
full [RFC 9111](https://www.rfc-editor.org/rfc/rfc9111) cache.

## Quick start

```bash
git clone https://github.com/bklooste/http-rediscache && cd http-rediscache
docker compose up -d
```

That starts the cache, Redis, and a tiny `upstream` that answers with the time it generated the page. Ask the
cache for it (`ttl=5` = fresh for 5 seconds):

```bash
URL='localhost:8080/cache?url=http://upstream:8080/hello&ttl=5'

curl -si "$URL" | grep -iE '^(x-cache|age)|generated'   # X-Cache: MISS   (went to the upstream)
curl -si "$URL" | grep -iE '^(x-cache|age)|generated'   # X-Cache: HIT    (same "generated at", from Redis)
sleep 6
curl -si "$URL" | grep -iE '^(x-cache|age)|generated'   # X-Cache: STALE  (old page served instantly, refresh started)
curl -si "$URL" | grep -iE '^(x-cache|age)|generated'   # X-Cache: HIT    (new "generated at")
```

Just the image, if you already have a Redis:

```bash
docker run --rm -p 8080:8080 -e ConnectionStrings__Redis=host.docker.internal:6379 \
  ghcr.io/bklooste/http-rediscache/cache:latest
```

Images are public on GHCR — no pull secret needed. Multi-arch (`linux/amd64`, `linux/arm64`).

## Configuration

The configuration surface **is** the public API of a container. Settings come from environment variables
(ASP.NET Core `Section__Key` form), an optional mounted `appsettings.json`, or both — env wins. Invalid
configuration fails at startup with a clear message in the container log.

Every setting has a default applied in code. **The only thing you must supply is the Redis connection string.**

| Env var | Type | Default | Description |
|---|---|---|---|
| `ConnectionStrings__Redis` | string | _(required)_ | [StackExchange.Redis connection string](https://stackexchange.github.io/StackExchange.Redis/Configuration), e.g. `redis:6379,password=…`. Add `abortConnect=false` to start even while Redis is down (`/health` then reports unhealthy until it is back). |
| `Cache__DefaultTtlSeconds` | int ≥ 1 | `30` | How long a response is a `HIT` when neither the route (`ttl`) nor the upstream (`max-age`) says otherwise. |
| `Cache__StaleWhileRevalidateSeconds` | int ≥ 0 | `60` | How long after expiry an entry may still be served as `STALE` while it is refreshed in the background. `0` turns stale-while-revalidate off. |
| `Cache__UpstreamTimeoutSeconds` | int 1–3600 | `10` | Timeout for one upstream call. Exceeding it is answered with `502`. |
| `Cache__KeyPrefix` | string | `plat-cache:` | Prefix of every Redis key the cache writes (`[A-Za-z0-9:_-]+`). Use a different one per environment when several share a Redis. Changing it just starts cold — old entries expire on their own. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | url | _(unset)_ | Standard OpenTelemetry. Unset = nothing exported. Also honours `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_PROTOCOL`, etc. Traces cover inbound requests, upstream calls and Redis commands. |
| `ASPNETCORE_HTTP_PORTS` | string | `8080` | Listening port. |

**Secrets.** Any setting can be supplied from a file by adding `_FILE` to its env var, e.g.
`ConnectionStrings__Redis_FILE=/run/secrets/redis` — use this with Docker/Kubernetes secret mounts so the
password does not appear in `docker inspect`. The service never logs its configuration.

**`DOTNET_` prefix.** Every setting is also read with a `DOTNET_` prefix (`DOTNET_ConnectionStrings__Redis`),
and that form wins over the plain one. It exists for deployments that were already configured that way.

## API reference

### `GET | POST | PUT /cache`

Fetches the upstream URL through the cache.

| Query param | Repeatable | Meaning |
|---|---|---|
| `url` | no | **Required.** The upstream URL, absolute (`http://api/x`) or scheme-less (`api:8080/x` — `http://` is assumed). Missing → `400`. |
| `cacheVar` | yes | Name of a query parameter (or, for POST/PUT, a top-level JSON body field) whose **value** is part of the cache key. |
| `cacheHeader` | yes | Name of a request header whose **value** is part of the cache key. |
| `ttl` | no | Seconds this response stays fresh. Overrides upstream `max-age`. Ignored unless > 0. |

Every *other* query parameter is forwarded to the upstream, as are all request headers (except `Host`,
`Connection`-style hop-by-hop headers, `Content-Length`, `Content-Type` — rebuilt from the body — and
`Accept-Encoding`). The request body is forwarded for POST/PUT.

Response: the upstream's status, body and `Content-Type`, plus

| Header | Value |
|---|---|
| `X-Cache` | `HIT` (fresh, from Redis), `STALE` (expired but inside the stale window, from Redis), `MISS` (came from the upstream) |
| `Age` | Seconds since the entry was stored (`0` on a `MISS`). |

An unreachable or too-slow upstream is answered with `502 upstream unavailable`. Non-2xx upstream responses are
passed through and **never cached**.

#### What makes two requests "the same" — read this before relying on it

The cache key is the **method + the upstream URL without its query string + the `cacheVar` values + the
`cacheHeader` values**, hashed. So:

- A query parameter that changes the response **must** be listed as a `cacheVar`. `?page=2` is forwarded to the
  upstream either way, but unless `cacheVar=page` is given, page 2 shares page 1's entry.
- The same holds for a query string written inside `url=` itself.
- For POST/PUT, list every JSON body field that changes the response as a `cacheVar`. Fields you don't list
  are forwarded but ignored by the key.
- Headers work the same way (`cacheHeader=X-User` to cache per user); an unlisted header never splits the cache.

```bash
# cached per brand and per user; page is forwarded but does not split the cache
curl 'localhost:8080/cache?url=http://api/products&cacheVar=brand&cacheHeader=X-User&brand=acme&page=1' -H 'X-User: 42'
```

#### TTL — which number wins

1. Upstream `Cache-Control: no-store`, `no-cache` or `private` → **not cached**, always. Neither `ttl` nor the
   default overrides an upstream that opts out.
2. Otherwise the route's `ttl` param.
3. Otherwise the upstream's `s-maxage`, then `max-age` (`max-age=0` → not cached).
4. Otherwise `Cache__DefaultTtlSeconds`.

A caller sending `Cache-Control: no-cache` or `no-store` bypasses the read and forces a refresh (the fresh
response is stored again).

#### Stale-while-revalidate

Between `ttl` and `ttl + Cache__StaleWhileRevalidateSeconds` an entry is served as `STALE`. **For GET** each
stale hit also starts a background refresh. **For POST/PUT it does not** — a stale entry is served as is, and
refreshes only once it has fully expired and a request goes to the upstream (POST/PUT are not assumed safe to
repeat unprompted).

#### POST / PUT caching rules

A POST/PUT response is cacheable **only if the request body is a JSON object** (`Content-Type` containing `json`).
Any other body — plain text, form data, a JSON array, invalid JSON, or no body — is forwarded but **never cached**.
Only top-level string / number / boolean fields are readable as `cacheVar`s; nested objects are not.

#### Redis layout

Per entry, two keys: `{Cache__KeyPrefix}{SHA-256 hex}:b` (body) and `…:m` (content type, stored-at, ttl). They
expire `ttl + 5 minutes` after being written, so the stale window is still served after logical expiry.

### `GET /health`

`200` when the service can reach Redis; `503` otherwise. Used by the container `HEALTHCHECK` and the k8s probes.

## Deployment

Runs on **any Kubernetes node**; it only needs network access to its Redis. Redis is an external endpoint, never
something the service ships or pins a node for.

```bash
kubectl apply -f deploy/k8s/deployment.yaml
```

The manifest deliberately has no `nodeSelector`, `affinity` or tolerations. Pin an explicit image version
(`:0.1`), not `:latest`, in production. Two or more replicas are fine — all state is in Redis.

## Development

```bash
dotnet build HttpRedisCache.slnx
dotnet test HttpRedisCache.slnx      # needs Docker: the tests start a real Redis with Testcontainers
docker build -t http-rediscache .
```

Versions come from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
(`version.json`); every push to `main` that passes tests publishes a new patch version. See
[CHANGELOG.md](CHANGELOG.md) and [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[MIT](LICENSE). Third-party licences of the shipped image are listed in [NOTICE](NOTICE).
