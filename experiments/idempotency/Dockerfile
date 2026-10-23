# syntax=docker/dockerfile:1
# Build context: experiments/idempotency

FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY src/Lab.Shared/Lab.Shared.csproj src/Lab.Shared/
COPY src/WorkOrders/WorkOrders.csproj src/WorkOrders/
COPY src/Inventory/Inventory.csproj src/Inventory/
COPY tests/Idempotency.Tests/Idempotency.Tests.csproj tests/Idempotency.Tests/
RUN dotnet restore tests/Idempotency.Tests/Idempotency.Tests.csproj
COPY src/ src/
COPY tests/ tests/

FROM build AS publish
RUN dotnet publish src/WorkOrders/WorkOrders.csproj -c Release -o /app/workorders --no-restore \
 && dotnet publish src/Inventory/Inventory.csproj -c Release -o /app/inventory --no-restore

# Test runner: runs the xUnit suite against the compose PostgreSQL/RabbitMQ.
FROM build AS test
RUN dotnet build tests/Idempotency.Tests/Idempotency.Tests.csproj -c Release --no-restore
ENTRYPOINT ["dotnet", "test", "tests/Idempotency.Tests/Idempotency.Tests.csproj", "-c", "Release", "--no-build", \
            "--logger", "console;verbosity=normal"]

# One runtime image with both programs; compose.yaml picks the entrypoint per service.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS runtime
WORKDIR /app
COPY --from=publish /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/workorders/WorkOrders.dll"]
