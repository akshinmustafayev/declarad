using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Declarad.State.Backends
{
    /// <summary>
    /// Terraform-compatible HTTP backend: GET/POST the state at <c>address</c>; LOCK/UNLOCK requests carry the
    /// lock info and a locked state answers 423 or 409.
    /// </summary>
    public sealed class HttpBackend : IStateBackend
    {
        private readonly HttpClient _http;
        private readonly Uri _address;
        private readonly string _updateMethod;
        private readonly Uri? _lockAddress;
        private readonly Uri? _unlockAddress;
        private readonly string _lockMethod;
        private readonly string _unlockMethod;
        private string? _lockId;

        public HttpBackend(BackendConfig config, Func<string, string?> getEnv)
        {
            string Setting(string key, string env) => config.Get(key) ?? getEnv(env) ?? "";
            static Uri Url(string value, string what) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri
                : throw new DeclaradException($"The http backend's {what} '{value}' is not an http(s) URL.");
            _address = Url(config.Get("address") ?? getEnv("TF_HTTP_ADDRESS") ?? throw new DeclaradException("The http backend needs 'address'."), "address");
            _updateMethod = Setting("update_method", "TF_HTTP_UPDATE_METHOD") is { Length: > 0 } u ? u : "POST";
            _lockAddress = Setting("lock_address", "TF_HTTP_LOCK_ADDRESS") is { Length: > 0 } l ? Url(l, "lock_address") : null;
            _unlockAddress = Setting("unlock_address", "TF_HTTP_UNLOCK_ADDRESS") is { Length: > 0 } ul ? Url(ul, "unlock_address") : _lockAddress;
            _lockMethod = Setting("lock_method", "TF_HTTP_LOCK_METHOD") is { Length: > 0 } lm ? lm : "LOCK";
            _unlockMethod = Setting("unlock_method", "TF_HTTP_UNLOCK_METHOD") is { Length: > 0 } um ? um : "UNLOCK";

            var handler = new HttpClientHandler();
            if (config.GetBool("skip_cert_verification", false))
            {
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            string username = Setting("username", "TF_HTTP_USERNAME");
            if (username.Length > 0)
            {
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{Setting("password", "TF_HTTP_PASSWORD")}"));
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
        }

        public string Type => "http";
        public string Location => _address.ToString();

        private HttpResponseMessage Send(string method, Uri uri, string? body)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), uri);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            try
            {
                return _http.Send(request);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new DeclaradException($"HTTP backend request {method} {uri} failed: {ex.Message}", default, ex);
            }
        }

        private static string Body(HttpResponseMessage response) => response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        public StateFile? Read()
        {
            using var response = Send("GET", _address, null);
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new DeclaradException($"HTTP backend GET {_address} returned {(int)response.StatusCode}: {Body(response)}");
            }
            return StateStore.Parse(Body(response), Location);
        }

        public void Write(StateFile state)
        {
            var uri = _lockId == null ? _address : new UriBuilder(_address) { Query = (_address.Query.Length > 1 ? _address.Query[1..] + "&" : "") + "ID=" + Uri.EscapeDataString(_lockId) }.Uri;
            using var response = Send(_updateMethod, uri, StateStore.ToText(state));
            if (!response.IsSuccessStatusCode)
            {
                throw new DeclaradException($"HTTP backend {_updateMethod} {_address} returned {(int)response.StatusCode}: {Body(response)}");
            }
        }

        public bool Lock(LockInfo info)
        {
            if (_lockAddress == null)
            {
                return false; // Locking not configured on this backend.
            }
            using var response = Send(_lockMethod, _lockAddress, info.ToJson());
            if (response.StatusCode is HttpStatusCode.Locked or HttpStatusCode.Conflict)
            {
                throw new StateLockedException(Location, LockInfo.Parse(Body(response)));
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new DeclaradException($"HTTP backend {_lockMethod} {_lockAddress} returned {(int)response.StatusCode}: {Body(response)}");
            }
            _lockId = info.Id;
            return true;
        }

        public void Unlock(string lockId)
        {
            if (_unlockAddress == null)
            {
                return;
            }
            using var response = Send(_unlockMethod, _unlockAddress, new LockInfo { Id = lockId }.ToJson());
            if (!response.IsSuccessStatusCode)
            {
                throw new DeclaradException($"HTTP backend {_unlockMethod} {_unlockAddress} returned {(int)response.StatusCode}: {Body(response)}");
            }
            _lockId = null;
        }

        public void ForceUnlock(string lockId) => Unlock(lockId);
    }
}
