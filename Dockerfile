# syntax=docker/dockerfile:1
# Self-contained: builds from this repo's own tree only. Nothing outside the build context is referenced.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /src
COPY . .
# Regular (framework-dependent) publish. Switch to PublishAot=true + the runtime-deps:10.0-noble-chiseled-aot
# image only for services that already support AOT — a technical choice, never a licensing one.
RUN dotnet publish src/HttpRedisCache/HttpRedisCache.csproj -c Release -a $TARGETARCH \
      -p:Version=$VERSION -p:UseAppHost=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
ARG VERSION=0.0.0
ARG REVISION=unknown
LABEL org.opencontainers.image.title="http-rediscache" \
      org.opencontainers.image.source="https://github.com/bklooste/http-rediscache" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
WORKDIR /app
COPY --from=build /app .
# The chiselled base defaults to the non-root `app` user; stated explicitly so it can't regress.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# No shell/curl in a chiselled image, so the app probes itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "HttpRedisCache.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "HttpRedisCache.dll"]
