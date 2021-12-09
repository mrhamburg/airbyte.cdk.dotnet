# Build and publish cdk
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build

ARG BUILD_VERSION="0.0.1"
ARG WORKDIR=/airbyte/build
WORKDIR $WORKDIR

COPY . ./

RUN dotnet test

WORKDIR $WORKDIR/Airbyte.Cdk
RUN dotnet build -c Release -p:Version=$BUILD_VERSION -o output

FROM build AS publish
ARG NUGET_APIKEY=""
ARG NUGET_SOURCE="https://api.nuget.org/v3/index.json"

ARG WORKDIR=/airbyte/integration_code
WORKDIR $WORKDIR
COPY --from=build /airbyte/build/Airbyte.Cdk/output .
RUN dotnet nuget push ./Airbyte.Cdk.$BUILD_VERSION.nupkg --api-key $NUGET_APIKEY --source $NUGET_SOURCE
