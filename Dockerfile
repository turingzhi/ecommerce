FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Ecommerce.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish ./
RUN mkdir /data && chown $APP_UID:$APP_UID /data
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
ENV Ecommerce__DatabasePath=/data/ecommerce-identity.db
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ecommerce.dll"]
