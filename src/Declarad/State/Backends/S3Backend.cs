using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.S3;
using Amazon.S3.Model;

namespace Declarad.State.Backends
{
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
}
