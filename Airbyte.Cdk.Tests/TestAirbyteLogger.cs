using System;
using System.Collections.Generic;
using System.Text.Json;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Shouldly;
using Xunit;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests
{
    public class TestAirbyteLogger
    {
        [Fact]
        public void Test_Exception_IncludesStackTrace()
        {
            var messages = new List<AirbyteMessage>();
            var logger = new AirbyteLogger(messages.Add);
            Exception caught;
            try
            {
                throw new Exception("boom");
            }
            catch (Exception e)
            {
                caught = e;
            }

            logger.Exception(caught);

            messages.Count.ShouldBe(1);
            messages[0].Type.ShouldBe(MessageType.LOG);
            messages[0].Log.Level.ShouldBe(Level.ERROR);
            messages[0].Log.Message.ShouldBe("boom");
            messages[0].Log.StackTrace.ShouldNotBeNullOrWhiteSpace();
        }

        [Fact]
        public void Test_TraceError_SerializesFailureTypeAndStream()
        {
            var messages = new List<AirbyteMessage>();
            var logger = new AirbyteLogger(messages.Add);

            logger.TraceError("Your API key is invalid", "401", "at ...", AirbyteFailureType.config_error,
                new StreamDescriptor { Name = "users", Namespace = "public" });

            messages.Count.ShouldBe(1);
            messages[0].Type.ShouldBe(MessageType.TRACE);
            messages[0].Trace.Type.ShouldBe(AirbyteTraceType.ERROR);
            messages[0].Trace.Error.Message.ShouldBe("Your API key is invalid");
            messages[0].Trace.Error.InternalMessage.ShouldBe("401");
            messages[0].Trace.Error.StackTrace.ShouldBe("at ...");
            messages[0].Trace.Error.FailureType.ShouldBe(AirbyteFailureType.config_error);
            messages[0].Trace.Error.StreamDescriptor.Name.ShouldBe("users");
        }

        [Fact]
        public void Test_TraceEstimate_Serializes()
        {
            var messages = new List<AirbyteMessage>();
            var logger = new AirbyteLogger(messages.Add);

            logger.TraceEstimate(AirbyteEstimateType.STREAM, "users", "public", 100, 2048);

            messages.Count.ShouldBe(1);
            messages[0].Trace.Type.ShouldBe(AirbyteTraceType.ESTIMATE);
            messages[0].Trace.Estimate.Type.ShouldBe(AirbyteEstimateType.STREAM);
            messages[0].Trace.Estimate.Name.ShouldBe("users");
            messages[0].Trace.Estimate.Namespace.ShouldBe("public");
            messages[0].Trace.Estimate.RowEstimate.ShouldBe(100);
            messages[0].Trace.Estimate.ByteEstimate.ShouldBe(2048);
        }

        [Fact]
        public void Test_TraceStreamStatus_Serializes()
        {
            var messages = new List<AirbyteMessage>();
            var logger = new AirbyteLogger(messages.Add);

            logger.TraceStreamStatus(new StreamDescriptor { Name = "users" }, AirbyteStreamStatus.RUNNING);

            messages.Count.ShouldBe(1);
            messages[0].Trace.Type.ShouldBe(AirbyteTraceType.STREAM_STATUS);
            messages[0].Trace.StreamStatus.Status.ShouldBe(AirbyteStreamStatus.RUNNING);
            messages[0].Trace.StreamStatus.StreamDescriptor.Name.ShouldBe("users");
        }

        [Fact]
        public void Test_TraceAnalytics_Serializes()
        {
            var messages = new List<AirbyteMessage>();
            var logger = new AirbyteLogger(messages.Add);

            logger.TraceAnalytics("sql-source-used-cursor-count-indexed", "2");

            messages.Count.ShouldBe(1);
            messages[0].Trace.Type.ShouldBe(AirbyteTraceType.ANALYTICS);
            messages[0].Trace.Analytics.Type.ShouldBe("sql-source-used-cursor-count-indexed");
            messages[0].Trace.Analytics.Value.ShouldBe("2");
        }

        [Fact]
        public void Test_ControlConnectorConfig_Serializes()
        {
            var messages = new List<AirbyteMessage>();
            var logger = new AirbyteLogger(messages.Add);

            logger.ControlConnectorConfig("{\"api_key\":\"new\"}".AsJsonElement());

            messages.Count.ShouldBe(1);
            messages[0].Type.ShouldBe(MessageType.CONTROL);
            messages[0].Control.Type.ShouldBe(AirbyteControlType.CONNECTOR_CONFIG);
            messages[0].Control.ConnectorConfig.Config.Value.GetProperty("api_key").GetString().ShouldBe("new");
        }
    }
}
