# syntax=docker/dockerfile:1

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + top-level project files first (better layer caching for restores)
COPY *.sln .
COPY global.json* .
COPY ChatfishApp.csproj .
# The referenced client project must be present for restore to succeed
COPY ChatfishApp.Client/ChatfishApp.Client.csproj ./ChatfishApp.Client/

# Restore using the main project (will follow the ProjectReference to the Client)
RUN dotnet restore "ChatfishApp.csproj"

# Copy everything else (source code, wwwroot, migrations, etc.)
COPY . .

# Publish *only* the main host project (not the whole solution).
# This produces a runnable app in /app/publish containing ChatfishApp.dll + the Client WASM assets.
RUN dotnet publish "ChatfishApp.csproj" -c Release -o /app/publish

# Runtime stage (smaller image)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Copy the published output
COPY --from=build /app/publish .

# DataProtection keys are stored inside the SQLite database (chatfish.db) via EF Core.
# No extra volume or keys directory is required for auth session durability.
# As long as your hosting provider persists the chatfish.db file (or the working directory containing it),
# logins will survive restarts/sleeps/deploys.

# Railway will use this (or you can override the start command in the UI)
ENTRYPOINT ["dotnet", "ChatfishApp.dll"]
