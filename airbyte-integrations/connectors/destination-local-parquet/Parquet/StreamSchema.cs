using System;
using System.Collections.Generic;
using Parquet.Schema;

namespace destination_local_parquet.Parquet
{
    internal sealed class StreamSchema
    {
        public required ParquetSchema Parquet { get; init; }
        public required RootShape Shape { get; init; }
        public IReadOnlyList<FieldMapping> Properties { get; init; } = Array.Empty<FieldMapping>();
        public IReadOnlyDictionary<string, FieldMapping> ByName { get; init; } =
            new Dictionary<string, FieldMapping>(StringComparer.Ordinal);
        public FieldMapping? Value { get; init; }

        public static StreamSchema Fallback()
        {
            var field = new DataField("data", typeof(string), isNullable: true);
            return new StreamSchema
            {
                Parquet = new ParquetSchema(field),
                Shape = RootShape.Value,
                Value = new FieldMapping { Name = "data", Field = field, Shape = FieldShape.Scalar }
            };
        }
    }
}
