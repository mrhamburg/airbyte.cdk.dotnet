using Airbyte.Cdk.Sources.Streams.Http.Auth;
using Shouldly;
using Flurl;
using Flurl.Http;
using Moq;
using Xunit;

namespace Airbyte.Cdk.Tests.Sources.Streams.Http.Auth
{
    public class TestBasicAuth
    {
        [Fact]
        public void TestTokenAuthentication()
        {
            var tokens = new[] {"test-token"};
            var system = new BasicAuth(tokens);
            var request = new Mock<FlurlRequest>(() => new FlurlRequest(Url.Parse("airbyte.io")));
            request.CallBase = true;

            var header = system.GetAuthHeader(request.Object);

            header.Headers.Count.ShouldBe(1);
            header.Headers[0].Name.ShouldBe("Authorization");
            header.Headers[0].Value.ShouldBe("Bearer test-token");
        }

        [Fact]
        public void TestMultipleTokenAuthentication()
        {
            var tokens = new[] { "token1", "token2" };
            var system = new BasicAuth(tokens);
            var request = new Mock<FlurlRequest>(() => new FlurlRequest(Url.Parse("airbyte.io")));
            request.CallBase = true;

            var header = system.GetAuthHeader(request.Object);

            header.Headers.Count.ShouldBe(1);
            header.Headers[0].Name.ShouldBe("Authorization");
            header.Headers[0].Value.ShouldBe("Bearer token1 token2");
        }
    }
}
