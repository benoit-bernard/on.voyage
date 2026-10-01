# syntax=docker/dockerfile:1.7
# PWA (Blazor WebAssembly): published once, served as static files by Caddy (no .NET at run time). Build context: the repository root.
# The PWA reads wwwroot/appsettings.json: Gateway:BaseAddress is empty (same origin; the reverse proxy routes /api and /media to the
# gateway). Map:TilesUrl is written at container start from PWA_MAP_TILES_URL, so the image is identical in every environment.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Web/OnVoyage.Web.Pwa/OnVoyage.Web.Pwa.csproj -c Release -o /out --nologo

FROM caddy:2-alpine AS final
ENV XDG_DATA_HOME=/tmp/caddy-data XDG_CONFIG_HOME=/tmp/caddy-config PWA_MAP_TILES_URL=
COPY deploy/docker/pwa.Caddyfile /etc/caddy/Caddyfile
COPY deploy/docker/pwa-entrypoint.sh /usr/local/bin/pwa-entrypoint.sh
COPY --from=build /out/wwwroot /srv
RUN chmod 0555 /usr/local/bin/pwa-entrypoint.sh \
    && chown -R 10001:10001 /srv

USER 10001:10001
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=5 \
    CMD wget -q --spider http://127.0.0.1:8080/ || exit 1
ENTRYPOINT ["/usr/local/bin/pwa-entrypoint.sh"]
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
