using System.Threading.Tasks;
using Flurl.Http;

namespace Airbyte.Cdk.Sources.Streams.Http.Auth
{
    /// <summary>
    /// Applies an authentication scheme to an outgoing request. Implementations extend this type
    /// instead of adding another branch to the request builder.
    /// </summary>
    public abstract class AuthBase
    {
        public abstract void Apply(IFlurlRequest request);

        public virtual Task ApplyAsync(IFlurlRequest request)
        {
            Apply(request);
            return Task.CompletedTask;
        }
    }
}
