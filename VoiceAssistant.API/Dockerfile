FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg curl && rm -rf /var/lib/apt/lists/*
# mcr.microsoft.com/dotnet/aspnet already ships a non-root "app" user (uid 1654) — reuse it
RUN mkdir -p /app/audio && chown -R app:app /app/audio

COPY --from=build /app .
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5000

# Stays root here on purpose — the entrypoint fixes /app/audio's volume
# ownership, then drops to the non-root "app" user (setpriv, no subshell,
# so SIGTERM on container stop still reaches dotnet directly) before
# actually running the app. See docker-entrypoint.sh.
ENTRYPOINT ["/docker-entrypoint.sh"]
