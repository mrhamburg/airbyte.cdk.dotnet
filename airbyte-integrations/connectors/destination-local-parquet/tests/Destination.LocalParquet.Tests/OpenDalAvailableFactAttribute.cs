using System;
using OpenDAL;
using Xunit;

namespace Destination.LocalParquet.Tests
{
    internal sealed class OpenDalAvailableFactAttribute : FactAttribute
    {
        public OpenDalAvailableFactAttribute()
        {
            if (!OpenDalNative.IsAvailable)
                Skip = "Apache.OpenDAL native library is not available on this host (it requires glibc 2.39 or newer).";
        }
    }

    internal static class OpenDalNative
    {
        private static readonly Lazy<bool> Available = new(Check);

        public static bool IsAvailable => Available.Value;

        private static bool Check()
        {
            try
            {
                using var op = new Operator("memory");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
