# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

## Unreleased

- New: `GLOBAL_`-prefixed env vars (e.g. `GLOBAL_ConnectionStrings__Redis` from a shared env file) are read as
  environment-wide defaults, below `appsettings.json`; `DOTNET_` vars still override everything.
- Fixed: a startup failure (e.g. missing `ConnectionStrings__Redis`) now prints one clear line and exits 1;
  before, the unhandled exception crashed the container with SIGSEGV (exit 139).
- Initial public release: extracted from the `plat-cache` service of a private platform, with its behaviour
  unchanged — same `/cache` contract (`url`, `cacheVar`, `cacheHeader`, `ttl`), same `X-Cache` / `Age`
  headers, same Redis key layout.
- New: `Cache__KeyPrefix` (default `plat-cache:`, so existing keys are unaffected).
- New: every setting is validated at startup and has a code-level default; the only required setting is
  `ConnectionStrings__Redis`. Secrets can be supplied as `*_FILE`.
- Fixed: a compressing upstream no longer poisons the cache. The cache used to forward the caller's
  `Accept-Encoding`, then store and replay the compressed bytes without their `Content-Encoding` header, so
  clients received undecodable content. It now always fetches and caches identity content.
- Fixed: `/health` reuses the service's Redis connection instead of opening a second one.
