#nullable enable
using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class StrictSchemaTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private const string Envelope = @"
{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""properties"": {
    ""amount"": { ""type"": ""number"" },
    ""base"": { ""type"": ""string"" },
    ""date"": { ""type"": ""string"" },
    ""rates"": {
      ""type"": ""object"",
      ""additionalProperties"": { ""type"": [""number"", ""null""] }
    }
  },
  ""required"": [""amount"", ""base"", ""date"", ""rates""]
}";

        [Fact]
        public void Test_Validate_PassesOnValidRecord()
        {
            var schema = StrictSchema.Build(Json(Envelope));

            RecordValidator.Validate(schema, Json(@"{""amount"":1.0,""base"":""USD"",""date"":""2026-10-29"",""rates"":{""EUR"":0.9}}"))
                .ShouldBeEmpty();
        }

        [Fact]
        public void Test_Validate_FailsOnUndeclaredTopLevelProperty()
        {
            var schema = StrictSchema.Build(Json(Envelope));

            RecordValidator.Validate(schema, Json(@"{""amount"":1.0,""base"":""USD"",""date"":""2026-10-29"",""rates"":{},""surprise"":true}"))
                .ShouldNotBeEmpty();
        }

        [Fact]
        public void Test_Validate_PassesOnNewKeyInsideTypedMap()
        {
            var schema = StrictSchema.Build(Json(Envelope));

            RecordValidator.Validate(schema, Json(@"{""amount"":1.0,""base"":""USD"",""date"":""2026-10-29"",""rates"":{""ZZZ"":1.5}}"))
                .ShouldBeEmpty();
        }

        [Fact]
        public void Test_Validate_FailsOnWrongType()
        {
            var schema = StrictSchema.Build(Json(Envelope));

            RecordValidator.Validate(schema, Json(@"{""amount"":""one"",""base"":""USD"",""date"":""2026-10-29"",""rates"":{}}"))
                .ShouldNotBeEmpty();
        }

        [Fact]
        public void Test_Validate_FailsOnUndeclaredNestedProperty()
        {
            var schema = StrictSchema.Build(Json(@"
{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""properties"": {
    ""rows"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": { ""id"": { ""type"": ""string"" } }
      }
    }
  }
}"));

            RecordValidator.Validate(schema, Json(@"{""rows"":[{""id"":""a"",""extra"":1}]}")).ShouldNotBeEmpty();
            RecordValidator.Validate(schema, Json(@"{""rows"":[{""id"":""a""}]}")).ShouldBeEmpty();
        }

        [Fact]
        public void Test_Build_LeavesPatternPropertiesPermissive()
        {
            var schema = StrictSchema.Build(Json(@"
{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""properties"": { ""x"": { ""type"": ""string"" } },
  ""patternProperties"": { ""^p_"": { ""type"": ""number"" } }
}"));

            RecordValidator.Validate(schema, Json(@"{""x"":""a"",""p_1"":2,""other"":3}")).ShouldBeEmpty();
        }

        [Fact]
        public void Test_Validate_PassesOnAnyOfAlternative()
        {
            var schema = StrictSchema.Build(Json(@"
{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""properties"": { ""value"": { ""anyOf"": [ { ""type"": ""string"" }, { ""type"": ""number"" } ] } }
}"));

            RecordValidator.Validate(schema, Json(@"{""value"":""abc""}")).ShouldBeEmpty();
            RecordValidator.Validate(schema, Json(@"{""value"":1}")).ShouldBeEmpty();
        }

        [Fact]
        public void Test_Validate_PassesOnOneOfAlternative()
        {
            var schema = StrictSchema.Build(Json(@"
{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""properties"": { ""value"": { ""oneOf"": [ { ""type"": ""string"" }, { ""type"": ""number"" } ] } }
}"));

            RecordValidator.Validate(schema, Json(@"{""value"":""abc""}")).ShouldBeEmpty();
        }

        [Fact]
        public void Test_Validate_PassesOnAllOfWithRef()
        {
            var schema = StrictSchema.Build(Json(@"
{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""definitions"": {
    ""base"": { ""type"": ""object"", ""properties"": { ""id"": { ""type"": ""string"" } } }
  },
  ""allOf"": [
    { ""$ref"": ""#/definitions/base"" },
    { ""properties"": { ""extra"": { ""type"": ""string"" } } }
  ]
}"));

            RecordValidator.Validate(schema, Json(@"{""id"":""1"",""extra"":""e""}")).ShouldBeEmpty();
        }

        [Fact]
        public void Test_Validate_FailsOnWrongTypeInsideNestedObject()
        {
            var schema = StrictSchema.Build(Json(@"
{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""properties"": {
    ""meta"": { ""type"": ""object"", ""properties"": { ""count"": { ""type"": ""integer"" } } }
  }
}"));

            RecordValidator.Validate(schema, Json(@"{""meta"":{""count"":""three""}}")).ShouldNotBeEmpty();
        }

        [Fact]
        public void Test_Build_RejectsUnsupportedDraft()
        {
            var error = Should.Throw<ConnectorSpecException>(() => StrictSchema.Build(Json(@"
{
  ""$schema"": ""http://json-schema.org/draft-04/schema#"",
  ""type"": ""object""
}")));

            error.Message.ShouldContain("draft-04");
        }

        [Fact]
        public void Test_Validate_ReportsInstanceLocation()
        {
            var schema = StrictSchema.Build(Json(Envelope));

            var errors = RecordValidator.Validate(schema,
                Json(@"{""amount"":""one"",""base"":""USD"",""date"":""2026-10-29"",""rates"":{}}"));

            errors.ShouldContain(e => e.Contains("/amount"));
        }
    }
}