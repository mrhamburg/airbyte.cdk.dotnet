#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Transport;
using CommandLine;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Process entry point. Parses the command line, loads the connector implementation, and hands
    /// the command to <see cref="CommandDispatcher"/>.
    /// </summary>
    public class AirbyteEntrypoint
    {
        public static string AirbyteImplPath =>
            Environment.GetEnvironmentVariable("AIRBYTE_IMPL_PATH") ?? string.Empty;

        public static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            Options? options = null;
            Parser.Default.ParseArguments<ReadOptions, WriteOptions, InitOptions, PublishOptions>(args)
                .WithParsed<ReadOptions>(value => options = value)
                .WithParsed<WriteOptions>(value => options = value)
                .WithParsed<InitOptions>(value => options = value)
                .WithParsed<PublishOptions>(value => options = value)
                .WithNotParsed(_ => Environment.Exit(1));

            if (options is InitOptions)
            {
                await InitCli.Process();
                return;
            }

            if (options is PublishOptions publishOptions)
            {
                await Publish.Process(publishOptions);
                return;
            }

            if (options is null)
                return;

            var logger = new AirbyteLogger();
            try
            {
                var connector = ConnectorLoader.Load(AirbyteImplPath,
                    Environment.GetEnvironmentVariable("AIRBYTE_IMPL_MODULE"));
                var context = new CliContext(options, connector, logger);
                await CommandDispatcher.LaunchAsync(context);
            }
            catch (Exception e)
            {
                if (!e.Data.Contains(AirbyteLogger.TraceEmittedDataKey))
                    logger.TraceError("The connector failed", $"{e.GetType().Name}: {e.Message}",
                        e.StackTrace, AirbyteFailureType.system_error);
                logger.Fatal($"Exception ({e.GetType()}): {e.Message}");
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// Consumes destination input line by line and returns state messages to the platform in the order received.
        /// Processing is deliberately sequential so a destination failure cannot leave a producer blocked on stdin.
        /// </summary>
        public static async Task RunWriteLoop(TextReader input, TextWriter output, AirbyteLogger logger,
            Destination destination, JsonElement config, ConfiguredAirbyteCatalog catalog) =>
            await RunWriteLoop(new StdioDataChannelReader(input, logger.Warn), output, logger, destination, config,
                catalog);

        /// <summary>
        /// Consumes the merged data channel. Socket mode supplies records from Unix domain sockets
        /// and control messages from stdin; STDIO mode supplies a single JSONL reader.
        /// </summary>
        public static async Task RunWriteLoop(IDataChannelReader reader, TextWriter output, AirbyteLogger logger,
            Destination destination, JsonElement config, ConfiguredAirbyteCatalog catalog)
        {
            var dispatcher = new DestinationMessageDispatcher(destination, logger, config, catalog, output);
            await foreach (var msg in reader.ReadAllAsync())
                await dispatcher.HandleAsync(msg);

            await dispatcher.CompleteAsync();
            await destination.CompleteAsync();
            await reader.CompleteAsync();
        }

        public static bool TryGetPath(string filename, out string filepath) =>
            InputFileResolver.TryGetPath(filename, AirbyteImplPath, out filepath);

        public static void ToConsole<T>(T item) where T : AirbyteMessage => ToConsole(item, Console.Out);

        public static void ToConsole<T>(T item, TextWriter writer) where T : AirbyteMessage =>
            writer.WriteLine(JsonSerializer.Serialize(item, AirbyteProtocolJson.Options));
    }
}
