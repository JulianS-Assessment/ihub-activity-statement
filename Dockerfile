FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY IHub.Statements.slnx .
COPY IHub.Statements.Api/IHub.Statements.Api.csproj IHub.Statements.Api/
COPY IHub.Statements.IntegrationTests/IHub.Statements.IntegrationTests.csproj IHub.Statements.IntegrationTests/
RUN dotnet restore IHub.Statements.slnx
COPY . .
RUN dotnet publish IHub.Statements.Api -c Release -o /app --no-restore

FROM build AS tests
ENTRYPOINT ["dotnet", "test", "IHub.Statements.slnx", "-c", "Release", "--no-restore"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "IHub.Statements.Api.dll"]
