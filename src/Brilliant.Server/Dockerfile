# Build from the repository root:  docker build -f src/Brilliant.Server/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Brilliant.Core/Brilliant.Core.csproj src/Brilliant.Core/
COPY src/Brilliant.Server/Brilliant.Server.csproj src/Brilliant.Server/
RUN dotnet restore src/Brilliant.Server/Brilliant.Server.csproj
COPY src/Brilliant.Core src/Brilliant.Core
COPY src/Brilliant.Server src/Brilliant.Server
RUN dotnet publish src/Brilliant.Server/Brilliant.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Brilliant.Server.dll"]
