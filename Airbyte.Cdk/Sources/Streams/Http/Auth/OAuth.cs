using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Airbyte.Cdk.Sources.Utils;
using Flurl.Http;

namespace Airbyte.Cdk.Sources.Streams.Http.Auth
{
    public class OAuth : AuthBase
    {

        public string TokenRefreshEndpoint { get; }

        public string ClientId { get; }

        public string ClientSecret { get; }

        public string RefreshToken { get; }

        public string ExpiresInName { get; }

        public string AccessTokenName { get; }

        private string AccessToken { get; set; }

        private readonly object _tokenLock = new();
        private readonly SemaphoreSlim _tokenLockAsync = new(1, 1);

        public DateTime? TokenExpiry { get; private set; }

        public IEnumerable<string> Scopes { get; }

        public OAuth(string tokenrefreshendpoint, string clientid, string clientsecret, string refreshtoken,
            IEnumerable<string> scopes = null, DateTime? tokenexpiry = null, string accesstokenname = "",
            string expiresinname = "")
        {
            TokenRefreshEndpoint = tokenrefreshendpoint;
            ClientId = clientid;
            ClientSecret = clientsecret;
            RefreshToken = refreshtoken;
            Scopes = scopes ?? new List<string>();
            TokenExpiry = tokenexpiry;
            AccessTokenName = accesstokenname;
            ExpiresInName = expiresinname;
        }

        public string GetAccessToken()
        {
            lock (_tokenLock)
            {
                if (TokenHasExpired())
                    (AccessToken, TokenExpiry) = RefreshAccessToken();

                return AccessToken;
            }
        }

        public override void Apply(IFlurlRequest request) =>
            request.WithOAuthBearerToken(GetAccessToken());

        public override async Task ApplyAsync(IFlurlRequest request) =>
            request.WithOAuthBearerToken(await GetAccessTokenAsync());

        public virtual async Task<string> GetAccessTokenAsync()
        {
            await _tokenLockAsync.WaitAsync();
            try
            {
                if (TokenHasExpired())
                    (AccessToken, TokenExpiry) = await RefreshAccessTokenAsync();

                return AccessToken;
            }
            finally
            {
                _tokenLockAsync.Release();
            }
        }

        public bool TokenHasExpired() => !TokenExpiry.HasValue || DateTime.UtcNow > TokenExpiry;

        /// <summary>
        /// Override to define additional parameters
        /// </summary>
        /// <returns></returns>
        public virtual string GetRefreshRequestBody()
        {
            Dictionary<string, object> payload = new()
            {
                { "grant_type", "refresh_token" },
                { "client_id", ClientId },
                { "client_secret", ClientSecret },
                { "refresh_token", RefreshToken }
            };

            if (Scopes.Any())
                payload["scope"] = Scopes;

            return payload.AsJsonElement().GetRawText();
        }

        public virtual (string, DateTime) RefreshAccessToken() =>
            RefreshAccessTokenAsync().GetAwaiter().GetResult();

        public virtual async Task<(string, DateTime)> RefreshAccessTokenAsync()
        {
            try
            {
                var response = await TokenRefreshEndpoint.PostJsonAsync(GetRefreshRequestBody());
                var json = await response.GetJsonAsync<JsonElement>();
                var token = json.GetProperty(AccessTokenName).GetString()
                            ?? throw new InvalidOperationException("The token response did not contain an access token.");
                return (token, ExpiryFromResponse(json, ExpiresInName, DateTime.UtcNow));
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Error while refreshing access token: {e.Message}", e);
            }
        }

        /// <summary>
        /// Reads an OAuth <c>expires_in</c> value. The value is a duration in seconds, not a date.
        /// </summary>
        public static DateTime ExpiryFromResponse(JsonElement json, string expiresInName, DateTime now)
        {
            if (!json.TryGetProperty(expiresInName, out var value))
                throw new FormatException($"The token response did not contain '{expiresInName}'.");

            int seconds;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numericSeconds))
                seconds = numericSeconds;
            else if (value.ValueKind == JsonValueKind.String &&
                     int.TryParse(value.GetString(), out var stringSeconds))
                seconds = stringSeconds;
            else
                throw new FormatException($"The value of '{expiresInName}' is not a number of seconds.");

            return now.AddSeconds(seconds);
        }
    }

    public static class OauthFlurlRequestExtension
    {
        public static IFlurlRequest WithOauth(this IFlurlRequest request, OAuth auth) =>
            request.WithOAuthBearerToken(auth.GetAccessToken());
    }
}
