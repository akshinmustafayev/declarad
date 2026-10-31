using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Declarad.State.Backends
{
    public static class BackendFactory
    {
        /// <summary>Settings each backend accepts. Unknown settings are rejected so typos surface early.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> KnownSettings = new Dictionary<string, string[]>
        {
            ["local"] = new[] { "path" },
            ["http"] = new[] { "address", "update_method", "lock_address", "lock_method", "unlock_address", "unlock_method", "username", "password", "skip_cert_verification" },
            ["s3"] = new[]
            {
                "bucket", "key", "region", "endpoint", "endpoints.s3", "access_key", "secret_key", "token", "profile", "encrypt", "kms_key_id",
                "use_path_style", "force_path_style", "use_lockfile", "acl", "dynamodb_table",
                "skip_credentials_validation", "skip_region_validation", "skip_requesting_account_id", "skip_metadata_api_check", "skip_s3_checksum",
            },
            ["azurerm"] = new[]
            {
                "storage_account_name", "container_name", "key", "access_key", "sas_token", "connection_string", "use_azuread_auth", "endpoint",
                "resource_group_name", "subscription_id", "tenant_id", "client_id", "client_secret", "use_oidc", "use_msi",
            },
        };

        public static IStateBackend Create(BackendConfig config, string rootDir, Func<string, string?> getEnv)
        {
            if (!KnownSettings.TryGetValue(config.Type, out var known))
            {
                throw new DeclaradException($"Unknown backend '{config.Type}'. Supported: {string.Join(", ", KnownSettings.Keys)}.");
            }
            foreach (string key in config.Settings.Keys.Where(k => !known.Contains(k)))
            {
                throw new DeclaradException($"The {config.Type} backend has no setting '{key}'. Known: {string.Join(", ", known)}.");
            }
            return config.Type switch
            {
                "local" => new LocalBackend(Path.GetFullPath(Path.Combine(rootDir, config.Get("path") ?? StateStore.DefaultFileName))),
                "http" => new HttpBackend(config, getEnv),
                "s3" => new S3Backend(config, getEnv),
                "azurerm" => new AzureBackend(config, getEnv),
                _ => throw new InvalidOperationException(),
            };
        }
    }

    /// <summary>State in a local file (the default). The lock is a sibling file holding the lock info.</summary>
    public sealed class LocalBackend : IStateBackend
    {
        public string Path { get; }
        private string LockPath => Path + ".lock.info";

        public LocalBackend(string path)
        {
            Path = path;
        }

        public string Type => "local";
        public string Location => Path;

        public StateFile? Read() => File.Exists(Path) ? StateStore.Parse(File.ReadAllText(Path), Path) : null;

        public void Write(StateFile state) => StateStore.WriteFile(Path, state);

        public bool Lock(LockInfo info)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            try
            {
                using var stream = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Encoding.UTF8.GetBytes(info.ToJson()));
                return true;
            }
            catch (IOException) when (File.Exists(LockPath))
            {
                throw new StateLockedException(Path, ReadLock());
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new DeclaradException($"Cannot create the lock file {LockPath}: {ex.Message}", default, ex);
            }
        }

        private LockInfo? ReadLock()
        {
            try
            {
                return LockInfo.Parse(File.ReadAllText(LockPath));
            }
            catch (IOException)
            {
                return null;
            }
        }

        public void Unlock(string lockId)
        {
            if (ReadLock() is { } holder && holder.Id != lockId)
            {
                throw new DeclaradException($"Cannot unlock {Path}: it is held by lock {holder.Id}, not {lockId}.");
            }
            File.Delete(LockPath);
        }

        public void ForceUnlock(string lockId)
        {
            var holder = ReadLock();
            if (holder == null && !File.Exists(LockPath))
            {
                throw new DeclaradException($"The state at {Path} is not locked.");
            }
            if (holder != null && holder.Id != lockId)
            {
                throw new DeclaradException($"The lock on {Path} has id {holder.Id}, not {lockId}.");
            }
            File.Delete(LockPath);
        }
    }

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

    /// <summary>
    /// State in an S3 bucket. Locking uses a <c>&lt;key&gt;.tflock</c> object written with a conditional put
    /// (If-None-Match), like Terraform's use_lockfile, so no DynamoDB table is needed.
    /// </summary>
    public sealed class S3Backend : IStateBackend
    {
        private readonly IAmazonS3 _s3;
        private readonly string _bucket;
        private readonly string _key;
        private readonly bool _encrypt;
        private readonly string? _kmsKeyId;
        private readonly bool _useLockfile;

        public S3Backend(BackendConfig config, Func<string, string?> getEnv)
        {
            if (config.Get("dynamodb_table") != null)
            {
                throw new DeclaradException("dynamodb_table locking is not supported; use use_lockfile = true (the default).");
            }
            _bucket = config.Require("bucket");
            _key = config.Require("key");
            _encrypt = config.GetBool("encrypt", false);
            _kmsKeyId = config.Get("kms_key_id");
            _useLockfile = config.GetBool("use_lockfile", true);

            var s3Config = new AmazonS3Config
            {
                ForcePathStyle = config.GetBool("use_path_style", config.GetBool("force_path_style", false)),
            };
            string? region = config.Get("region") ?? getEnv("AWS_REGION") ?? getEnv("AWS_DEFAULT_REGION");
            string? endpoint = config.Get("endpoint") ?? config.Get("endpoints.s3") ?? getEnv("AWS_ENDPOINT_URL_S3");
            if (endpoint != null)
            {
                s3Config.ServiceURL = endpoint;
                s3Config.AuthenticationRegion = region ?? "us-east-1";
            }
            else
            {
                s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(region ?? throw new DeclaradException("The s3 backend needs 'region' (or AWS_REGION)."));
            }

            AWSCredentials? credentials = null;
            if (config.Get("access_key") is { } accessKey)
            {
                string secret = config.Get("secret_key") ?? throw new DeclaradException("The s3 backend needs 'secret_key' with 'access_key'.");
                credentials = config.Get("token") is { } token ? new SessionAWSCredentials(accessKey, secret, token) : new BasicAWSCredentials(accessKey, secret);
            }
            else if (config.Get("profile") is { } profile)
            {
                credentials = new CredentialProfileStoreChain().TryGetAWSCredentials(profile, out var c) ? c
                    : throw new DeclaradException($"AWS profile '{profile}' was not found.");
            }
            _s3 = credentials != null ? new AmazonS3Client(credentials, s3Config) : new AmazonS3Client(s3Config);
        }

        public string Type => "s3";
        public string Location => $"s3://{_bucket}/{_key}";
        private string LockKey => _key + ".tflock";

        /// <summary>Waits for an S3 call; errors become DeclaradException unless their status is in <paramref name="handled"/>.</summary>
        private static T Wait<T>(Task<T> task, string what, params HttpStatusCode[] handled)
        {
            try
            {
                return task.GetAwaiter().GetResult();
            }
            catch (AmazonServiceException ex) when (!handled.Contains(ex.StatusCode))
            {
                throw new DeclaradException($"S3 {what} failed: {ex.Message}", default, ex);
            }
            catch (AmazonClientException ex)
            {
                throw new DeclaradException($"S3 {what} failed: {ex.Message}", default, ex);
            }
        }

        private string? GetText(string key)
        {
            try
            {
                using var response = Wait(_s3.GetObjectAsync(_bucket, key), $"read of {key}", HttpStatusCode.NotFound);
                using var reader = new StreamReader(response.ResponseStream);
                return reader.ReadToEnd();
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                if (ex.ErrorCode == "NoSuchBucket")
                {
                    throw new DeclaradException($"S3 bucket '{_bucket}' does not exist (or is not visible with these credentials).", default, ex);
                }
                return null;
            }
        }

        public StateFile? Read() => GetText(_key) is { } text ? StateStore.Parse(text, Location) : null;

        public void Write(StateFile state)
        {
            var request = new PutObjectRequest { BucketName = _bucket, Key = _key, ContentBody = StateStore.ToText(state), ContentType = "application/json" };
            if (_kmsKeyId != null)
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS;
                request.ServerSideEncryptionKeyManagementServiceKeyId = _kmsKeyId;
            }
            else if (_encrypt)
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
            }
            Wait(_s3.PutObjectAsync(request), "write of the state");
        }

        public bool Lock(LockInfo info)
        {
            if (!_useLockfile)
            {
                return false;
            }
            try
            {
                Wait(_s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = LockKey,
                    ContentBody = info.ToJson(),
                    ContentType = "application/json",
                    IfNoneMatch = "*",
                }), "lock", HttpStatusCode.PreconditionFailed, HttpStatusCode.Conflict);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            {
                throw new StateLockedException(Location, LockInfo.Parse(GetText(LockKey)));
            }
        }

        public void Unlock(string lockId)
        {
            if (!_useLockfile)
            {
                return;
            }
            if (LockInfo.Parse(GetText(LockKey)) is { } holder && holder.Id != lockId)
            {
                throw new DeclaradException($"Cannot unlock {Location}: it is held by lock {holder.Id}, not {lockId}.");
            }
            Wait(_s3.DeleteObjectAsync(_bucket, LockKey), "unlock");
        }

        public void ForceUnlock(string lockId)
        {
            var holder = LockInfo.Parse(GetText(LockKey)) ?? throw new DeclaradException($"The state at {Location} is not locked.");
            if (holder.Id != lockId)
            {
                throw new DeclaradException($"The lock on {Location} has id {holder.Id}, not {lockId}.");
            }
            Wait(_s3.DeleteObjectAsync(_bucket, LockKey), "unlock");
        }
    }

    /// <summary>
    /// State in an Azure Storage blob. Locking takes an infinite lease on the blob and records the lock info in
    /// its "terraformlockid" metadata, as Terraform's azurerm backend does.
    /// </summary>
    public sealed class AzureBackend : IStateBackend
    {
        private const string LockMetadata = "terraformlockid";
        private readonly BlobClient _blob;
        private string? _leaseId;

        public AzureBackend(BackendConfig config, Func<string, string?> getEnv)
        {
            string container = config.Require("container_name");
            string key = config.Require("key");
            string? connectionString = config.Get("connection_string") ?? getEnv("ARM_CONNECTION_STRING");
            if (connectionString != null)
            {
                _blob = new BlobContainerClient(connectionString, container).GetBlobClient(key);
                return;
            }

            string account = config.Get("storage_account_name") ?? throw new DeclaradException("The azurerm backend needs 'storage_account_name' (or connection_string).");
            var endpoint = new Uri((config.Get("endpoint") ?? $"https://{account}.blob.core.windows.net").TrimEnd('/') + $"/{container}");
            if ((config.Get("access_key") ?? getEnv("ARM_ACCESS_KEY")) is { } accessKey)
            {
                _blob = new BlobContainerClient(endpoint, new StorageSharedKeyCredential(account, accessKey)).GetBlobClient(key);
            }
            else if ((config.Get("sas_token") ?? getEnv("ARM_SAS_TOKEN")) is { } sas)
            {
                _blob = new BlobContainerClient(endpoint, new AzureSasCredential(sas.TrimStart('?'))).GetBlobClient(key);
            }
            else if (config.GetBool("use_azuread_auth", false) || config.GetBool("use_msi", false) || config.GetBool("use_oidc", false))
            {
                _blob = new BlobContainerClient(endpoint, new DefaultAzureCredential()).GetBlobClient(key);
            }
            else
            {
                throw new DeclaradException("The azurerm backend needs access_key (ARM_ACCESS_KEY), sas_token (ARM_SAS_TOKEN), connection_string or use_azuread_auth = true.");
            }
        }

        public string Type => "azurerm";
        public string Location => _blob.Uri.GetLeftPart(UriPartial.Path);

        /// <summary>Runs an Azure call; errors become DeclaradException unless their status is in <paramref name="handled"/>.</summary>
        private static T Call<T>(Func<T> action, string what, params int[] handled)
        {
            try
            {
                return action();
            }
            catch (RequestFailedException ex) when (!handled.Contains(ex.Status))
            {
                throw new DeclaradException($"Azure storage {what} failed: {ex.Message.Split('\n')[0]}", default, ex);
            }
        }

        public StateFile? Read()
        {
            try
            {
                var content = Call(() => _blob.DownloadContent().Value.Content, "read of the state", 404);
                return StateStore.Parse(content.ToString(), Location);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public void Write(StateFile state)
        {
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Conditions = _leaseId != null ? new BlobRequestConditions { LeaseId = _leaseId } : null,
                // Uploading replaces metadata; keep the lock info while the lease is held.
                Metadata = _leaseId != null ? _lockMetadata : null,
            };
            Call(() => _blob.Upload(BinaryData.FromString(StateStore.ToText(state)), options), "write of the state");
        }

        private IDictionary<string, string>? _lockMetadata;

        public bool Lock(LockInfo info)
        {
            // A lease needs an existing blob; create an empty one if this is the first run.
            try
            {
                Call(() => _blob.Upload(BinaryData.FromString(""), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }), "lock", 409, 412);
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
            }

            var lease = _blob.GetBlobLeaseClient(info.Id);
            try
            {
                Call(() => lease.Acquire(TimeSpan.FromSeconds(-1)), "lock", 409);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                throw new StateLockedException(Location, ReadLockInfo());
            }
            _leaseId = info.Id;
            var metadata = Call(() => _blob.GetProperties(new BlobRequestConditions { LeaseId = _leaseId }).Value.Metadata, "lock");
            metadata[LockMetadata] = Convert.ToBase64String(Encoding.UTF8.GetBytes(info.ToJson()));
            Call(() => _blob.SetMetadata(metadata, new BlobRequestConditions { LeaseId = _leaseId }), "lock");
            _lockMetadata = metadata;
            return true;
        }

        private LockInfo? ReadLockInfo()
        {
            try
            {
                var metadata = _blob.GetProperties().Value.Metadata;
                return metadata.TryGetValue(LockMetadata, out var encoded) ? LockInfo.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded))) : null;
            }
            catch (Exception ex) when (ex is RequestFailedException or FormatException)
            {
                return null;
            }
        }

        public void Unlock(string lockId)
        {
            var metadata = Call(() => _blob.GetProperties().Value.Metadata, "unlock");
            if (metadata.Remove(LockMetadata))
            {
                Call(() => _blob.SetMetadata(metadata, new BlobRequestConditions { LeaseId = lockId }), "unlock");
            }
            Call(() => _blob.GetBlobLeaseClient(lockId).Release(), "unlock");
            _leaseId = null;
            _lockMetadata = null;
        }

        public void ForceUnlock(string lockId)
        {
            var holder = ReadLockInfo();
            if (holder != null && holder.Id != lockId)
            {
                throw new DeclaradException($"The lock on {Location} has id {holder.Id}, not {lockId}.");
            }
            try
            {
                Call(() => _blob.GetBlobLeaseClient().Break(TimeSpan.Zero), "force-unlock", 409);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                throw new DeclaradException($"The state at {Location} is not locked.", default, ex);
            }
            var metadata = Call(() => _blob.GetProperties().Value.Metadata, "force-unlock");
            if (metadata.Remove(LockMetadata))
            {
                Call(() => _blob.SetMetadata(metadata), "force-unlock");
            }
        }
    }
}
