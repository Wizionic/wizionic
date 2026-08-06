# syntax=docker/dockerfile:1

# Build stage ΓÇö WASM host only (App.csproj). MAUI is excluded via .dockerignore.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for better layer caching on restore
COPY *.sln .
COPY global.json* ./
COPY App.csproj .
COPY App.Client/App.Client.csproj ./App.Client/
COPY App.Core/App.Core.csproj ./App.Core/
COPY App.Shared/App.Shared.csproj ./App.Shared/

# Restore only the host project graph (Client + Core + Shared ΓÇö not MAUI)
RUN dotnet restore "App.csproj"

# Copy remaining source (MAUI omitted by .dockerignore)
COPY . .

# Publish host + WASM client. Trim is per-project (Client=true, host=false) ΓÇö do not force global.
RUN dotnet publish "App.csproj" -c Release -o /app/publish \
    /p:BuildProjectReferences=true

# Runtime stage (smaller image)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "App.dll"]
