# Build and test cdk
# Publishing to nuget.org happens in .github/workflows/release.yml (trusted publishing),
# so this image never sees a NuGet credential.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG BUILD_VERSION="0.0.1"
ARG WORKDIR=/airbyte/build
WORKDIR $WORKDIR

COPY . ./

RUN dotnet test

WORKDIR $WORKDIR/Airbyte.Cdk
RUN dotnet build -c Release -p:Version=$BUILD_VERSION -o output
