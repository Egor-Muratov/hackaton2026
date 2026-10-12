# Базовые образы: точные теги с digest из infra/images.lock (make check-images / make pin-images).
ARG NODE_IMAGE=node:24.21.0-alpine3.24@sha256:ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1
ARG NGINX_IMAGE=nginx:1.30.5-alpine3.24@sha256:0985e772fb9f729e6fa0980da05fca5d9c468e870eed43071545afa9d2e27d94

# Офлайн-запас: стадия-заглушка с пустыми wheels/ nuget/ npm/. compose подменяет её каталогом запаса
# (additional_contexts: offline). kaniko контекстов не знает — берёт заглушку и собирает онлайн.
FROM ${NODE_IMAGE} AS offline
RUN mkdir -p /wheels /nuget /npm

# Сборка статики
FROM ${NODE_IMAGE} AS build
ARG OFFLINE=0
ARG MIRROR_NPM_REGISTRY=
WORKDIR /app
COPY package.json package-lock.json ./
COPY --from=offline /npm /offline/npm
RUN if [ "$OFFLINE" = 1 ]; then npm ci --offline --cache /offline/npm --no-audit --no-fund; \
    else npm ci --no-audit --no-fund ${MIRROR_NPM_REGISTRY:+--registry "$MIRROR_NPM_REGISTRY"}; fi
COPY . .
ARG VITE_API_MODE=live
ENV VITE_API_MODE=$VITE_API_MODE
RUN npm run build

# Раздача статики; /api проксирует внешний nginx из compose
FROM ${NGINX_IMAGE}
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
