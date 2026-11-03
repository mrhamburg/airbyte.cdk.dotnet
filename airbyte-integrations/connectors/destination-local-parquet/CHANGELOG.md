# Changelog

## 0.0.2
Add Apache OpenDAL storage. Any OpenDAL service can be selected with inline config, a settings file, or
environment variables; `fs` remains the default. Add configurable Parquet compression, row-group size,
flush-on-state, and file-name template.

## 0.0.1
Initial release. Writes Airbyte records to typed Parquet files under `output_path`.

Follow-up: switch the `ProjectReference` to `Airbyte.Cdk` back to the NuGet package after the next CDK
release that contains the Protobuf socket transport. Tracked in
[#31](https://github.com/mrhamburg/airbyte.cdk.dotnet/issues/31).
