using System;
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

            Parser.Default.ParseArguments<ReadOptions, WriteOptions, InitOptions, PublishOptions>(args)
                .WithParsed<ReadOptions>(options => Options = options)
                .WithParsed<WriteOptions>(options => Options = options)
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

            if (!File.Exists(AirbyteImplPath))
                throw new FileNotFoundException($"Cannot find implementation binary {AirbyteImplPath}");

            var implementation = Assembly.LoadFile(AirbyteImplPath).GetTypes().Where(x => typeof(Connector).IsAssignableFrom(x) && !x.IsAbstract && !x.IsInterface)
                .FirstOrDefault(x => x.Name.Equals(implModule, StringComparison.OrdinalIgnoreCase));

            if (implementation == null)
                throw new Exception("Source implementation not found!");
            if (Activator.CreateInstance(implementation) is not Connector instance)
                throw new Exception("Implementation provided does not implement Connector class!");

            Connector = instance;
            try
            {
                await Launch();
            }
            catch (Exception e)
            {
                if (!e.Data.Contains("AirbyteTraceEmitted"))
                    Logger.TraceError("The connector failed", $"{e.GetType().Name}: {e.Message}",
                        e.StackTrace, AirbyteFailureType.system_error);
                Logger.Fatal($"Exception ({e.GetType()}): {e.Message}");
                Environment.ExitCode = 1;
            }
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

        private static async Task Launch()
        {
            var spec = Connector.Spec();

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
                                await source.Read(Logger, writer, config, catalog, state);
                            }
                            catch (Exception e)
                            {
                                Logger.Exception(e);
                                if (!e.Data.Contains("AirbyteTraceEmitted"))
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
                    await RunWriteLoop(Console.In, Console.Out, Logger, destination, config, catalog);
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
            Destination destination, JsonElement config, ConfiguredAirbyteCatalog catalog)
        {
            while (true)
            {
                var text = await input.ReadLineAsync();
                if (text == null)
                    break;

                if (!AirbyteProtocolReader.TryParseMessage(text, out var msg))
                {
                    logger.Warn("Dropping input that is not a valid AirbyteMessage");
                    continue;
                }

                await destination.Write(logger, config, catalog, msg!);

                if (msg!.Type == Type.STATE)
                    ToConsole(msg, output);
            }
        }

        private static JsonElement GetConfig(ConnectorSpecification spec)
        {
            var contents = string.IsNullOrWhiteSpace(Options.Config) ? throw new Exception("Config is undefined!")
                : !TryGetPath(Options.Config, out var filepath) ? throw new FileNotFoundException("Could not find config file: " + filepath)
                : File.ReadAllText(filepath);

            if (string.IsNullOrWhiteSpace(contents))
                throw new Exception("Config file is empty!");

            var toreturn = contents.AsJsonElement();

            if (!ResourceSchemaLoader.TryCheckConfigAgainstSpecOrExit(toreturn, spec, out var exc))
                throw new Exception($"Config does not match spec schema: {exc.Message}");

            Logger.Info($"Found config file at location: {filepath}");
            //Logger.Debug($"Config file contents: {toreturn.RootElement}");

            return toreturn;
        }

        private static ConfiguredAirbyteCatalog GetCatalog()
        {
            if (!TryGetPath(Options.Catalog, out var filepath))
                throw new FileNotFoundException("Cannot find catalog file: " + Options.Catalog);

            Logger.Info($"Found catalog at location: {filepath}");
            return AirbyteProtocolReader.ReadCatalog(filepath);
        }

        private static JsonElement GetState(Source source, ReadOptions readoptions)
        {
            if (TryGetPath(readoptions.State, out var filepath))
                Logger.Info($"Found state file at location: {filepath}");
            else
                Logger.Warn($"Could not find state file, config reported state file location: {readoptions.State}");

            var contents = source.ReadState(filepath);
            Logger.Debug($"State file contents: {contents.GetRawText()}");
            return contents;
        }

        public static bool TryGetPath(string filename, out string filepath)
        {
            filepath = string.Empty;
            if (string.IsNullOrWhiteSpace(filename))
                return false;
            foreach (var path in new[] { Path.Join(Path.GetDirectoryName(AirbyteImplPath), filename), filename, Path.Join(Directory.GetCurrentDirectory(), filename) })
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


