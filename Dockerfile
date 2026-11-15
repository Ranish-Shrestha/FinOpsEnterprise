# syntax=docker/dockerfile:1
# Builds both deployable images from one context (the repository root):
#   docker build --target api    -t finops-api .
#   docker build --target worker -t finops-worker .
# Base images are pinned to exact versions; bump them deliberately with the SDK in global.json.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
WORKDIR /src
# Restore first (cached unless a project or lock file changes). --locked-mode fails on lock-file drift, as in CI.
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/FinOps.Domain/FinOps.Domain.csproj src/FinOps.Domain/packages.lock.json src/FinOps.Domain/
COPY src/FinOps.Application/FinOps.Application.csproj src/FinOps.Application/packages.lock.json src/FinOps.Application/
COPY src/FinOps.Infrastructure/FinOps.Infrastructure.csproj src/FinOps.Infrastructure/packages.lock.json src/FinOps.Infrastructure/
COPY src/FinOps.Api/FinOps.Api.csproj src/FinOps.Api/packages.lock.json src/FinOps.Api/
COPY src/FinOps.Worker/FinOps.Worker.csproj src/FinOps.Worker/packages.lock.json src/FinOps.Worker/
RUN dotnet restore src/FinOps.Api/FinOps.Api.csproj --locked-mode \
 && dotnet restore src/FinOps.Worker/FinOps.Worker.csproj --locked-mode
COPY src/FinOps.Domain/ src/FinOps.Domain/
COPY src/FinOps.Application/ src/FinOps.Application/
COPY src/FinOps.Infrastructure/ src/FinOps.Infrastructure/
COPY src/FinOps.Api/ src/FinOps.Api/
COPY src/FinOps.Worker/ src/FinOps.Worker/
# The images run on linux-x64 (glibc). Native assets for other platforms (about 105 MB, mostly QuestPDF's renderer)
# are removed; runtimes/unix (SqlClient) and runtimes/linux-x64 stay.
RUN dotnet publish src/FinOps.Api/FinOps.Api.csproj -c Release --no-restore -o /out/api -p:UseAppHost=false \
 && dotnet publish src/FinOps.Worker/FinOps.Worker.csproj -c Release --no-restore -o /out/worker -p:UseAppHost=false  && for app in /out/api /out/worker; do       find "$app/runtimes" -mindepth 1 -maxdepth 1 -type d ! -name unix ! -name linux-x64 -exec rm -rf {} +;     done

# Runtime: Ubuntu chiseled "extra" (ICU and time zones for SqlClient; no shell, no package manager), non-root (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "FinOps.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra AS worker
WORKDIR /app
COPY --from=build /out/worker .
ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "FinOps.Worker.dll"]
