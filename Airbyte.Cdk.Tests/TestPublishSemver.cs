using System.IO;
using Airbyte.Cdk;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.Tests
{
    public class TestPublishSemver
    {
        [Fact]
        public void Test_GetSemver_ReadsVersionHeadingsOnly()
        {
            var directory = Path.Combine(Path.GetTempPath(), "airbyte-semver-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var changelog = Path.Combine(directory, "CHANGELOG.md");
                File.WriteAllText(changelog, "# Changelog\n\n" +
                                             "## 0.0.1\nInitial release, using dotnet CDK\n\n" +
                                             "## 0.0.3\nUpdated to Airbyte.Cdk 0.0.8 and Flurl 4.0.2.\n\n" +
                                             "## 0.0.4\nAdded integration tests.\n");

                Publish.GetSemver(directory).ShouldBe("0.0.4");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void Test_GetSemver_IgnoresVersionLikeProse()
        {
            var directory = Path.Combine(Path.GetTempPath(), "airbyte-semver-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var changelog = Path.Combine(directory, "CHANGELOG.md");
                File.WriteAllText(changelog, "# Changelog\n\n" +
                                             "## 0.0.3\nUpdated to Airbyte.Cdk 0.0.8.\n");

                Publish.GetSemver(directory).ShouldBe("0.0.3");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
