using System;
using System.Text.Json;
using Parquet;

namespace destination_opendal_parquet.OpenDal
{
    internal sealed class ParquetSettings
    {
        private ParquetSettings(CompressionMethod? compression, int? rowGroupSize)
        {
            Compression = compression;
            RowGroupSize = rowGroupSize;
        }

        public CompressionMethod? Compression { get; }

        public int? RowGroupSize { get; }

        public static ParquetSettings Resolve(JsonElement config, JsonElement? settings)
        {
            var inline = ConfigJson.GetObject(config, "parquet");
            var file = ConfigJson.GetObject(settings, "parquet");

            var compression = ParseCompression(
                ConfigJson.GetString(inline, "compression") ?? ConfigJson.GetString(file, "compression"));

            var rowGroupSize = ConfigJson.GetInt(inline, "row_group_size")
                               ?? ConfigJson.GetInt(file, "row_group_size");
            if (rowGroupSize is < 1)
                throw new ArgumentException("Config property 'parquet.row_group_size' must be a positive integer.");

            return new ParquetSettings(compression, rowGroupSize);
        }

        public ParquetOptions? ToParquetOptions()
        {
            if (Compression is null && RowGroupSize is null)
                return null;

            var defaults = new ParquetOptions();
            return new ParquetOptions
            {
                CompressionMethod = Compression ?? defaults.CompressionMethod,
                RowGroupSize = RowGroupSize ?? defaults.RowGroupSize
            };
        }

        private static CompressionMethod? ParseCompression(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return value.Trim().ToLowerInvariant() switch
            {
                "none" => CompressionMethod.None,
                "snappy" => CompressionMethod.Snappy,
                "gzip" => CompressionMethod.Gzip,
                "lzo" => CompressionMethod.Lzo,
                "brotli" => CompressionMethod.Brotli,
                "lz4" => CompressionMethod.LZ4,
                "lz4_raw" => CompressionMethod.Lz4Raw,
                "zstd" => CompressionMethod.Zstd,
                _ => throw new ArgumentException(
                    $"Config property 'parquet.compression' has unsupported value '{value}'.")
            };
        }
    }
}
