namespace Airbyte.Cdk.Models
{
    public enum DestinationSyncMode
    {
        append,
        overwrite,
        append_dedup,
        update,
        soft_delete
    }
}
