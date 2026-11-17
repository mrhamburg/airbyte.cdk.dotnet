#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Utils;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Resolves config, catalog, and state files relative to the implementation binary and the
    /// current directory.
    /// </summary>
    internal static class InputFileResolver
    {
        public static bool TryGetPath(string? filename, string? implPath, out string filepath)
        {
            filepath = string.Empty;
            if (string.IsNullOrWhiteSpace(filename))
                return false;

            foreach (var path in new[]
                     {
                         Path.Join(Path.GetDirectoryName(implPath), filename), filename,
                         Path.Join(Directory.GetCurrentDirectory(), filename)
                     })
                if (File.Exists(path))
                {
                    filepath = path;
                    break;
                }

            return !string.IsNullOrWhiteSpace(filepath);
        }

        public static JsonElement GetConfig(Options options, ConnectorSpecification spec, AirbyteLogger logger)
        {
            var contents = string.IsNullOrWhiteSpace(options.Config)
                ? throw new InvalidOperationException("Config is undefined!")
                : !TryGetPath(options.Config, AirbyteEntrypoint.AirbyteImplPath, out var filepath)
                    ? throw new FileNotFoundException("Could not find config file: " + options.Config, options.Config)
                    : File.ReadAllText(filepath);

            if (string.IsNullOrWhiteSpace(contents))
                throw new InvalidOperationException("Config file is empty!");

            var toreturn = contents.AsJsonElement();

            if (!ResourceSchemaLoader.TryCheckConfigAgainstSpecOrExit(toreturn, spec, out var exc))
                throw new InvalidOperationException($"Config does not match spec schema: {exc?.Message ?? "unknown error"}");

            logger.Info($"Found config file at location: {filepath}");
            return toreturn;
        }

        public static ConfiguredAirbyteCatalog GetCatalog(Options options, AirbyteLogger logger)
        {
            if (!TryGetPath(options.Catalog, AirbyteEntrypoint.AirbyteImplPath, out var filepath))
                throw new FileNotFoundException("Cannot find catalog file: " + options.Catalog, options.Catalog);

            logger.Info($"Found catalog at location: {filepath}");
            return AirbyteProtocolReader.ReadCatalog(filepath);
        }

        public static JsonElement GetState(Source source, ReadOptions readOptions, AirbyteLogger logger)
        {
            if (TryGetPath(readOptions.State, AirbyteEntrypoint.AirbyteImplPath, out var filepath))
                logger.Info($"Found state file at location: {filepath}");
            else
                logger.Warn(
                    $"Could not find state file, config reported state file location: {readOptions.State}");

            var contents = source.ReadState(filepath);
            logger.Debug($"State file contents: {contents.GetRawText()}");
            return contents;
        }
    }
}
