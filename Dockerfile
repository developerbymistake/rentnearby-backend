# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# The build host's IPv6 route to nuget.org stalls (4.5 MB of 17 MB in 20 s while IPv4 takes 0.2 s), which made
# dotnet restore hit "no data received for 60000ms". Build stage only; the runtime image is unchanged.
# TEMPORARY (owner order 2026-11-26): REMOVE this line (and this comment) in the NEXT build once the NuGet IPv6
# route is healthy again (test: the curl -6 download of a nupkg from api.nuget.org finishes in under a second).
ENV DOTNET_SYSTEM_NET_DISABLEIPV6=1

# Copy project files first
COPY RentNearBy.Api/*.csproj RentNearBy.Api/
COPY RentNearBy.Core/*.csproj RentNearBy.Core/
COPY RentNearBy.Infrastructure/*.csproj RentNearBy.Infrastructure/

# Restore dependencies (don't skip this)
RUN dotnet restore RentNearBy.Api/RentNearBy.Api.csproj

# Copy entire source code
COPY . .

# Publish (one compile only; build and publish used to compile everything twice)
WORKDIR /src/RentNearBy.Api
RUN dotnet publish RentNearBy.Api.csproj -c Release --no-restore -o /app/publish

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5000
ENTRYPOINT ["dotnet", "RentNearBy.Api.dll"]
