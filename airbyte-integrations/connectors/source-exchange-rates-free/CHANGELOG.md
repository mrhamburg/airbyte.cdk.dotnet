# Changelog

## 0.0.1
Initial release, using dotnet CDK

## 0.0.2
Corrected entrypoint, due to airbyte [specifications](https://airbyte.io/blog/extending-docker-images-on-kubernetes) for kubernetes

## 0.0.3
Replaced the retired exchangerate.host free endpoint with the Frankfurter API, updated schemas, and fixed the sample catalog/state to use the connector's actual streams.
