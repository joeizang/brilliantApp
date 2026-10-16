# Build from the repository root:  docker build -f src/Brilliant.Server/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Brilliant.Core/Brilliant.Core.csproj src/Brilliant.Core/
COPY src/Brilliant.Server/Brilliant.Server.csproj src/Brilliant.Server/
RUN dotnet restore src/Brilliant.Server/Brilliant.Server.csproj
COPY src/Brilliant.Core src/Brilliant.Core
COPY src/Brilliant.Server src/Brilliant.Server
COPY apps apps
RUN dotnet publish src/Brilliant.Server/Brilliant.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The base image listens on plain 8080 by default; this server is HTTPS only, on 8443 (see ServerTls).
ENV ASPNETCORE_HTTP_PORTS=
EXPOSE 8443
USER $APP_UID
ENTRYPOINT ["dotnet", "Brilliant.Server.dll"]
