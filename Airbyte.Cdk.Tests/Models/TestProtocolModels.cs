using System.Text.Json;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Shouldly;
using Xunit;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests.Models
{
    public class TestProtocolModels
    {
        [Fact]
        public void Test_ProtocolSerializer_UsesProtocolEnumNames()
        {
            var json = JsonSerializer.Serialize(new
            {
                sync = SyncMode.full_refresh,
                state = AirbyteStateType.STREAM,
                auth = AuthType.oauth2_0
            }, AirbyteProtocolJson.Options);

            using var document = JsonDocument.Parse(json);
            document.RootElement.GetProperty("sync").GetString().ShouldBe("full_refresh");
            document.RootElement.GetProperty("state").GetString().ShouldBe("STREAM");
            document.RootElement.GetProperty("auth").GetString().ShouldBe("oauth2.0");
        }

        [Fact]
        public void Test_AuthType_DeserializesOAuth2DotZero()
        {
            var auth = JsonSerializer.Deserialize<AuthSpecification>(
                "{\"auth_type\":\"oauth2.0\"}", AirbyteProtocolJson.Options);

            auth.AuthType.ShouldBe(AuthType.oauth2_0);
        }

        [Fact]
        public void Test_StreamState_SerializesDescriptorAndStats()
        {
            var message = new AirbyteStateMessage
            {
                Type = AirbyteStateType.STREAM,
                Stream = new AirbyteStreamState
                {
                    StreamDescriptor = new StreamDescriptor { Name = "historical", Namespace = "public" },
                    StreamState = "{\"date\":\"2021-05-18\"}".AsJsonElement()
                },
                SourceStats = new AirbyteStateStats { RecordCount = 12 }
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            root.GetProperty("type").GetString().ShouldBe("STREAM");
            root.GetProperty("stream").GetProperty("stream_descriptor").GetProperty("name").GetString()
                .ShouldBe("historical");
            root.GetProperty("stream").GetProperty("stream_descriptor").GetProperty("namespace").GetString()
                .ShouldBe("public");
            root.GetProperty("stream").GetProperty("stream_state").GetProperty("date").GetString()
                .ShouldBe("2021-05-18");
            root.GetProperty("sourceStats").GetProperty("recordCount").GetDouble().ShouldBe(12);
            root.TryGetProperty("data", out _).ShouldBeFalse();
        }

        [Fact]
        public void Test_LegacyState_StillSerializesAsData()
        {
            var message = new AirbyteStateMessage
            {
                Data = "{\"date\":\"2021-05-18\"}".AsJsonElement()
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            root.TryGetProperty("type", out _).ShouldBeFalse();
            root.GetProperty("data").GetProperty("date").GetString().ShouldBe("2021-05-18");
        }

        [Fact]
        public void Test_StateMessage_AcceptsStateTypeAlias()
        {
            const string json =
                "{\"state_type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\"},\"stream_state\":{}}}";

            var message = JsonSerializer.Deserialize<AirbyteStateMessage>(json, AirbyteProtocolJson.Options);

            message.ShouldNotBeNull();
            message.Type.ShouldBe(AirbyteStateType.STREAM);
            message.Stream.StreamDescriptor.Name.ShouldBe("users");
        }

        [Fact]
        public void Test_TraceMessage_SerializesError()
        {
            var message = new AirbyteMessage
            {
                Type = MessageType.TRACE,
                Trace = new AirbyteTraceMessage
                {
                    Type = AirbyteTraceType.ERROR,
                    EmittedAt = 1234,
                    Error = new AirbyteErrorTraceMessage
                    {
                        Message = "Your API key is invalid",
                        InternalMessage = "401",
                        StackTrace = "at ...",
                        FailureType = AirbyteFailureType.config_error
                    }
                }
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);
            var trace = document.RootElement.GetProperty("trace");

            document.RootElement.GetProperty("type").GetString().ShouldBe("TRACE");
            trace.GetProperty("type").GetString().ShouldBe("ERROR");
            trace.GetProperty("error").GetProperty("message").GetString().ShouldBe("Your API key is invalid");
            trace.GetProperty("error").GetProperty("failure_type").GetString().ShouldBe("config_error");
        }

        [Fact]
        public void Test_StreamStatusTraceMessage_Serializes()
        {
            var message = new AirbyteTraceMessage
            {
                Type = AirbyteTraceType.STREAM_STATUS,
                EmittedAt = 1234,
                StreamStatus = new AirbyteStreamStatusTraceMessage
                {
                    StreamDescriptor = new StreamDescriptor { Name = "users" },
                    Status = AirbyteStreamStatus.COMPLETE
                }
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            root.GetProperty("type").GetString().ShouldBe("STREAM_STATUS");
            root.GetProperty("stream_status").GetProperty("status").GetString().ShouldBe("COMPLETE");
            root.GetProperty("stream_status").GetProperty("stream_descriptor").GetProperty("name").GetString()
                .ShouldBe("users");
        }

        [Fact]
        public void Test_ControlMessage_SerializesConnectorConfig()
        {
            var message = new AirbyteMessage
            {
                Type = MessageType.CONTROL,
                Control = new AirbyteControlMessage
                {
                    Type = AirbyteControlType.CONNECTOR_CONFIG,
                    EmittedAt = 1234,
                    ConnectorConfig = new AirbyteControlConnectorConfigMessage
                    {
                        Config = "{\"api_key\":\"new\"}".AsJsonElement()
                    }
                }
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);
            var control = document.RootElement.GetProperty("control");

            document.RootElement.GetProperty("type").GetString().ShouldBe("CONTROL");
            control.GetProperty("type").GetString().ShouldBe("CONNECTOR_CONFIG");
            control.GetProperty("connectorConfig").GetProperty("config").GetProperty("api_key").GetString()
                .ShouldBe("new");
        }

        [Fact]
        public void Test_LogMessage_SerializesStackTrace()
        {
            var message = new AirbyteLogMessage
            {
                Level = Level.ERROR,
                Message = "failed",
                StackTrace = "at Test()"
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);

            document.RootElement.GetProperty("stack_trace").GetString().ShouldBe("at Test()");
        }

        [Fact]
        public void Test_RecordMessage_SerializesMetaAndFileReference()
        {
            var message = new AirbyteRecordMessage
            {
                Stream = "users",
                Data = "{\"id\":1}".AsJsonElement(),
                EmittedAt = 1,
                Meta = new AirbyteRecordMessageMeta
                {
                    Changes = new[]
                    {
                        new AirbyteRecordMessageMetaChange
                        {
                            Field = "name",
                            Change = AirbyteRecordMessageMetaChangeChange.NULLED,
                            Reason = AirbyteRecordMessageMetaChangeReason.SOURCE_FIELD_SIZE_LIMITATION
                        }
                    }
                },
                FileReference = new AirbyteRecordMessageFileReference
                {
                    StagingFileUrl = "s3://bucket/key",
                    SourceFileRelativePath = "path/file.json",
                    FileSizeBytes = 10
                }
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            root.GetProperty("meta").GetProperty("changes")[0].GetProperty("reason").GetString()
                .ShouldBe("SOURCE_FIELD_SIZE_LIMITATION");
            root.GetProperty("file_reference").GetProperty("staging_file_url").GetString()
                .ShouldBe("s3://bucket/key");
        }

        [Fact]
        public void Test_StreamAndConfiguredStream_SerializeNewFields()
        {
            var stream = new AirbyteStream
            {
                Name = "users",
                JsonSchema = "{}".AsJsonElement(),
                SupportedSyncModes = new[] { SyncMode.full_refresh },
                IsResumable = true,
                IsFileBased = false
            };

            var configured = new ConfiguredAirbyteStream
            {
                Stream = stream,
                SyncMode = SyncMode.full_refresh,
                DestinationSyncMode = DestinationSyncMode.update,
                DestinationObjectName = "users",
                GenerationId = 1,
                MinimumGenerationId = 0,
                SyncId = 2,
                IncludeFiles = true
            };

            var streamJson = JsonSerializer.Serialize(stream, AirbyteProtocolJson.Options);
            using var streamDocument = JsonDocument.Parse(streamJson);
            streamDocument.RootElement.GetProperty("is_resumable").GetBoolean().ShouldBeTrue();
            streamDocument.RootElement.GetProperty("is_file_based").GetBoolean().ShouldBeFalse();

            var configuredJson = JsonSerializer.Serialize(configured, AirbyteProtocolJson.Options);
            using var configuredDocument = JsonDocument.Parse(configuredJson);
            var root = configuredDocument.RootElement;
            root.GetProperty("destination_sync_mode").GetString().ShouldBe("update");
            root.GetProperty("destination_object_name").GetString().ShouldBe("users");
            root.GetProperty("generation_id").GetInt32().ShouldBe(1);
            root.GetProperty("minimum_generation_id").GetInt32().ShouldBe(0);
            root.GetProperty("sync_id").GetInt32().ShouldBe(2);
            root.GetProperty("include_files").GetBoolean().ShouldBeTrue();
        }

        [Fact]
        public void Test_DestinationCatalog_Serializes()
        {
            var message = new AirbyteMessage
            {
                Type = MessageType.DESTINATION_CATALOG,
                DestinationCatalog = new DestinationCatalog
                {
                    Operations = new[]
                    {
                        new DestinationOperation
                        {
                            ObjectName = "users",
                            SyncMode = DestinationSyncMode.soft_delete,
                            JsonSchema = "{}".AsJsonElement(),
                            MatchingKeys = new[] { new[] { "id" } }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);
            var operation = document.RootElement.GetProperty("destination_catalog").GetProperty("operations")[0];

            document.RootElement.GetProperty("type").GetString().ShouldBe("DESTINATION_CATALOG");
            operation.GetProperty("object_name").GetString().ShouldBe("users");
            operation.GetProperty("sync_mode").GetString().ShouldBe("soft_delete");
        }

        [Fact]
        public void Test_ProtocolVersion_IsLatest()
        {
            AirbyteProtocolVersion.Latest.ShouldBe("0.19.0");
        }
    }
}
