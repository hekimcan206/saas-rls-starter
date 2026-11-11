# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change. The NuGet cache mount is
# locked because compose builds this image and the demo image at the same time.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SaasRls.Tenancy/SaasRls.Tenancy.csproj src/SaasRls.Tenancy/
COPY src/SaasRls.Tenancy.EntityFrameworkCore/SaasRls.Tenancy.EntityFrameworkCore.csproj src/SaasRls.Tenancy.EntityFrameworkCore/
COPY src/SaasRls.Api/SaasRls.Api.csproj src/SaasRls.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore src/SaasRls.Api/SaasRls.Api.csproj

COPY .editorconfig ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish src/SaasRls.Api/SaasRls.Api.csproj --configuration Release --output /app/publish -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, and a non-root "app" user (UID 1654) baked in.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="saas-rls-starter-api" \
      org.opencontainers.image.source="https://github.com/hekimcan206/saas-rls-starter" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SaasRls.Api.dll"]
