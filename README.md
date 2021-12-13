# Airbyte .NET CDK  

 [![CI](https://github.com/mrhamburg/airbyte.cdk.dotnet/actions/workflows/build.yml/badge.svg?query=branch%3Amain)](https://github.com/mrhamburg/airbyte.cdk.dotnet/actions/workflows/build.yml?query=branch%3Amain) [![CI](https://github.com/mrhamburg/airbyte.cdk.dotnet/actions/workflows/release.yml/badge.svg)](https://github.com/mrhamburg/airbyte.cdk.dotnet/actions/workflows/release.yml)  

- [Quick Start](#quick-start)
- [Components](#components)
- [Concepts & Documentation](#concepts--documentation)
- [Contributing - Connector](#contributing---connector)
- [Contributing - CDK](#contributing---cdk)

The Airbyte Dotnet CDK is a framework for rapidly developing production-grade Airbyte connectors. The CDK currently offers helpers specific for creating Airbyte source connectors for:

* HTTP APIs \(REST APIs, GraphQL, etc..\)
* Generic .NET sources \(anything not covered by the above\)

The CDK provides an improved developer experience by providing basic implementation structure and abstracting away low-level glue boilerplate.

## Quick Start

Generate a boilerplate connector using the code generator. Create a new directory and run:

```bash
git clone https://github.com/mrhamburg/airbyte.cdk.dotnet.git
cd airbyte.cdk.dotnet
dotnet run init --project Airbyte.Cdk
```

## Components

Component | Type |Code | Installation | Version
----------|------|-----|------|--------
Airbyte CDK | CDK | [Airbyte.Cdk](Airbyte.Cdk) | `dotnet add package Airbyte.Cdk` |[![NetMQ NuGet version](https://img.shields.io/nuget/v/Airbyte.Cdk.svg)](https://www.nuget.org/packages/Airbyte.Cdk/)
Exchange Rates Free | Source | [source-exchange-rates-free](airbyte-integrations/connectors/source-exchange-rates-free) | `docker pull airbytedotnet/source-exchange-rates-free` | ![Docker Image Version (latest semver)](https://img.shields.io/docker/v/airbytedotnet/source-exchange-rates-free?sort=semver&style=flat)

## Concepts & Documentation
This readme is a general introduction to the CDK. Readers should have basic familiarity with the [Airbyte Specification](https://docs.airbyte.io/architecture/airbyte-specification) before proceeding.

If you have any issues with troubleshooting or want to learn more about the CDK from the Airbyte team, head to the \#connector-development channel in [Slack](https://airbytehq.slack.com/ssb/redirect) to inquire further!

### Basic Concepts
If you want to learn more about the classes required to implement an Airbyte Source, head to our [basic concepts doc](https://docs.airbyte.io/connector-development/cdk-python/basic-concepts) as described in the python-cdk.
### Full Refresh Streams
A `Stream` is the atomic unit for reading data from a Source. A stream can read data from anywhere: a relational database, an API, or even scrape a web page! \(although that might be stretching the limits of what a connector should do\).
To implement a stream, there are two minimum requirements: 1. Define the stream's schema 2. Implement the logic for reading records from the underlying data source
Schema's should be stored in the schemas folder part of your project.
Using the fluentbuilder we can define a source. The short and simple example below, does the following steps:
1. Create a string `url` as a baseurl for all subsequent requests
2. Create a base implementation by converting the string to a HttpStream and set a default response parser being the whole object (this function expects a [JsonPath](https://github.com/json-path/JsonPath) expression to extract the data)
3. Create a new http stream using the create statement and name it symbols (the name `symbols` will also be appended to the url when executing the request, thus the example below will result in an executed endpoint being: `https://api.exchangerate.host/symbols`)
```csharp
public override Stream[] Streams(JsonElement config)
{
    string url = "https://api.exchangerate.host";
    var baseimpl = url.HttpStream().ParseResponseObject("$");
    return new Stream[] { baseimpl.Create("symbols") };
}
```

### Incremental Streams
An incremental Stream is a stream which reads data incrementally. That is, it only reads data that was generated or updated since the last time it ran, and is thus far more efficient than a stream which reads all the source data every time it runs. If possible, developers are encouraged to implement incremental streams to reduce sync times and resource usage.

Several new pieces are essential to understand how incrementality works with the CDK:

* Cursor fields
* `AirbyteStateMessage`
* `Stream.GetUpdatedState`

The `AirbyteStateMessage` is sent based on the `StateCheckpointInterval` setting of a stream object. Every N number of requests will result in sending out an `AirbyteStateMessage`. In the example below, this is every 25th request for the `symbols` stream.

The `CursorField` refers to the field in the stream's output records used to determine the "recency" or ordering of records. An example is a `date` field in an API, as shown in the example below.

Cursor fields can be input by the user \(e.g: a user can choose to use an auto-incrementing `id` column in a DB table\) or they can be defined by the source e.g: where an API defines that `date` is what determines the ordering of records.

In the context of the CDK, setting the `Stream.CursorField` property to any truthy value informs the framework that this stream is incremental.

`Stream.GetUpdatedState`, this function helps the stream keep track of the latest state by inspecting every record output by the stream \(as returned by the `Stream.ReadRecords` method\) and comparing it against the most recent state object. This allows sync to resume from where the previous sync last stopped, regardless of success or failure. This function typically compares the state object's and the latest record's cursor field, picking the latest one.


```csharp
public override Stream[] Streams(JsonElement config)
{
...
    Dictionary<string, DateTime> _currentstate = new Dictionary<string, DateTime>();
    string basesymbol = config.GetProperty("symbol").GetString();
    var incremental = baseimpl
        .CursorField(new[] {"date"})
        .BackoffTime(((i, _) => TimeSpan.FromMinutes(i * 10)))
        .GetUpdatedState((_, _) => _currentstate.AsJsonElement())
        .RequestParams((_, _, _) => new Dictionary<string, object> {{ "date", _currentstate[basesymbol] } })
        .BackoffTime((i, response) =>
            response.StatusCode == 429 ? i * TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(1))
        .HttpMethod(HttpMethod.Get)
        .PageSize(150)
        .StateCheckpointInterval(25)
        .MaxRetries(15)
        .ShouldRetry(exc => exc.StatusCode > 300)
        .WithAuth(new BasicAuth(new[] {config.GetProperty("api-token").GetString()}))
        .Create("symbols");
...
}
```

### Practical Tips

Airbyte recommends using the CDK template generator to develop with the CDK. The template generates created all the required scaffolding, with convenient TODOs, allowing developers to truly focus on implementing the API.

## Contributing - Connector

### First time setup

Make sure the latest version of dotnet is installed, this can be found using the following link: [Dotnet SDK](https://dotnet.microsoft.com/download). Also, make sure you have docker installed, information to do so can be found here: [Get Docker](https://docs.docker.com/get-docker/)

Run the following commands to get a boilerplate for creating a new connector:
```bash
git clone https://github.com/mrhamburg/airbyte.cdk.dotnet.git
cd airbyte.cdk.dotnet
dotnet run init --project Airbyte.Cdk
```

Once ready, check the README.md file in your connector directory for instructions. Include your connector in this readme as well, see the components section at the top.

#### Iteration

* Iterate on the code locally
* Run tests via `dotnet test`
* Try to build the connector using docker `docker build .`

#### Testing

All tests are located in the `*.Tests` directory where your connector is created. Run `dotnet test` to run them.

#### Publishing a new version of a connector

1. Open a PR
2. Once ready, update the CHANGELOG.md file, which should include a new version number 
3. Connectors are automatically published to Docker Hub after updates to the main branch. If the connector is updated without incrementing the version, GitHub will NOT overwrite the existing image in Docker Hub.

## Contributing - CDK

### First time setup

Make sure the latest version of dotnet is installed, this can be found using the following link: [Dotnet SDK](https://dotnet.microsoft.com/download). Also, make sure you have docker installed, information to do so can be found here: [Get Docker](https://docs.docker.com/get-docker/)

#### Iteration

* Iterate on the code locally
* Run tests via `dotnet test`
* Try to build the nuget package using docker `docker build --target build .`

#### Testing

All tests are located in the `Airbyte.Cdk.Test` directory. Run `dotnet test` to run them.

#### Publishing a new version to NuGet

1. Open a PR
2. Testing and releasing is part of the CI/CD process

## Coming Soon

* Don't see a feature you need? [Create an issue and let us know how we can help!](https://github.com/mrhamburg/airbyte.cdk.dotnet/issues/new)

