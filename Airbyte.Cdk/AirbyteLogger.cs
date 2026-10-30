#nullable enable
using System;
using System.Text.Json;
using Airbyte.Cdk.Models;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk
{
    public class AirbyteLogger
    {
        private readonly Action<AirbyteMessage> _sink;

        public AirbyteLogger() : this(message => AirbyteEntrypoint.ToConsole(message))
        {
        }

        public AirbyteLogger(Action<AirbyteMessage> sink) => _sink = sink;

        public void Log(Level level, string message, string? stackTrace = null)
        {
            var logrecord = new AirbyteLogMessage {Level = level, Message = message, StackTrace = stackTrace};
            var logmessage = new AirbyteMessage {Type = Type.LOG, Log = logrecord};
            _sink(logmessage);
        }

        public void Fatal(string message) => Log(Level.FATAL, message);

        public void Exception(Exception exception) =>
            Log(Level.ERROR, exception.Message, exception.StackTrace);

        public void Error(string message) => Log(Level.ERROR, message);

        public void Warn(string message) => Log(Level.WARN, message);

        public void Info(string message) => Log(Level.INFO, message);

        public void Debug(string message) => Log(Level.DEBUG, message);

        public void Trace(string message) => Log(Level.TRACE, message);

        public void TraceMessage(AirbyteTraceMessage message) =>
            _sink(new AirbyteMessage {Type = Type.TRACE, Trace = message});

        public void TraceError(string message, string? internalMessage = null, string? stackTrace = null,
            AirbyteFailureType failureType = AirbyteFailureType.system_error,
            StreamDescriptor? streamDescriptor = null) =>
            TraceMessage(new AirbyteTraceMessage
            {
                Type = AirbyteTraceType.ERROR,
                EmittedAt = Now(),
                Error = new AirbyteErrorTraceMessage
                {
                    Message = message,
                    InternalMessage = internalMessage,
                    StackTrace = stackTrace,
                    FailureType = failureType,
                    StreamDescriptor = streamDescriptor
                }
            });

        public void TraceEstimate(AirbyteEstimateType type, string name, string? ns = null,
            long? rowEstimate = null, long? byteEstimate = null) =>
            TraceMessage(new AirbyteTraceMessage
            {
                Type = AirbyteTraceType.ESTIMATE,
                EmittedAt = Now(),
                Estimate = new AirbyteEstimateTraceMessage
                {
                    Type = type,
                    Name = name,
                    Namespace = ns,
                    RowEstimate = rowEstimate,
                    ByteEstimate = byteEstimate
                }
            });

        public void TraceStreamStatus(StreamDescriptor descriptor, AirbyteStreamStatus status,
            AirbyteStreamStatusReason[]? reasons = null) =>
            TraceMessage(new AirbyteTraceMessage
            {
                Type = AirbyteTraceType.STREAM_STATUS,
                EmittedAt = Now(),
                StreamStatus = new AirbyteStreamStatusTraceMessage
                {
                    StreamDescriptor = descriptor,
                    Status = status,
                    Reasons = reasons
                }
            });

        public void TraceAnalytics(string type, string? value = null) =>
            TraceMessage(new AirbyteTraceMessage
            {
                Type = AirbyteTraceType.ANALYTICS,
                EmittedAt = Now(),
                Analytics = new AirbyteAnalyticsTraceMessage {Type = type, Value = value}
            });

        public void ControlConnectorConfig(JsonElement config) =>
            _sink(new AirbyteMessage
            {
                Type = Type.CONTROL,
                Control = new AirbyteControlMessage
                {
                    Type = AirbyteControlType.CONNECTOR_CONFIG,
                    EmittedAt = Now(),
                    ConnectorConfig = new AirbyteControlConnectorConfigMessage {Config = config}
                }
            });

        private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
