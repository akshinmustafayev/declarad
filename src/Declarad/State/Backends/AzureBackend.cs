using System.Text;
using Azure;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Declarad.State.Backends
{
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
