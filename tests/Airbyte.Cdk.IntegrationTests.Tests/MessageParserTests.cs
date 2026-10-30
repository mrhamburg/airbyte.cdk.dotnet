#nullable enable
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class MessageParserTests
    {
        [Fact]
        public void Test_TryParse_AcceptsJsonObject()
        {
            MessageParser.TryParse("{\"type\":\"RECORD\",\"record\":{\"stream\":\"s\"}}", out var message)
                .ShouldBeTrue();

            message.GetProperty("type").GetString().ShouldBe("RECORD");
        }

        [Fact]
        public void Test_TryParse_RejectsBlankLine()
        {
            MessageParser.TryParse("   ", out _).ShouldBeFalse();
        }

        [Fact]
        public void Test_TryParse_RejectsNonJsonOutput()
        {
            MessageParser.TryParse("MSBuild version 18.0.0 for .NET", out _).ShouldBeFalse();
        }

        [Fact]
        public void Test_TryParse_RejectsJsonThatIsNotAnObject()
        {
            MessageParser.TryParse("[1,2,3]", out _).ShouldBeFalse();
        }

        [Fact]
        public void Test_TypeOf_ReadsMessageType()
        {
            var message = JsonDocument.Parse("{\"type\":\"TRACE\"}").RootElement.Clone();

            MessageParser.TypeOf(message).ShouldBe("TRACE");
        }

        [Fact]
        public void Test_TraceIsError_DetectsErrorTraces()
        {
            var error = JsonDocument.Parse("{\"type\":\"TRACE\",\"trace\":{\"type\":\"ERROR\",\"error\":{\"message\":\"boom\"}}}")
                .RootElement.Clone();
            var log = JsonDocument.Parse("{\"type\":\"LOG\",\"log\":{\"level\":\"ERROR\",\"message\":\"x\"}}")
                .RootElement.Clone();

            MessageParser.IsErrorTrace(error).ShouldBeTrue();
            MessageParser.IsErrorTrace(log).ShouldBeFalse();
        }
    }
}