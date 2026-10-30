using System;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.Tests
{
    [CollectionDefinition("Environment", DisableParallelization = true)]
    public class EnvironmentCollection
    {
    }

    [Collection("Environment")]
    public class TestConnector
    {
        [Fact]
        public void TestReadConfig()
        {
            var testfile = "{\"bogus\": \"file\"}";
            string filepath = "TestReadConfig";
            if (File.Exists(filepath))
                File.Delete(filepath);
            File.WriteAllText(filepath, testfile);

            var result = Connector.ReadConfig(filepath);

            result.GetRawText().ShouldBe(testfile, "output should match input");
        }

        [Fact]
        public void TestWriteConfig()
        {
            var testfile = "{\"bogus\": \"file\"}";
            string filepath = "TestWriteConfig";
            
            Connector.WriteConfig(testfile.AsJsonElement(), filepath);

            var result = File.ReadAllText(filepath);

            result.ShouldBe(testfile, "output should match input");
        }

        [Fact]
        public void TestSpec_DefaultsProtocolVersion()
        {
            var directory = Path.Combine(Path.GetTempPath(), "airbyte-cdk-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var implementation = Path.Combine(directory, "implementation.dll");
            File.WriteAllText(implementation, string.Empty);
            File.WriteAllText(Path.Combine(directory, "spec.json"),
                "{\"connectionSpecification\":{\"type\":\"object\"}}");

            var original = Environment.GetEnvironmentVariable("AIRBYTE_IMPL_PATH");
            try
            {
                Environment.SetEnvironmentVariable("AIRBYTE_IMPL_PATH", implementation);

                var spec = new TestConnectorImplementation().Spec();

                spec.ProtocolVersion.ShouldBe(AirbyteProtocolVersion.Latest);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AIRBYTE_IMPL_PATH", original);
                Directory.Delete(directory, true);
            }
        }

        private class TestConnectorImplementation : Connector
        {
            public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config) =>
                new() { Status = Status.SUCCEEDED };
        }
    }
}
