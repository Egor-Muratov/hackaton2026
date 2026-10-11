# Сборка статики
FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN if [ -f package-lock.json ]; then npm ci --no-audit --no-fund; else npm install --no-audit --no-fund; fi
COPY . .
ARG VITE_API_MODE=live
ENV VITE_API_MODE=$VITE_API_MODE
RUN npm run build

# Раздача статики; /api проксирует внешний nginx из compose
FROM nginx:alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
