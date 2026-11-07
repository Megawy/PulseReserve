# Production image for the PulseReserve API (ASP.NET Core 10, one process: API + background workers + SignalR hub).
# STATUS: written but NOT BUILT or run (Docker was not available to the author). Build it in CI before relying on it:
#   docker build -f docker/api.Dockerfile -t pulsereserve-api .
# Configuration comes from environment variables only (see docs/architecture/29-deployment-and-release-readiness.md); nothing secret is baked in.
# Pin the base images by digest in your registry policy; 10.0.13 or later is required for the SignalR fix GHSA-334f-vvp4-7g6p.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

# Only what the AppHost needs is copied (the tests and docs are excluded by .dockerignore). Layer caching: copy the sources after
# a restore if build time matters; kept simple here because CI builds this image rarely.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/PulseReserve.AppHost/PulseReserve.AppHost.csproj

RUN dotnet publish src/PulseReserve.AppHost/PulseReserve.AppHost.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Not Development: the simulator, the dev notification adapter and migrate-on-startup refuse to run outside Development/Testing.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0

# The runtime image ships a non-root user; the API never needs root.
USER $APP_UID
EXPOSE 8080

# No HEALTHCHECK here (the image has no curl/wget): point the orchestrator's probes at /health/live and /health/ready.
ENTRYPOINT ["dotnet", "PulseReserve.AppHost.dll"]
