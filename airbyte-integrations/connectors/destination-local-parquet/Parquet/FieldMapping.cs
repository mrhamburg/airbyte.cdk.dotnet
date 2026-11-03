using System;
using System.Collections.Generic;
using Parquet.Schema;

namespace destination_local_parquet.Parquet
{
    internal enum FieldShape { Scalar, Struct, Map, List }

    internal enum RootShape { Struct, Map, Value }

    internal sealed class FieldMapping
    {
        public required string Name { get; init; }
        public required Field Field { get; init; }
        public required FieldShape Shape { get; init; }
        public bool IsRequired { get; init; }
        public IReadOnlyList<FieldMapping> Properties { get; init; } = Array.Empty<FieldMapping>();
        public FieldMapping? Value { get; init; }
    }
}
