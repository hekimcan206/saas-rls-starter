# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SaasRls.Tenancy/SaasRls.Tenancy.csproj src/SaasRls.Tenancy/
COPY src/SaasRls.Tenancy.EntityFrameworkCore/SaasRls.Tenancy.EntityFrameworkCore.csproj src/SaasRls.Tenancy.EntityFrameworkCore/
COPY src/SaasRls.Api/SaasRls.Api.csproj src/SaasRls.Api/
COPY samples/SaasRls.Demo/SaasRls.Demo.csproj samples/SaasRls.Demo/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore samples/SaasRls.Demo/SaasRls.Demo.csproj

COPY .editorconfig ./
COPY src/ src/
COPY samples/SaasRls.Demo/ samples/SaasRls.Demo/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish samples/SaasRls.Demo/SaasRls.Demo.csproj --configuration Release --output /app/demo -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="saas-rls-starter-demo" \
      org.opencontainers.image.source="https://github.com/hekimcan206/saas-rls-starter" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/demo .
USER $APP_UID
ENTRYPOINT ["dotnet", "SaasRls.Demo.dll"]
