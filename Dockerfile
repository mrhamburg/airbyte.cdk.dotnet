# Build and publish cdk
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build

ENV BUILD_VERSION="0.0.1"
ENV WORKDIR=/airbyte/build
WORKDIR $WORKDIR

COPY . ./

RUN dotnet test

WORKDIR $WORKDIR/Airbyte.Cdk
RUN dotnet build -c Release -p:Version=$BUILD_VERSION -o output

FROM build AS publish
ENV NUGET_APIKEY=""
ENV NUGET_SOURCE="https://api.nuget.org/v3/index.json"

ENV WORKDIR=/airbyte/integration_code
WORKDIR $WORKDIR
COPY --from=build /airbyte/build/Airbyte.Cdk/output .
RUN dotnet nuget push ./Airbyte.Cdk.$BUILD_VERSION.nupkg --api-key $NUGET_APIKEY --source $NUGET_SOURCE
