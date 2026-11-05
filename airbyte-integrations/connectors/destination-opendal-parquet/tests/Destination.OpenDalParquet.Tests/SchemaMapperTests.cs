using System;
using System.Linq;
using System.Text.Json;
using destination_opendal_parquet.Parquet;
using Parquet.Schema;
using Shouldly;
using Xunit;

namespace Destination.OpenDalParquet.Tests
{
    public class SchemaMapperTests
    {
        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        private static FieldMapping Property(StreamSchema schema, string name) =>
            schema.Properties.Single(x => x.Name == name);

        [Fact]
        public void Test_FromJsonSchema_RootPropertiesBecomeTopLevelColumns()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}},"required":["a"]}
                """));

            schema.Shape.ShouldBe(RootShape.Struct);
            schema.Parquet.Fields.Select(x => x.Name).ShouldBe(new[] { "a", "b" });
            Property(schema, "a").IsRequired.ShouldBeTrue();
            Property(schema, "b").IsRequired.ShouldBeFalse();
        }

        [Fact]
        public void Test_FromJsonSchema_RootMapBecomesValueMapColumn()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","additionalProperties":{"type":"string"}}
                """));

            schema.Shape.ShouldBe(RootShape.Map);
            schema.Parquet.Fields.ShouldHaveSingleItem().Name.ShouldBe("value");
            schema.Value!.Shape.ShouldBe(FieldShape.Map);
        }

        [Fact]
        public void Test_FromJsonSchema_IntegerIsLongAndNumberIsDouble()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"i":{"type":"integer"},"n":{"type":"number"}}}
                """));

            ((DataField)Property(schema, "i").Field).ClrType.ShouldBe(typeof(long));
            ((DataField)Property(schema, "n").Field).ClrType.ShouldBe(typeof(double));
        }

        [Fact]
        public void Test_FromJsonSchema_NullUnionIsNullable()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"v":{"type":["number","null"]}},"required":["v"]}
                """));

            Property(schema, "v").Field.IsNullable.ShouldBeTrue();
        }

        [Fact]
        public void Test_FromJsonSchema_RequiredStringIsNonNullable()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"s":{"type":"string"}},"required":["s"]}
                """));

            Property(schema, "s").Field.IsNullable.ShouldBeFalse();
        }

        [Fact]
        public void Test_FromJsonSchema_ArrayBecomesListOfElement()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"items":{"type":"array","items":{"type":"integer"}}}}
                """));

            Property(schema, "items").Shape.ShouldBe(FieldShape.List);
            ((DataField)Property(schema, "items").Value!.Field).ClrType.ShouldBe(typeof(long));
        }

        [Fact]
        public void Test_FromJsonSchema_AdditionalPropertiesBecomesMap()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"rates":{"type":"object","additionalProperties":{"type":["number","null"]}}}}
                """));

            var rates = Property(schema, "rates");
            rates.Shape.ShouldBe(FieldShape.Map);
            ((DataField)rates.Value!.Field).ClrType.ShouldBe(typeof(double));
            rates.Value!.Field.IsNullable.ShouldBeTrue();
        }

        [Fact]
        public void Test_FromJsonSchema_PropertiesWinOverAdditionalProperties()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":{"type":"number"}}
                """));

            schema.Shape.ShouldBe(RootShape.Struct);
            schema.Parquet.Fields.ShouldHaveSingleItem().Name.ShouldBe("a");
        }

        [Fact]
        public void Test_FromJsonSchema_UnknownTypeFallsBackToString()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("""
                {"type":"object","properties":{"x":{"$ref":"#/nope"}}}
                """));

            ((DataField)Property(schema, "x").Field).ClrType.ShouldBe(typeof(ReadOnlyMemory<char>));
        }

        [Fact]
        public void Test_FromRecord_InfersStructFromFirstRecord()
        {
            var schema = ParquetSchemaMapper.FromRecord(Json("""{"a":"x","b":2,"c":true}"""));

            schema.Shape.ShouldBe(RootShape.Struct);
            schema.Properties.Select(x => x.Name).ShouldBe(new[] { "a", "b", "c" });
        }

        [Fact]
        public void Test_FromRecord_NumberInfersDouble()
        {
            var schema = ParquetSchemaMapper.FromRecord(Json("""{"b":2}"""));

            ((DataField)schema.Properties.Single(x => x.Name == "b").Field).ClrType.ShouldBe(typeof(double));
        }

        [Fact]
        public void Test_FromJsonSchema_EmptyFallsBackToDataColumn()
        {
            var schema = ParquetSchemaMapper.FromJsonSchema(Json("{}"));

            schema.Shape.ShouldBe(RootShape.Value);
            schema.Parquet.Fields.ShouldHaveSingleItem().Name.ShouldBe("data");
        }
    }
}
