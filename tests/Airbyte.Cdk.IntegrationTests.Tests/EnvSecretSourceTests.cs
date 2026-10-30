#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class EnvSecretSourceTests : IDisposable
    {
        private const string Key = "AIRBYTE_INT_TEST_SECRET";

        public void Dispose() => Environment.SetEnvironmentVariable(Key, null);

        [Fact]
        public async Task Test_GetAsync_ReturnsEnvironmentValue()
        {
            Environment.SetEnvironmentVariable(Key, "s3cr3t");

            var value = await new EnvSecretSource().GetAsync(Key, CancellationToken.None);

            value.ShouldBe("s3cr3t");
        }

        [Fact]
        public async Task Test_GetAsync_ReturnsNull_WhenMissing()
        {
            Environment.SetEnvironmentVariable(Key, null);

            (await new EnvSecretSource().GetAsync(Key, CancellationToken.None)).ShouldBeNull();
        }

        [Fact]
        public void Test_Create_ReturnsEnvSource()
        {
            SecretSourceFactory.Create().ShouldBeOfType<EnvSecretSource>();
        }
    }
}