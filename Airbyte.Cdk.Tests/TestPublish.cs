using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Airbyte.Cdk.Tests
{
    public class TestPublish
    {
        /// <summary>
        /// Tests to see if we can get the semver version
        /// </summary>
        [Fact(Skip = "local only")]
        public void TestGetSemver()
        {
            string connectorpath = Path.Join(Publish.MoveToUpperPath(Assembly.GetExecutingAssembly().Location, 5, true), 
                "airbyte-integrations", "connectors", "source-exchange-rates-free");
            var found = Publish.GetSemver(connectorpath);

            found.Should().NotBeEmpty();
        }
        
        [Fact(Skip = "local only")]
        public async Task TestGetImageAlreadyExists()
        {
            string imagetest = "airbytedotnet/source-exchange-rates-free:nonexistent";
            var found = await Publish.ImageAlreadyExists(imagetest);

            found.Should().BeFalse();
        }
    }
}