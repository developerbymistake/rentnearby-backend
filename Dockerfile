FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY RentNearBy.Core/RentNearBy.Core.csproj RentNearBy.Core/
COPY RentNearBy.Infrastructure/RentNearBy.Infrastructure.csproj RentNearBy.Infrastructure/
COPY RentNearBy.Api/RentNearBy.Api.csproj RentNearBy.Api/
RUN dotnet restore RentNearBy.Api/RentNearBy.Api.csproj

COPY . .
RUN dotnet publish RentNearBy.Api/RentNearBy.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN mkdir -p /app/uploads

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5000

ENTRYPOINT ["dotnet", "RentNearBy.Api.dll"]
