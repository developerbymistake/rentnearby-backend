# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files first
COPY RentNearBy.Api/*.csproj RentNearBy.Api/
COPY RentNearBy.Core/*.csproj RentNearBy.Core/
COPY RentNearBy.Infrastructure/*.csproj RentNearBy.Infrastructure/

# Restore dependencies (don't skip this)
RUN dotnet restore RentNearBy.Api/RentNearBy.Api.csproj

# Copy entire source code
COPY . .

# Build
WORKDIR /src/RentNearBy.Api
RUN dotnet build RentNearBy.Api.csproj -c Release --no-restore -o /app/build

# Publish
FROM build AS publish
RUN dotnet publish RentNearBy.Api.csproj -c Release -o /app/publish

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

EXPOSE 5000
ENTRYPOINT ["dotnet", "RentNearBy.Api.dll"]
