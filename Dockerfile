# syntax=docker/dockerfile:1

# Build stage — WASM host only (ChatfishApp.csproj). MAUI is excluded via .dockerignore.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for better layer caching on restore
COPY *.sln .
COPY global.json* ./
COPY ChatfishApp.csproj .
COPY ChatfishApp.Client/ChatfishApp.Client.csproj ./ChatfishApp.Client/
COPY ChatfishApp.Core/ChatfishApp.Core.csproj ./ChatfishApp.Core/
COPY ChatfishApp.Shared/ChatfishApp.Shared.csproj ./ChatfishApp.Shared/

# Restore only the host project graph (Client + Core + Shared — not MAUI)
RUN dotnet restore "ChatfishApp.csproj"

# Copy remaining source (MAUI omitted by .dockerignore)
COPY . .

# Publish the ASP.NET host + Blazor WASM client assets
RUN dotnet publish "ChatfishApp.csproj" -c Release -o /app/publish /p:BuildProjectReferences=true

# Runtime stage (smaller image)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ChatfishApp.dll"]