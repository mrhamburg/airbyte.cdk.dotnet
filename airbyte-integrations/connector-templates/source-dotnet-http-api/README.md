# {{connectorname}} Source

This is the repository for the {{connectorname}} source connector, written in C#.
For information about how to use this connector within Airbyte, see [the documentation](https://docs.airbyte.io/integrations/sources/{{connectorname}}).

## Local development

### Prerequisites
**To iterate on this connector, make sure to complete this prerequisites section.**

#### Minimum Dotnet version required `= 6.0.x`

### Locally running the connector
```
dotnet read --command spec
dotnet read --command check --config sample_files/config.json
dotnet read --command discover --config sample_files/config.json
dotnet read --command read --config sample_files/config.json --catalog sample_files/configured_catalog.json
```

### Locally running the connector docker image

#### Build
First, make sure you build the latest Docker image:
```
docker build . -t airbytedotnet/{{connectorname}}:dev
```

#### Run
Then run any of the connector commands as follows:
```
docker run --rm airbyte/{{connectorname}}:dev spec
docker run --rm -v $(pwd)/secrets:/secrets airbyte/{{connectorname}}:dev check --config /sample_files/config.json
docker run --rm -v $(pwd)/secrets:/secrets airbyte/{{connectorname}}:dev discover --config /sample_files/config.json
docker run --rm -v $(pwd)/secrets:/secrets -v $(pwd)/integration_tests:/integration_tests airbyte/{{connectorname}}:dev read --config /sample_files/config.json --catalog /sample_files/configured_catalog.json
```
## Testing
To run tests locally, from the connector directory run:
```
dotnet test
```

### Publishing a new version of the connector
You've checked out the repo, implemented a million dollar feature, and you're ready to share your changes with the world. Now what?
1. Make sure your changes are passing all tests.
1. Bump the connector version in the `CHANEGLOG.md` file -- we use [SemVer](https://semver.org/).
1. Create a Pull Request.
1. Pat yourself on the back for being an awesome contributor.
1. Someone from the community will take a look at your PR and iterate with you to merge it into master.
