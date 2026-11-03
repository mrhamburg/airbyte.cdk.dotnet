FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG BUILD_VERSION="0.0.1"
ENV WORKDIR=/airbyte/build
WORKDIR $WORKDIR

COPY . ./

WORKDIR $WORKDIR/airbyte-integrations/connectors/destination-local-parquet

RUN dotnet test tests/Destination.LocalParquet.Tests/Destination.LocalParquet.Tests.csproj
RUN dotnet build -c Release -o output

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

ENV WORKDIR=/airbyte/integration_code
WORKDIR $WORKDIR

COPY --from=build /airbyte/build/airbyte-integrations/connectors/destination-local-parquet/output .

ENV AIRBYTE_IMPL_PATH="/airbyte/integration_code/destination_local_parquet.dll"
ENV AIRBYTE_IMPL_MODULE="LocalParquet"
ENV AIRBYTE_ENTRYPOINT "dotnet /airbyte/integration_code/destination_local_parquet.dll write --command"
ENTRYPOINT ["dotnet", "/airbyte/integration_code/destination_local_parquet.dll", "write", "--command"]

LABEL io.airbyte.version=$BUILD_VERSION
LABEL io.airbyte.name=airbytedotnet/destination-local-parquet
