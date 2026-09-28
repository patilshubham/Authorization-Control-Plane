# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src

COPY backend/src/Authorization.Infrastructure/Authorization.Infrastructure.csproj backend/src/Authorization.Infrastructure/
COPY backend/src/Authorization.Ai/Authorization.Ai.csproj backend/src/Authorization.Ai/
COPY backend/src/Authorization.Api/Authorization.Api.csproj backend/src/Authorization.Api/
RUN dotnet restore backend/src/Authorization.Api/Authorization.Api.csproj

COPY backend/src/ backend/src/
RUN dotnet publish backend/src/Authorization.Api/Authorization.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

WORKDIR /app
RUN apk add --no-cache krb5-libs
COPY --from=build /app/publish ./

EXPOSE 8080

ENTRYPOINT ["dotnet", "Authorization.Api.dll"]
