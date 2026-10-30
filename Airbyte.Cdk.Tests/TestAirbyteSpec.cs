using System.IO;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.Tests
{
    public class TestAirbyteSpec
    {
        private const string ValidSpec = "{\"documentationUrl\":\"https://google.com\",\"connectionSpecification\":{\"type\":\"object\",\"required\":[\"api_token\"],\"additionalProperties\":false,\"properties\":{\"api_token\":{\"type\":\"string\"}},},}";

        [Fact]
        public void TestFromFile()
        {
            string filename = "test.json";
            File.WriteAllText(filename, ValidSpec);
            var result = AirbyteSpec.FromFile(filename);

            result.SpecString.ShouldBe(ValidSpec);
        }

        [Fact]
        public void TestFromFileNonexistent() =>
            Should.Throw<DirectoryNotFoundException>(() => AirbyteSpec.FromFile("/asdaaaa/i do not exist"));
    }
}
