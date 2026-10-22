# syntax=docker/dockerfile:1
# Build context: experiments/transactional-outbox

FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY src/WorkOrders/WorkOrders.csproj src/WorkOrders/
COPY tests/WorkOrders.Tests/WorkOrders.Tests.csproj tests/WorkOrders.Tests/
RUN dotnet restore tests/WorkOrders.Tests/WorkOrders.Tests.csproj
COPY src/ src/
COPY tests/ tests/

FROM build AS publish
RUN dotnet publish src/WorkOrders/WorkOrders.csproj -c Release -o /app --no-restore

# Test runner: runs the xUnit suite against the compose PostgreSQL/RabbitMQ.
FROM build AS test
RUN dotnet build tests/WorkOrders.Tests/WorkOrders.Tests.csproj -c Release --no-restore
ENTRYPOINT ["dotnet", "test", "tests/WorkOrders.Tests/WorkOrders.Tests.csproj", "-c", "Release", "--no-build", \
            "--logger", "console;verbosity=normal"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS runtime
WORKDIR /app
COPY --from=publish /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "WorkOrders.dll"]
