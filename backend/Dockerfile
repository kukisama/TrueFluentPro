# backend/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore (leverage Docker cache)
COPY TrueFluentPro.Api/*.csproj ./TrueFluentPro.Api/
RUN dotnet restore TrueFluentPro.Api/TrueFluentPro.Api.csproj

# Copy source and publish
COPY TrueFluentPro.Api/ ./TrueFluentPro.Api/
RUN dotnet publish TrueFluentPro.Api/TrueFluentPro.Api.csproj -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Install curl (health check)
RUN apk add --no-cache curl

# Create data directories
RUN mkdir -p /app/data /app/logs

COPY --from=build /app/publish .

# Non-root user
RUN adduser -D -u 1000 appuser && chown -R appuser:appuser /app
USER appuser

EXPOSE 8080 8443

ENTRYPOINT ["dotnet", "TrueFluentPro.Api.dll"]
