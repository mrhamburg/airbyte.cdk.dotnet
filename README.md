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
Jortt | Source | [source-jortt](airbyte-integrations/connectors/source-jortt) | `docker pull airbytedotnet/source-jortt` | ![Docker Image Version (latest semver)](https://img.shields.io/docker/v/airbytedotnet/source-jortt?sort=semver&style=flat)
OpenDAL Parquet | Destination | [destination-opendal-parquet](airbyte-integrations/connectors/destination-opendal-parquet) | `docker pull airbytedotnet/destination-opendal-parquet` | ![Docker Image Version (latest semver)](https://img.shields.io/docker/v/airbytedotnet/destination-opendal-parquet?sort=semver&style=flat)

## Protocol compatibility

The CDK targets the Airbyte protocol **v0.19.0**. STDIO/JSON is the default transport. Sources emit per-stream `STREAM` state messages (under `state.stream.stream_descriptor` and `state.stream.stream_state`) and include `sourceStats.recordCount`. On input, the `--state` file may contain a legacy stream-name map, a single `STREAM`/`GLOBAL`/`LEGACY` state message, an `AirbyteMessage` envelope, or a list of state messages.

### Socket mode

Set these environment variables to use the platform's socket data channel:

```bash
DATA_CHANNEL_MEDIUM=SOCKET
DATA_CHANNEL_FORMAT=PROTOBUF
DATA_CHANNEL_SOCKET_PATHS=/var/run/sockets/airbyte_socket_1.sock,/var/run/sockets/airbyte_socket_2.sock
```

In socket mode the source binds one Unix domain socket per path and the destination connects to them.
Records and states travel over the sockets as length-delimited Protobuf, while logs, traces and control
messages stay on stdout. State messages are also copied to stdout for the bookkeeper. Each checkpoint
segment gets a `partition_id`; records carry it, and the matching state carries it plus an incrementing
`id`. The destination only commits a state after all records for that partition have been written.

The .NET CDK sends every message through the Protobuf `airbyte_protocol_message` JSON field, which the
platform destination accepts for record, state and control messages. This keeps record values
schema-correct without reimplementing the platform's typed value encoder. Typed records produced by
other CDKs are decoded when the configured catalog provides the stream schema. Without the environment
variables the CDK keeps using STDIO/JSON and no behavior changes.

Connector specifications should declare `protocol_version`; `Connector.Spec()` defaults it to `0.19.0` when it is absent.

### Migration notes

- Sources now emit per-stream `STREAM` state instead of legacy `{"state":{"data":...}}` messages. Legacy state files are still accepted on input.
- The state passed to a source can now be a JSON array of per-stream state messages. `AbstractSource.State` remains a `JsonElement`, but callers must not assume it is an object.
- Destinations must flush pending records before returning from `Destination.Write` when the message is a `STATE`; the CDK echoes that state immediately after `Write` returns.
- Destinations may override the new no-op `Destination.CompleteAsync`; the CDK calls it once after the input ends, before the process exits. Use it for a final flush or deferred cleanup such as removing old `overwrite` files.
- Do not mix legacy `data` state and per-stream `STREAM` state within one source/sync; pick one representation.
- Connector package pins should move to the next published CDK version before a connector image is rebuilt with these changes.

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

The `AirbyteStateMessage` is sent based on the `StateCheckpointInterval` setting of a stream object. Every N number of requests will result in sending out an `AirbyteStateMessage`. In the example below, this is every 25th request for the `symbols` stream. State is emitted as a per-stream `STREAM` message, not the legacy `{"state": {"data": ...}}` shape, and a terminal state is emitted for full refresh streams as well.

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

### Substreams (parent and child streams)

Some APIs only expose a collection below a parent object, for example `projects/{project_id}/line_items`. Declare
the child as a substream and it is read once per parent record, with the parent key available to the request
callbacks through the stream slice:

```csharp
var projects = UrlBase.HttpStream()
    .Path((_, _, _) => "v3/projects")
    .Create("projects");

var lineItems = UrlBase.HttpStream()
    .Path((_, streamSlice, _) => $"v3/projects/{streamSlice["project_id"]}/line_items")
    .Dependency(new SubstreamDependency("projects", "id", "project_id"))
    .Create("project_line_items");
```

`SubstreamDependency(parentStream, parentKeyField, sliceParameter)` supports a dotted key path (`customer.id`).

- **The parent is read once.** When it is in the catalog its records are emitted and reused; when it is not, it is
  read internally without emitting records. Order does not matter: if the substream is read first, the parent's
  records are replayed from the cache when its own catalog entry is reached.
- **Parent records without the key are skipped** with a warning rather than failing the sync.
- **State belongs to the substream, not to each parent.** A substream is a stream: it keeps one cursor, and the
  CDK feeds the last state it emitted into the next parent's read so the cursor cannot move backwards when parents
  come back in an arbitrary order. The stream's `GetUpdatedState` still decides how to merge, so implement it as a
  maximum (`max(current state, record cursor)`) when the API has no ordering guarantee.
- **`incremental` is supported** as long as the stream reports a cursor and the API filters per parent; the parent
  key is merged into whatever `StreamSlices` returns.
- **Nested substreams are rejected** with an explicit message: a substream cannot itself be the parent of another.

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
* Try to build the connector using docker from the repository root: `docker build -f airbyte-integrations/connectors/<connector>/Dockerfile .`

#### Testing

All tests are located in the `*.Tests` directory where your connector is created. Run `dotnet test` to run them.

#### Integration tests

Every connector declares one or more live scenarios in `integration_tests.yaml` next to its project file.
`tests/Airbyte.Cdk.IntegrationTests` builds the connector, runs `check`, then `discover`, then `read`, and
validates every emitted record against the JSON schema the connector advertises for that stream. It is a
black-box client: it speaks the Airbyte JSONL protocol and never references the connector assembly.

```yaml
connector:
  module: ExchangeRates        # optional; defaults to ENV AIRBYTE_IMPL_MODULE in the Dockerfile
secrets:                       # optional; config property name -> secret key
  api_key: SOURCE_EXAMPLE_API_KEY
tests:
  - name: full refresh
    config:
      start_date: "2026-10-01"
      retries: 3               # unquoted scalars keep their type; quote anything that should stay a string
    limit: 50                  # per stream: the connector is stopped once every listed stream has 50 records
    socket: false              # optional; true runs the read over Unix domain sockets with Protobuf
    streams:
      - name: latest
      - name: orders
        sync_mode: incremental # rejected when discover does not advertise incremental for the stream
        state:                 # optional starting state, passed to the connector as-is
          orders: { cursor: "2026-10-01" }
        min_records: 1         # default 1; use 0 for a stream that may legitimately be empty
```

Run a connector locally:

```bash
dotnet run --project tests/Airbyte.Cdk.IntegrationTests -- \
  run --connector airbyte-integrations/connectors/source-exchange-rates-free
```

Schemas are checked strictly: a record field the stream's schema does not declare fails the run, so schema
drift is reported instead of silently accepted. Typed maps (an object whose schema only declares
`additionalProperties`, such as a currency table) stay permissive, because a new key there is data rather
than drift. Schemas composed with `allOf`/`anyOf`/`oneOf` or `$ref` are left open, because closing one
branch would reject the fields declared by its siblings.

A scenario with `limit` stops the connector once **each** listed stream has produced that many records, and keeps
at most that many records per stream for validation. Streams are read one after another, so the first stream is
still fetched in full until the later ones reach the cap; the retained slice keeps memory bounded regardless.

An `incremental` scenario must also emit at least one `STATE` message, so a connector that degrades to a full
refresh cannot pass quietly.

Destination connectors use a different scenario shape. Set `connector.type: destination` and add a `source`
block naming the source connector directory and its config. The harness builds that source with
`-p:UseLocalCdk=true` until the next CDK release ships the Protobuf socket transport. Destination scenarios run
over Protobuf sockets only and cannot declare `limit` or `socket: false`; the harness injects `output_path` into
the destination config for each scenario and validates the files it writes. Source config and state values
may use `{{today-N}}` (for example `{{today-7}}`) to keep a live scenario bounded as time passes.

Secrets are resolved through the `ISecretSource` seam. Three implementations ship:

- **PostgreSQL store** (`mrhamburg.SecretsManager.PostgreSql`) — selected as soon as `SECRETS_POSTGRESQL_CONNECTION_STRING`
  is set. Connection details never appear in the repository: every value comes from environment variables
  (`SECRETS_POSTGRESQL_CONNECTION_STRING`, `_SCHEMA`, `_TABLE_PREFIX`, `_AUTO_CREATE_SCHEMA`, `_ENCRYPTION_KEY`,
  `_ENCRYPTION_KEY_FILE`). A YAML secret key is the key in the store. Use a role that can only read the secret
  table, so a leaked connection string cannot rewrite credentials.
- **JSON object** in `AIRBYTE_INTEGRATION_SECRETS_JSON`.
- **Environment variables**, where a secret key is the variable name.

A scenario whose secrets are unavailable is reported as **skipped**, never as passed; once a secret store is
configured (`SECRETS_POSTGRESQL_CONNECTION_STRING` or `AIRBYTE_INTEGRATION_SECRETS_JSON`) the workflow passes
`--no-skip` on `main`, so a missing credential then fails the build instead of looking green. Secret values are
masked in error output, and the rendered `config.json` is deleted after a run unless `--keep-artifacts` is set, in
which case the secret values in it are masked.

`.github/workflows/integration-tests.yml` runs only the connectors whose files changed in a push to `main`, plus
manual runs. It has no `pull_request` trigger, because that event executes the workflow file of the pull request
head with the repository's secrets.

The job that needs credentials references the `integration` GitHub environment. GitHub checks the environment's
deployment branch policy against the ref of the run, so a branch or a pull request is refused those secrets however
the workflow is edited; only `main` can deploy. The job gets no store credentials at repository level. The harness
also removes `SECRETS_POSTGRESQL_*` and `AIRBYTE_INTEGRATION_SECRETS_JSON` from the processes it starts, so a
connector only sees the values it was given through its config file.

The PostgreSQL store is not reachable from the public internet: it sits behind a NetBird network. Before reading a
secret the job joins that network with [NetBird Connect](https://github.com/marketplace/actions/netbird-connect)
(`Alemiz112/netbird-connect`, pinned to a commit) and leaves it again in an `always()` step. After the connection
the job accepts the network routes with `netbird routes select all` (`netbird up` has no route flag), so the network
must advertise the route to the database (a routing peer, or the database itself as a peer). Without
`NETBIRD_SETUP_KEY` the steps are skipped and the scenarios fall back to their skipped state.

One-time setup (repository admin):

- Create the `integration` environment and set *Deployment branches and tags* to *Selected branches* → `main`.
- Put `SECRETS_POSTGRESQL_CONNECTION_STRING` and `SECRETS_POSTGRESQL_ENCRYPTION_KEY` in that environment, and delete
  the repository-level copies; otherwise `secrets.*` still resolves outside the environment. The job pins
  `SECRETS_POSTGRESQL_SCHEMA` to `secrets`, so the store's tables must live in that schema (not the provider
  default, `public`).
- Put a NetBird **ephemeral** setup key in `NETBIRD_SETUP_KEY`, and `NETBIRD_MANAGEMENT_URL` when the network is
  self-hosted (leave it empty for NetBird cloud). An ephemeral key removes the peer when the runner disconnects.
- Scope the NetBird policy for that key to the database host and port, and use a read-only PostgreSQL role: a run
  on `main` executes repository code with network access to the store.
- Optionally add required reviewers, so every post-merge run also needs an approval.

The credentialed job runs the whole repository at `main`, so restrict who can merge changes under `.github/`,
`tests/` and the package configuration (`Directory.Packages.props`, `NuGet.Config`) with required reviews.

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
3. To publish, create a GitHub release with a SemVer tag (e.g. `0.0.8`). The `release.yml` workflow tests the CDK, packs it with that version, and pushes it to nuget.org.

The package is published with [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), so no NuGet API key is stored in this repository. A short-lived key is requested through GitHub OIDC, which requires:

* A trusted publishing policy on [nuget.org](https://www.nuget.org/account/trustedpublishing) for repository owner `mrhamburg`, repository `airbyte.cdk.dotnet`, workflow file `release.yml`, environment `release`.
* A GitHub environment named `release` holding the secret `NUGET_USER` (your nuget.org profile name, not your email address).

## Coming Soon

* Don't see a feature you need? [Create an issue and let us know how we can help!](https://github.com/mrhamburg/airbyte.cdk.dotnet/issues/new)

