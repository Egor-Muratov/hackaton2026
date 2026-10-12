# Forecast API: multi-stage сборка (SDK 10 -> ASP.NET 10 runtime), порт 8080.
# Базовые образы: точные теги с digest из infra/images.lock (make check-images / make pin-images).
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4

# Офлайн-запас: стадия-заглушка с пустыми wheels/ nuget/ npm/. compose подменяет её каталогом запаса
# (additional_contexts: offline). kaniko контекстов не знает — берёт заглушку и собирает онлайн.
FROM ${DOTNET_SDK_IMAGE} AS offline
RUN mkdir -p /wheels /nuget /npm

FROM ${DOTNET_SDK_IMAGE} AS build
ARG OFFLINE=0
ARG MIRROR_NUGET_SOURCE=
WORKDIR /src
COPY Forecast.Api.csproj nuget.config global.json ./
COPY --from=offline /nuget /offline/nuget
# Явный --source перекрывает nuget.config (там путь к запасу на машине разработчика).
RUN if [ "$OFFLINE" = 1 ]; then dotnet restore Forecast.Api.csproj --source /offline/nuget; \
    else dotnet restore Forecast.Api.csproj --source "${MIRROR_NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"; fi
COPY . .
RUN dotnet publish Forecast.Api.csproj -c Release -o /app --no-restore

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Forecast.Api.dll"]
