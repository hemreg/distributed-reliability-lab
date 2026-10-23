# syntax=docker/dockerfile:1
# Build context: experiments/retry-resilience
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.13

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY src/Dependency/Dependency.csproj src/Dependency/
COPY src/LoadGenerator/LoadGenerator.csproj src/LoadGenerator/
COPY tests/RetryResilience.Tests/RetryResilience.Tests.csproj tests/RetryResilience.Tests/
RUN dotnet restore tests/RetryResilience.Tests/RetryResilience.Tests.csproj
COPY src/ src/
COPY tests/ tests/

FROM build AS publish
RUN dotnet publish src/Dependency/Dependency.csproj -c Release -o /app/dependency --no-restore \
 && dotnet publish src/LoadGenerator/LoadGenerator.csproj -c Release -o /app/loadgen --no-restore

# Test runner: unit tests only, no other containers needed.
FROM build AS test
RUN dotnet build tests/RetryResilience.Tests/RetryResilience.Tests.csproj -c Release --no-restore
ENTRYPOINT ["dotnet", "test", "tests/RetryResilience.Tests/RetryResilience.Tests.csproj", "-c", "Release", "--no-build", \
            "--logger", "console;verbosity=normal"]

# One image, two programs: compose picks /app/dependency or /app/loadgen.
FROM ${RUNTIME_IMAGE} AS runtime
ARG SDK_IMAGE
ARG RUNTIME_IMAGE
# Recorded in results/summary.json.
ENV E03_IMAGES="build=${SDK_IMAGE} runtime=${RUNTIME_IMAGE}"
WORKDIR /app
COPY --from=publish /app .
USER $APP_UID
