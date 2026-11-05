using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using destination_opendal_parquet.Parquet;
using Shouldly;
using Xunit;

namespace Destination.OpenDalParquet.Tests
{
    public class ValueConverterTests
    {
        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        [Fact]
        public void Test_Convert_RootMap_UsesIdealKeyAndValueTypes()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","additionalProperties":{"type":"string"}}
                """));

            var row = RecordValueConverter.Convert(schema, Json("""{"USD":"US Dollar","EUR":"Euro"}"""));

            var map = row["value"].ShouldBeAssignableTo<System.Collections.IDictionary>()!;
            map.Count.ShouldBe(2);
            map.Keys.Cast<object>().ShouldAllBe(x => x!.GetType() == typeof(ReadOnlyMemory<char>));
            row["value"].ShouldBeOfType<Dictionary<ReadOnlyMemory<char>, ReadOnlyMemory<char>?>>();
        }

        [Fact]
        public void Test_Convert_StructWithNullableDoubleMap()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{
                  "amount":{"type":"number"},
                  "base":{"type":"string"},
                  "rates":{"type":"object","additionalProperties":{"type":["number","null"]}}
                },"required":["amount","base","rates"]}
                """));

            var row = RecordValueConverter.Convert(schema, Json("""
                {"amount":1.5,"base":"USD","rates":{"EUR":0.9,"GBP":null}}
                """));

            row["amount"].ShouldBe(1.5d);
            row["base"].ShouldBeOfType<ReadOnlyMemory<char>>();
            ((System.Collections.IDictionary)row["rates"]!).Count.ShouldBe(2);
            row["rates"].ShouldBeOfType<Dictionary<ReadOnlyMemory<char>, double?>>();
        }

        [Fact]
        public void Test_Convert_ListOfStructs()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"items":{"type":"array","items":{
                  "type":"object","properties":{"name":{"type":"string"}}}}}}
                """));

            var row = RecordValueConverter.Convert(schema, Json("""{"items":[{"name":"a"},{"name":"b"}]}"""));

            row["items"].ShouldBeAssignableTo<System.Collections.IList>()!.Cast<object>().Count().ShouldBe(2);
        }

        [Fact]
        public void Test_Convert_MissingNullableFieldIsNull()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"a":{"type":"string"}}}
                """));

            RecordValueConverter.Convert(schema, Json("{}"))["a"].ShouldBeNull();
        }

        [Fact]
        public void Test_Convert_NullForRequiredFieldThrows()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}
                """));

            var error = Should.Throw<FormatException>(() =>
                RecordValueConverter.Convert(schema, Json("""{"a":null}""")));

            error.Message.ShouldContain("a");
        }

        [Fact]
        public void Test_Convert_IntegerOverflowNamesTheField()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"i":{"type":"integer"}},"required":["i"]}
                """));

            var error = Should.Throw<FormatException>(() =>
                RecordValueConverter.Convert(schema, Json("""{"i":1e20}""")));

            error.Message.ShouldContain("i");
        }

        [Fact]
        public void Test_Convert_MissingRequiredFieldThrows()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}
                """));

            var error = Should.Throw<FormatException>(() => RecordValueConverter.Convert(schema, Json("{}")));

            error.Message.ShouldContain("a");
        }

        [Fact]
        public void Test_Convert_TypeMismatchThrows()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"flag":{"type":"boolean"}}}
                """));

            Should.Throw<FormatException>(() =>
                RecordValueConverter.Convert(schema, Json("""{"flag":3}""")));
        }

        [Fact]
        public void Test_Convert_FallbackWritesRecordJsonIntoDataColumn()
        {
            var schema = StreamSchema.Fallback();

            var row = RecordValueConverter.Convert(schema, Json("""{"a":1}"""));

            row.Keys.ShouldBe(new[] { "data" });
            row["data"].ShouldBeOfType<ReadOnlyMemory<char>>().ToString().ShouldBe("""{"a":1}""");
        }

        [Fact]
        public void Test_Convert_NumberForStringFieldUsesInvariantText()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"x":{"type":"string"}}}
                """));

            var row = RecordValueConverter.Convert(schema, Json("""{"x":12}"""));

            row["x"].ShouldBeOfType<ReadOnlyMemory<char>>().ToString().ShouldBe("12");
        }

        [Fact]
        public void Test_Convert_ObjectForStringFieldUsesJsonText()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"x":{"type":"string"}}}
                """));

            var row = RecordValueConverter.Convert(schema, Json("""{"x":{"a":1}}"""));

            row["x"].ShouldBeOfType<ReadOnlyMemory<char>>().ToString().ShouldBe("""{"a":1}""");
        }

        [Fact]
        public void Test_Convert_ExtraKeyIsIgnoredWithWarning()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"a":{"type":"string"}}}
                """));
            var warnings = new List<string>();

            var row = RecordValueConverter.Convert(schema, Json("""{"a":"x","b":1}"""), warnings.Add);

            row.Keys.ShouldBe(new[] { "a" });
            warnings.ShouldContain(x => x.Contains("b"));
        }
    }
}
