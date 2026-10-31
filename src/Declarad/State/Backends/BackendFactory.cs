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
}
