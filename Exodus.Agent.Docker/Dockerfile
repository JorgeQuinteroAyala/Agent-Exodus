FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ["Exodus.Agent.Core/Exodus.Agent.Core.csproj", "Exodus.Agent.Core/"]
COPY ["Exodus.Agent.Docker/Exodus.Agent.Docker.csproj", "Exodus.Agent.Docker/"]

RUN dotnet restore "Exodus.Agent.Docker/Exodus.Agent.Docker.csproj" \
    --runtime linux-musl-x64

COPY Exodus.Agent.Core/ Exodus.Agent.Core/
COPY Exodus.Agent.Docker/ Exodus.Agent.Docker/

RUN dotnet publish "Exodus.Agent.Docker/Exodus.Agent.Docker.csproj" \
    -c Release \
    --runtime linux-musl-x64 \
    --no-restore \
    --no-self-contained \
    -o /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache curl \
    && addgroup -S agent \
    && adduser -S agent -G agent

COPY --from=build --chown=agent:agent /app/publish .

EXPOSE 8080

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -s http://localhost:8080/health > /dev/null || exit 1

ENTRYPOINT ["dotnet", "Exodus.Agent.Docker.dll"]