# Development image for the PulseReserve API: SDK + dotnet watch. Source is bind-mounted by compose.dev.yaml,
# so this image only changes when the SDK version does. Never used for production.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# Build output lives outside the bind mount so Windows/macOS host bin/obj folders never collide with Linux builds.
ENV UseArtifactsOutput=true \
    ArtifactsPath=/artifacts \
    DefaultItemExcludes="**/bin/**;**/obj/**" \
    NUGET_PACKAGES=/nuget \
    DOTNET_USE_POLLING_FILE_WATCHER=1 \
    DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS=http://+:8080

WORKDIR /workspace
EXPOSE 8080
CMD ["dotnet", "watch", "run", "--project", "src/PulseReserve.AppHost/PulseReserve.AppHost.csproj", "--no-launch-profile", "--non-interactive"]
