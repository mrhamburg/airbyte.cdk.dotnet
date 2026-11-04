using System;
using System.Collections.Generic;
using System.Text.Json;
using OpenDAL;

namespace destination_opendal_parquet.OpenDal
{
    internal sealed class OpenDalConfig
    {
        private static readonly HashSet<string> ReservedEnvironmentVariables = new(StringComparer.Ordinal)
        {
            "OPENDAL_SETTINGS",
            "OPENDAL_SERVICE",
            "OPENDAL_SCHEME",
            "OPENDAL_OPTIONS"
        };

        private OpenDalConfig(string service, IReadOnlyDictionary<string, string> options)
        {
            Service = service;
            Options = options;
        }

        public string Service { get; }

        public IReadOnlyDictionary<string, string> Options { get; }

        public Operator CreateOperator() =>
            new(Service, new Dictionary<string, string>(Options, StringComparer.Ordinal));

        public static OpenDalConfig Resolve(JsonElement config, JsonElement? settings,
            IReadOnlyDictionary<string, string> environment)
        {
            var inlineService = ConfigJson.GetString(config, "service") ?? ConfigJson.GetString(config, "scheme");
            var environmentService = GetEnvironment(environment, "OPENDAL_SERVICE")
                                     ?? GetEnvironment(environment, "OPENDAL_SCHEME");
            var fileService = ConfigJson.GetString(settings, "service") ?? ConfigJson.GetString(settings, "scheme");
            var service = inlineService ?? environmentService ?? fileService ?? "fs";
            if (string.IsNullOrWhiteSpace(service))
                throw new ArgumentException("Config property 'service' must not be empty.");

            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            ApplyOptions(options, settings, "options");
            ApplyEnvironmentOptions(options, environment, service);
            ApplyOptions(options, config, "options");

            if (string.Equals(service, "fs", StringComparison.OrdinalIgnoreCase) && !options.ContainsKey("root"))
                options["root"] = "/";

            return new OpenDalConfig(service, options);
        }

        private static void ApplyEnvironmentOptions(Dictionary<string, string> options,
            IReadOnlyDictionary<string, string> environment, string service)
        {
            var json = GetEnvironment(environment, "OPENDAL_OPTIONS");
            if (!string.IsNullOrWhiteSpace(json))
            {
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(json);
                    root = document.RootElement.Clone();
                }
                catch (JsonException e)
                {
                    throw new ArgumentException(
                        $"Environment variable 'OPENDAL_OPTIONS' is not valid JSON: {e.Message}", e);
                }

                if (root.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Environment variable 'OPENDAL_OPTIONS' must contain a JSON object.");
                ApplyOptionsElement(options, root);
            }

            foreach (var (name, value) in environment)
            {
                if (ReservedEnvironmentVariables.Contains(name))
                    continue;
                if (name.StartsWith("OPENDAL_OPTION_", StringComparison.Ordinal))
                    options[NormalizeEnvironmentKey(name["OPENDAL_OPTION_".Length..])] = value;
            }

            var prefix = $"OPENDAL_{service.ToUpperInvariant()}_";
            foreach (var (name, value) in environment)
            {
                if (ReservedEnvironmentVariables.Contains(name))
                    continue;
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                    options[NormalizeEnvironmentKey(name[prefix.Length..])] = value;
            }
        }

        private static string NormalizeEnvironmentKey(string value) => value.ToLowerInvariant();

        private static void ApplyOptions(Dictionary<string, string> options, JsonElement? element, string name)
        {
            var value = ConfigJson.GetObject(element, name);
            if (value is not null)
                ApplyOptionsElement(options, value.Value);
        }

        private static void ApplyOptionsElement(Dictionary<string, string> options, JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Config property 'options' must be an object.");

            foreach (var property in element.EnumerateObject())
            {
                var value = OptionValue(property.Value, property.Name);
                if (value is null)
                    options.Remove(property.Name);
                else
                    options[property.Name] = value;
            }
        }

        private static string? OptionValue(JsonElement value, string name) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => throw new ArgumentException(
                $"Config property 'options.{name}' must be a string, number, boolean, or null.")
        };

        private static string? GetEnvironment(IReadOnlyDictionary<string, string> environment, string name) =>
            environment.TryGetValue(name, out var value) ? value : null;
    }
}
