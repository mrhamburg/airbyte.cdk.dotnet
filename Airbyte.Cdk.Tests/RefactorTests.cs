#nullable enable
using System;
using System.Text.Json;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Json.Schema;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.Tests
{
    public class RefactorTests
    {
        [Fact]
        public void Test_StreamKey_EqualityAndString()
        {
            var key = new StreamKey("users", "public");

            key.ShouldBe(new StreamKey("users", "public"));
            key.ShouldNotBe(new StreamKey("users", "private"));
            key.ToString().ShouldBe("public\u0000users");
        }

        [Fact]
        public void Test_GenericStream_PathFallsBackToBase()
        {
            var stream = new FluentStreamBuilder()
                .GetJsonSchema(() => JsonSchema.FromText("{}"))
                .Create("MyStream");

            stream.Path("{}".AsJsonElement()).ShouldBe("my_stream");
        }

        [Fact]
        public void Test_GenericStream_NextPageTokenFallsBackToEmpty()
        {
            var stream = new FluentStreamBuilder().Create("users");

            stream.NextPageToken(null!, null!).ShouldBeEmpty();
        }

        [Fact]
        public void Test_GenericStream_UrlBaseMissing_Throws()
        {
            var stream = new FluentStreamBuilder().Create("users");

            Should.Throw<InvalidOperationException>(() => stream.UrlBase());
        }
    }
}
