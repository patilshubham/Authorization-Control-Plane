# syntax=docker/dockerfile:1

FROM node:24-alpine AS build

WORKDIR /app

ARG VITE_API_BASE_URL=http://localhost:8080
ARG VITE_OIDC_AUTHORITY=http://localhost:8081/realms/authorization-local
ARG VITE_OIDC_CLIENT_ID=authorization-portal

ENV VITE_API_BASE_URL=$VITE_API_BASE_URL \
    VITE_OIDC_AUTHORITY=$VITE_OIDC_AUTHORITY \
    VITE_OIDC_CLIENT_ID=$VITE_OIDC_CLIENT_ID

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

FROM nginx:1.29-alpine AS runtime

COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]