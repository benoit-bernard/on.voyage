# syntax=docker/dockerfile:1.7
# Image of any ASP.NET Core / worker host of the solution (platform-api, catalog-api, discovery-api, factory-api, factory-worker,
# gateway, web-admin and the future insights-api, creators-api, creators-worker, web-public). One parameterised file, one image per
# host. Build context: the repository root.
#
#   docker build -f deploy/docker/service.Dockerfile \
#     --build-arg PROJECT=src/Services/Platform/OnVoyage.Platform.Api/OnVoyage.Platform.Api.csproj \
#     --build-arg ASSEMBLY=OnVoyage.Platform.Api -t onvoyage/platform-api .
#
# factory-worker additionally needs osm2pgsql and ffmpeg (Factory:Osm:Osm2pgsqlPath and Factory:Audio:FfmpegPath default to a PATH lookup):
#   --build-arg RUNTIME_PACKAGES="curl osm2pgsql ffmpeg"

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "${PROJECT}" -c Release -o /app --nologo -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
ARG ASSEMBLY
ARG RUNTIME_PACKAGES="curl"
ENV ASSEMBLY=${ASSEMBLY} \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

# curl is only there for the container health check. Writable directories: work files of the Factory worker and the media volume shared
# with Catalog; they belong to the non-root user so that a named volume mounted there inherits the ownership.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ${RUNTIME_PACKAGES} \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /media /data/factory \
    && chown -R "${APP_UID}:${APP_UID}" /media /data

WORKDIR /app
COPY --from=build /app .

# .NET 8+ images ship a non-root "app" user, exposed as APP_UID.
USER ${APP_UID}
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=60s --retries=5 \
    CMD curl -fsS "http://127.0.0.1:${ASPNETCORE_HTTP_PORTS}/alive" || exit 1
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet /app/${ASSEMBLY}.dll"]
