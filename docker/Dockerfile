# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src ./src
RUN dotnet publish src/Intellifer.Hub.Api/Intellifer.Hub.Api.csproj -c Release -o /out/api \
    && dotnet publish src/Intellifer.Hub.Worker/Intellifer.Hub.Worker.csproj -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
# No apt-get: the health check is the API itself (dotnet Intellifer.Hub.Api.dll --healthcheck),
# so the build never depends on Ubuntu's package mirrors being reachable.
WORKDIR /app
COPY --from=build /out/api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Intellifer.Hub.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
USER $APP_UID
ENTRYPOINT ["dotnet", "Intellifer.Hub.Worker.dll"]
