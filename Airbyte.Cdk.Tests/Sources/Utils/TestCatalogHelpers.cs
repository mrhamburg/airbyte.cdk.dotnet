using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.Tests.Sources.Utils
{
    public class TestCatalogHelpers
    {
        [Fact]
        public void Test_CoerceAirbyteCatalogAsFullRefresh_ClearsResumability()
        {
            var catalog = new AirbyteCatalog
            {
                Streams = new[]
                {
                    new AirbyteStream
                    {
                        Name = "users",
                        SupportedSyncModes = new[] { SyncMode.full_refresh, SyncMode.incremental },
                        SourceDefinedCursor = true,
                        DefaultCursorField = new[] { "updated_at" },
                        IsResumable = true
                    }
                }
            };

            var coerced = CatalogHelper.CoerceAirbyteCatalogAsFullRefresh(catalog);

            coerced.Streams[0].SupportedSyncModes.ShouldBe(new[] { SyncMode.full_refresh });
            coerced.Streams[0].SourceDefinedCursor.ShouldBe(false);
            coerced.Streams[0].DefaultCursorField.ShouldBeNull();
            coerced.Streams[0].IsResumable.ShouldBe(false);
        }
    }
}
