# Build and test implementation
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build

ARG BUILD_VERSION="0.0.1"
ENV WORKDIR=/airbyte/build
WORKDIR $WORKDIR

COPY . ./

RUN dotnet test
RUN dotnet build -c Release -o output

# Run implementation
FROM mcr.microsoft.com/dotnet/runtime:6.0 AS runtime

ENV WORKDIR=/airbyte/integration_code
WORKDIR $WORKDIR

COPY --from=build /airbyte/build/output .

ENV AIRBYTE_IMPL_PATH="/airbyte/integration_code/source_exchange_rates.dll"
ENV AIRBYTE_IMPL_MODULE="ExchangeRates"
ENV AIRBYTE_ENTRYPOINT "dotnet /airbyte/integration_code/source_exchange_rates.dll read --command"
ENTRYPOINT ["dotnet", "/airbyte/integration_code/source_exchange_rates.dll", "read", "--command"]

LABEL io.airbyte.version=$BUILD_VERSION
LABEL io.airbyte.name=airbytedotnet/source-exchange-rates-free