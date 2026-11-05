using System;
using System.Collections.Generic;
using System.IO;

namespace destination_opendal_parquet.OpenDal
{
    internal static class ObjectKey
    {
        public static string NormalizePrefix(string value)
        {
            var text = value.Replace('\\', '/').Trim();
            var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
                if (segment is "." or "..")
                    throw new ArgumentException($"output_path '{value}' must not contain '.' or '..' segments.");
            return string.Join('/', segments);
        }

        public static string Combine(params string?[] parts)
        {
            var segments = new List<string>();
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                var text = part.Replace('\\', '/').Trim('/');
                if (text.Length == 0)
                    continue;

                foreach (var segment in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment is "." or "..")
                        throw new ArgumentException($"Object key '{part}' must not contain '.' or '..' segments.");
                    segments.Add(segment);
                }
            }

            return string.Join('/', segments);
        }

        public static string SanitizeSegment(string value, string fallback = "stream")
        {
            var result = Uri.EscapeDataString(value).Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(result) || result is "." or ".." ? fallback : result;
        }
    }
}
