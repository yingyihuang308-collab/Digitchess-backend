FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Backend/Backend.csproj src/Backend/
RUN dotnet restore src/Backend/Backend.csproj
COPY src/Backend/ src/Backend/
RUN dotnet publish src/Backend/Backend.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Backend.dll"]
