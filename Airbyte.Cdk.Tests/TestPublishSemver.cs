using System.IO;
using Airbyte.Cdk;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.Tests
{
    public class TestPublishSemver
    {
        private static string WithChangelog(string contents)
        {
            var directory = Path.Combine(Path.GetTempPath(), "airbyte-semver-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "CHANGELOG.md"), contents);
            return directory;
        }

        [Fact]
        public void Test_GetSemver_ReadsVersionHeadingsOnly()
        {
            var directory = WithChangelog("# Changelog\n\n" +
                                          "## 0.0.1\nInitial release, using dotnet CDK\n\n" +
                                          "## 0.0.3\nUpdated to Airbyte.Cdk 0.0.8 and Flurl 4.0.2.\n\n" +
                                          "## 0.0.4\nAdded integration tests.\n");

            try
            {
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
            var directory = WithChangelog("# Changelog\n\n" +
                                          "## 0.0.3\nUpdated to Airbyte.Cdk 0.0.8.\n");

            try
            {
                Publish.GetSemver(directory).ShouldBe("0.0.3");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Theory]
        [InlineData("# 0.0.5\nSomething changed.\n")]
        [InlineData("## [0.0.5] - 2026-10-30\nSomething changed.\n")]
        [InlineData("## 0.0.5 (2026-10-30)\nSomething changed.\n")]
        [InlineData("## v0.0.5\nSomething changed.\n")]
        public void Test_GetSemver_AcceptsCommonHeadingFormats(string heading)
        {
            var directory = WithChangelog("# Changelog\n\n" + heading);

            try
            {
                Publish.GetSemver(directory).ShouldBe("0.0.5");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void Test_GetSemver_ReturnsEmpty_WhenNoHeadings()
        {
            var directory = WithChangelog("# Changelog\n\nNo versions here.\n");

            try
            {
                Publish.GetSemver(directory).ShouldBeEmpty();
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}