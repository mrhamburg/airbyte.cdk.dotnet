#nullable enable
using System;

namespace Airbyte.Cdk.Models
{
    /// <summary>
    /// Identifies a stream by name and optional namespace. State, partition, and transport code
    /// share this key so the same stream cannot be keyed two different ways.
    /// </summary>
    public readonly record struct StreamKey(string Name, string? Namespace)
    {
        public static StreamKey Empty => new(string.Empty, null);

        public static StreamKey From(StreamDescriptor descriptor) =>
            new(descriptor.Name, descriptor.Namespace);

        public override string ToString() => $"{Namespace}\u0000{Name}";
    }
}
