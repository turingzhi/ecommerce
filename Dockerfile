FROM node:22.22.0-bookworm-slim AS storefront
WORKDIR /storefront
COPY src/storefront/package*.json ./
RUN npm ci
COPY src/storefront/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ecommerce.Api/Ecommerce.Api.csproj ./Ecommerce.Api/
RUN dotnet restore Ecommerce.Api/Ecommerce.Api.csproj
COPY src/Ecommerce.Api/ ./Ecommerce.Api/
RUN dotnet publish Ecommerce.Api/Ecommerce.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=storefront /storefront/dist ./wwwroot
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ecommerce.Api.dll"]
