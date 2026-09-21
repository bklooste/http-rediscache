# Changelog

Every push to `main` publishes a new patch version automatically (see `version.json` — height-based,
not manually tagged), so not every version number gets its own entry here. This file tracks what
actually changed.

## Unreleased

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
