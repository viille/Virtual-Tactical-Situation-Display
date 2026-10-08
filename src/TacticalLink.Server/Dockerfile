FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TacticalDisplay.Core/TacticalDisplay.Core.csproj src/TacticalDisplay.Core/
COPY src/TacticalLink.Server/TacticalLink.Server.csproj src/TacticalLink.Server/
RUN dotnet restore src/TacticalLink.Server/TacticalLink.Server.csproj
COPY src/TacticalDisplay.Core/ src/TacticalDisplay.Core/
COPY src/TacticalLink.Server/ src/TacticalLink.Server/
RUN dotnet publish src/TacticalLink.Server/TacticalLink.Server.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
RUN mkdir -p /var/lib/tactical-link && chown -R $APP_UID:$APP_UID /var/lib/tactical-link
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["dotnet", "TacticalLink.Server.dll"]
