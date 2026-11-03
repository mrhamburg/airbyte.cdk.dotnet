# Vendored Airbyte protocol protobuf definitions

These files are copied verbatim from the Airbyte platform repository:

- Repository: <https://github.com/airbytehq/airbyte>
- Commit: `5ec24c9dfadb865dae70ffa4b828b4a65ace2263`
- Path: `airbyte-cdk/bulk/core/base/src/main/proto/`

They are vendored so the CDK and the integration test harness can build offline and
so both sides always use the same wire definitions. Do not edit them locally; update
the whole set from a newer Airbyte commit when the protocol changes.

Wire notes:

- Messages are length-delimited: a varint length prefix followed by the serialized
  `AirbyteMessageProtobuf`.
- `AirbyteMessageProtobuf.record` is the typed record path. The .NET CDK currently
  emits records through `airbyte_protocol_message`, the protocol's JSON fallback,
  which the platform destination accepts for every message type. This keeps record
  typing schema-correct without reimplementing the platform's schema-aware
  Protobuf value encoder.
- `partition_id` and the state `id` are additional JSON properties on records and
  state messages, matching `Meta.CHECKPOINT_ID_NAME` and `Meta.CHECKPOINT_INDEX_NAME`
  in the Kotlin CDK.
- The source binds and listens on each `DATA_CHANNEL_SOCKET_PATHS` entry. The
  destination connects to those paths as a client.
