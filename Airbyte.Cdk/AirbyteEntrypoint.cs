using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Utils;
using Airbyte.Cdk.Transport;
using CommandLine;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk
{
    public class AirbyteEntrypoint
    {
        static Connector Connector { get; set; }

        static Options Options { get; set; }

        static AirbyteLogger Logger { get; } = new();

        public static string AirbyteImplPath
        {
            get => Environment.GetEnvironmentVariable("AIRBYTE_IMPL_PATH");
        }

        public static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            Parser.Default.ParseArguments<ReadOptions, WriteOptions, SyncOptions, InitOptions, PublishOptions>(args)
                .WithParsed<ReadOptions>(options => Options = options)
                .WithParsed<WriteOptions>(options => Options = options)
                .WithParsed<SyncOptions>(options => Options = options)
                .WithParsed<InitOptions>(options => Options = options)
                .WithParsed<PublishOptions>(options => Options = options)
                .WithNotParsed(_ => Environment.Exit(1));

            //Check if this is an init
            if (Options is InitOptions)
            {
                await InitCli.Process();
                return;
            }
            if (Options is PublishOptions publishOptions)
            {
                await Publish.Process(publishOptions);
                return;
            }

            string implModule = Environment.GetEnvironmentVariable("AIRBYTE_IMPL_MODULE");
            Connector = LoadConnector(AirbyteImplPath, implModule ?? string.Empty, "Source");
            try
            {
                await Launch();
            }
            catch (Exception e)
            {
                if (!e.Data.Contains(AirbyteLogger.TraceEmittedDataKey))
                    Logger.TraceError("The connector failed", $"{e.GetType().Name}: {e.Message}",
                        e.StackTrace, AirbyteFailureType.system_error);
                Logger.Fatal($"Exception ({e.GetType()}): {e.Message}");
                Environment.ExitCode = 1;
            }
        }

        private static readonly Dictionary<string, Assembly> LoadedAssemblies = new(StringComparer.Ordinal);

        private static Connector LoadConnector(string implPath, string implModule, string role)
        {
            if (!File.Exists(implPath))
                throw new FileNotFoundException($"Cannot find {role.ToLowerInvariant()} implementation binary {implPath}");

            Assembly assembly;
            lock (LoadedAssemblies)
            {
                if (!LoadedAssemblies.TryGetValue(implPath, out assembly!))
                {
                    assembly = Assembly.LoadFile(implPath);
                    LoadedAssemblies[implPath] = assembly;
                }
            }

            var implementation = assembly.GetTypes()
                .Where(x => typeof(Connector).IsAssignableFrom(x) && !x.IsAbstract && !x.IsInterface)
                .FirstOrDefault(x => x.Name.Equals(implModule, StringComparison.OrdinalIgnoreCase));

            if (implementation == null)
                throw new Exception($"{role} implementation not found in {implPath}!");
            if (Activator.CreateInstance(implementation) is not Connector instance)
                throw new Exception($"Implementation provided for {role.ToLowerInvariant()} does not implement Connector class!");

            instance.ImplPath = implPath;
            return instance;
        }

        private static Source GetSource()
        {
            if (Connector is not Source source)
                throw new Exception($"Could not instantiate Source as current type ({Connector.GetType().Name}) is not implementing {nameof(Source)}");
            return source;
        }

        private static Destination GetDestination()
        {
            if (Connector is not Destination destination)
                throw new Exception($"Could not instantiate Destination as current type ({Connector.GetType().Name}) is not implementing {nameof(Destination)}");
            return destination;
        }

        private static Destination GetDestination(string implPath, string implModule)
        {
            var connector = LoadConnector(implPath, implModule, "Destination");
            if (connector is not Destination destination)
                throw new Exception($"Destination implementation {implModule} does not implement {nameof(Destination)}");
            return destination;
        }

        private static async Task Launch()
        {
            var spec = Connector.Spec();
            var dataChannel = DataChannelSettings.FromEnvironment();

            switch (Options.Command.ToLowerInvariant())
            {
                case "spec":
                    ToConsole(new AirbyteMessage
                    {
                        Type = Type.SPEC,
                        Spec = spec
                    });
                    break;
                case "check":
                    var result = Connector.Check(Logger, GetConfig(spec));
                    if (result.Status == Status.SUCCEEDED) Logger.Info("Check succeeded");
                    else Logger.Error("Check failed");
                    ToConsole(new AirbyteMessage
                    {
                        Type = Type.CONNECTION_STATUS,
                        ConnectionStatus = result
                    });
                    break;
                case "discover":
                    ToConsole(new AirbyteMessage
                    {
                        Type = Type.CATALOG,
                        Catalog = GetSource().Discover(Logger, GetConfig(spec))
                    });
                    break;
                case "read":
                    if (dataChannel.IsSocket)
                    {
                        var socketReadOptions = Options as ReadOptions;
                        var source = GetSource();
                        source.PartitionTracker = new PartitionTracker(true);

                        await using var sink = RoutingDataChannelWriter.Create(dataChannel, Console.Out);
                        var writer = new SinkChannelWriter(sink);
                        var sourceLogger = new AirbyteLogger(message => writer.TryWrite(message));
                        try
                        {
                            await source.Read(sourceLogger, writer, GetConfig(spec), GetCatalog(),
                                GetState(source, socketReadOptions));
                        }
                        finally
                        {
                            await sink.CompleteAsync();
                        }

                        break;
                    }

                    Channel<AirbyteMessage> readerChannel = Channel.CreateUnbounded<AirbyteMessage>();
                    var readOptions = Options as ReadOptions;
                    var readtasks = new[]
                    {
                        Task.Run(async () =>
                        {
                            var writer = readerChannel.Writer;
                            try
                            {
                                var source = GetSource();
                                var state = GetState(source, readOptions);
                                var config = GetConfig(spec);
                                var catalog = GetCatalog();

                                // Keep source logs/traces ordered with records and state.
                                var sourceLogger = new AirbyteLogger(message => writer.TryWrite(message));
                                await source.Read(sourceLogger, writer, config, catalog, state);
                            }
                            catch (Exception e)
                            {
                                Logger.Exception(e);
                                if (!e.Data.Contains(AirbyteLogger.TraceEmittedDataKey))
                                    Logger.TraceError("The connector failed",
                                        $"{e.GetType().Name}: {e.Message}", e.StackTrace,
                                        AirbyteFailureType.system_error);
                                Logger.Fatal("Could not process data due to exception: " + e.Message);
                                Environment.ExitCode = 1;
                            }
                            finally
                            {
                                writer.Complete();
                            }
                        }),
                        Task.Run(async () =>
                        {
                            await foreach (var msg in readerChannel.Reader.ReadAllAsync())
                                ToConsole(msg);
                        })
                    };

                    Task.WaitAll(readtasks);
                    break;
                case "write":
                {
                    var destination = GetDestination();
                    var config = GetConfig(spec);
                    var catalog = GetCatalog();
                    if (dataChannel.IsSocket)
                    {
                        var reader = new CompositeDataChannelReader(
                            new StdioDataChannelReader(Console.In, Logger.Warn),
                            new SocketDataChannelReader(dataChannel.SocketPaths, catalog));
                        await RunWriteLoop(reader, Console.Out, Logger, destination, config, catalog);
                        break;
                    }

                    await RunWriteLoop(Console.In, Console.Out, Logger, destination, config, catalog);
                    break;
                }
                case "sync":
                {
                    var syncOptions = (SyncOptions)Options;
                    var source = GetSource();
                    var destinationPath = string.IsNullOrWhiteSpace(syncOptions.DestinationImplPath)
                        ? AirbyteImplPath
                        : syncOptions.DestinationImplPath;
                    var destination = GetDestination(destinationPath, syncOptions.DestinationImplModule);
                    var destinationSpec = destination.Spec();
                    var sourceConfig = GetConfig(spec);
                    var destinationConfigPath = string.IsNullOrWhiteSpace(syncOptions.DestinationConfig)
                        ? Options.Config
                        : syncOptions.DestinationConfig;
                    var destinationConfig = GetConfig(destinationConfigPath, destinationSpec, destinationPath,
                        "Destination config");

                    await InMemoryDataChannel.RunAsync(source, destination, Logger, sourceConfig, destinationConfig,
                        GetCatalog(), GetState(source, syncOptions.State), syncOptions.ChannelCapacity, Console.Out);
                    break;
                }
                default:
                    throw new NotImplementedException($"Unexpected command: {Options.Command}");
            }
        }

        /// <summary>
        /// Consumes destination input line by line and returns state messages to the platform in the order received.
        /// Processing is deliberately sequential so a destination failure cannot leave a producer blocked on stdin.
        /// </summary>
        public static async Task RunWriteLoop(TextReader input, TextWriter output, AirbyteLogger logger,
            Destination destination, JsonElement config, ConfiguredAirbyteCatalog catalog) =>
            await RunWriteLoop(new StdioDataChannelReader(input, logger.Warn), output, logger, destination, config, catalog);

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
            await reader.CompleteAsync();
        }

        private static JsonElement GetConfig(ConnectorSpecification spec) =>
            GetConfig(Options.Config, spec, AirbyteImplPath, "Config");

        private static JsonElement GetConfig(string configPath, ConnectorSpecification spec, string implPath, string label)
        {
            if (string.IsNullOrWhiteSpace(configPath))
                throw new Exception($"{label} is undefined!");
            if (!TryGetPath(configPath, implPath, out var filepath))
                throw new FileNotFoundException($"Could not find {label.ToLowerInvariant()} file: {configPath}");

            var contents = File.ReadAllText(filepath);
            if (string.IsNullOrWhiteSpace(contents))
                throw new Exception($"{label} file is empty!");

            var toreturn = contents.AsJsonElement();
            if (!ResourceSchemaLoader.TryCheckConfigAgainstSpecOrExit(toreturn, spec, out var exc))
                throw new Exception($"{label} does not match spec schema: {exc.Message}");

            Logger.Info($"Found {label.ToLowerInvariant()} file at location: {filepath}");
            return toreturn;
        }

        private static ConfiguredAirbyteCatalog GetCatalog()
        {
            if (!TryGetPath(Options.Catalog, out var filepath))
                throw new FileNotFoundException("Cannot find catalog file: " + Options.Catalog);

            Logger.Info($"Found catalog at location: {filepath}");
            return AirbyteProtocolReader.ReadCatalog(filepath);
        }

        private static JsonElement GetState(Source source, ReadOptions readoptions) =>
            GetState(source, readoptions.State);

        private static JsonElement GetState(Source source, string statePath)
        {
            if (TryGetPath(statePath, out var filepath))
                Logger.Info($"Found state file at location: {filepath}");
            else
                Logger.Warn($"Could not find state file, config reported state file location: {statePath}");

            var contents = source.ReadState(filepath);
            Logger.Debug($"State file contents: {contents.GetRawText()}");
            return contents;
        }

        public static bool TryGetPath(string filename, out string filepath) =>
            TryGetPath(filename, AirbyteImplPath, out filepath);

        public static bool TryGetPath(string filename, string implPath, out string filepath)
        {
            filepath = string.Empty;
            if (string.IsNullOrWhiteSpace(filename))
                return false;

            var implDirectory = Path.GetDirectoryName(implPath) ?? string.Empty;
            foreach (var path in new[] { Path.Join(implDirectory, filename), filename, Path.Join(Directory.GetCurrentDirectory(), filename) })
                if (File.Exists(path))
                {
                    filepath = path;
                    break;
                }

            return !string.IsNullOrWhiteSpace(filepath);
        }

        public static void ToConsole<T>(T item) where T : AirbyteMessage => ToConsole(item, Console.Out);

        public static void ToConsole<T>(T item, TextWriter writer) where T : AirbyteMessage =>
            writer.WriteLine(JsonSerializer.Serialize(item, AirbyteProtocolJson.Options));

    }
}


