#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Airbyte.Cdk.Transport
{
    public enum DataChannelMedium
    {
        Stdio,
        Socket
    }

    public enum DataChannelFormat
    {
        Jsonl,
        Protobuf
    }

    /// <summary>
    /// Transport configuration supplied by the Airbyte platform. STDIO/JSONL is the default, so
    /// connectors that do not use socket mode keep the original behavior.
    /// </summary>
    public sealed class DataChannelSettings
    {
        public const string MediumVariable = "DATA_CHANNEL_MEDIUM";
        public const string FormatVariable = "DATA_CHANNEL_FORMAT";
        public const string SocketPathsVariable = "DATA_CHANNEL_SOCKET_PATHS";

        public DataChannelSettings(DataChannelMedium medium, DataChannelFormat format,
            IReadOnlyList<string>? socketPaths = null)
        {
            Medium = medium;
            Format = format;
            SocketPaths = socketPaths ?? Array.Empty<string>();

            if (Medium == DataChannelMedium.Socket && Format != DataChannelFormat.Protobuf)
                throw new ArgumentException("Socket mode requires DATA_CHANNEL_FORMAT=PROTOBUF");
            if (Medium == DataChannelMedium.Socket && SocketPaths.Count == 0)
                throw new ArgumentException("Socket mode requires at least one DATA_CHANNEL_SOCKET_PATHS entry");
        }

        public DataChannelMedium Medium { get; }
        public DataChannelFormat Format { get; }
        public IReadOnlyList<string> SocketPaths { get; }

        public bool IsSocket => Medium == DataChannelMedium.Socket;

        public static DataChannelSettings FromEnvironment() => Parse(
            Environment.GetEnvironmentVariable(MediumVariable),
            Environment.GetEnvironmentVariable(FormatVariable),
            Environment.GetEnvironmentVariable(SocketPathsVariable));

        public static DataChannelSettings Parse(string? medium, string? format, string? socketPaths)
        {
            var parsedMedium = ParseMedium(medium);
            var parsedFormat = ParseFormat(format);
            var paths = string.IsNullOrWhiteSpace(socketPaths)
                ? Array.Empty<string>()
                : socketPaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            try
            {
                return new DataChannelSettings(parsedMedium, parsedFormat, paths);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException($"Invalid data channel configuration: {e.Message}", e);
            }
        }

        private static DataChannelMedium ParseMedium(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DataChannelMedium.Stdio;

            return value.Trim().ToUpperInvariant() switch
            {
                "STDIO" => DataChannelMedium.Stdio,
                "SOCKET" => DataChannelMedium.Socket,
                _ => throw new InvalidOperationException($"Unsupported {MediumVariable}: {value}")
            };
        }

        private static DataChannelFormat ParseFormat(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DataChannelFormat.Jsonl;

            return value.Trim().ToUpperInvariant() switch
            {
                "JSONL" => DataChannelFormat.Jsonl,
                "PROTOBUF" => DataChannelFormat.Protobuf,
                _ => throw new InvalidOperationException($"Unsupported {FormatVariable}: {value}")
            };
        }
    }
}
