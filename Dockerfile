FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY cloud/RestaurantOrders.Cloud.Api/RestaurantOrders.Cloud.Api.csproj cloud/RestaurantOrders.Cloud.Api/
RUN dotnet restore cloud/RestaurantOrders.Cloud.Api/RestaurantOrders.Cloud.Api.csproj

COPY cloud/RestaurantOrders.Cloud.Api/ cloud/RestaurantOrders.Cloud.Api/
RUN dotnet publish cloud/RestaurantOrders.Cloud.Api/RestaurantOrders.Cloud.Api.csproj \
    --configuration Release \
    --output /out \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet", "RestaurantOrders.Cloud.Api.dll"]

