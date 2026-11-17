using System.Text.Json;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Sources.Utils
{
    public static class CatalogHelper
    {
        /// <summary>
        /// Updates the sync mode on all streams in this catalog to be full refresh
        /// </summary>
        public static AirbyteCatalog CoerceAirbyteCatalogAsFullRefresh(AirbyteCatalog catalog)
        {
            var json = JsonSerializer.Serialize(catalog, AirbyteProtocolJson.Options);
            var coercedCatalog = JsonSerializer.Deserialize<AirbyteCatalog>(json, AirbyteProtocolJson.Options)
                                 ?? throw new JsonException("Could not copy the catalog.");
            foreach (var stream in coercedCatalog.Streams)
            {
                stream.SourceDefinedCursor = false;
                stream.SupportedSyncModes = new[] { SyncMode.full_refresh };
                stream.DefaultCursorField = null;
                stream.IsResumable = false;
            }

            return coercedCatalog;
        }
    }
}
