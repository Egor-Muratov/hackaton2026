# Forecast API: multi-stage сборка (SDK 10 -> ASP.NET 10 runtime), порт 8080.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Forecast.Api.csproj nuget.config global.json ./
# В контейнере офлайн-каталога нет: restore только с nuget.org (явный --source перекрывает nuget.config).
RUN dotnet restore Forecast.Api.csproj --source https://api.nuget.org/v3/index.json
COPY . .
RUN dotnet publish Forecast.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Forecast.Api.dll"]
