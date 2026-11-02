using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Declarad.Directory
{
    /// <summary><see cref="IDirectory"/> over LDAP using System.DirectoryServices.Protocols (works on Windows, Linux and macOS).</summary>
    public sealed class LdapDirectory : IDirectory
    {
        private const int PageSize = 500;
        private static readonly HashSet<string> BinaryAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "objectGUID", "objectSid", "nTSecurityDescriptor", "msDS-GroupMSAMembership", "thumbnailPhoto", "userCertificate", "logonHours",
            "dnsRecord", "dNSProperty",
        };

        private readonly ConnectionSettings _settings;
        private readonly ILogger _logger;
        private LdapConnection? _connection;
        private string? _baseDn;
        private string? _domainName;
        private string? _configurationNamingContext;
        private bool? _recycleBinEnabled;

        public LdapDirectory(ConnectionSettings settings, ILogger logger)
        {
            _settings = settings;
            _logger = logger;
        }

        private bool _sealed = true;

        public bool IsEncrypted => _settings.IsEncrypted || (_settings.Auth == LdapAuth.Negotiate && _sealed);
        public string BaseDn => _baseDn ?? throw new InvalidOperationException("Not connected.");
        public string DomainName => _domainName ?? throw new InvalidOperationException("Not connected.");

        private LdapConnection Connection => _connection ?? throw new InvalidOperationException("Not connected.");

        public void Connect()
        {
            var identifier = new LdapDirectoryIdentifier(_settings.Server, _settings.EffectivePort, fullyQualifiedDnsHostName: true, connectionless: false);
            NetworkCredential? credential = _settings.Username != null ? new NetworkCredential(_settings.Username, _settings.Password) : null;
            var connection = new LdapConnection(identifier)
            {
                AuthType = _settings.Auth == LdapAuth.Negotiate ? AuthType.Negotiate : AuthType.Basic,
                Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds),
                AutoBind = false,
            };
            if (credential != null)
            {
                connection.Credential = credential;
            }
            connection.SessionOptions.ProtocolVersion = 3;
            // AD returns referrals for other partitions; chasing them hangs on non-domain-joined clients.
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

            if (_settings.IsEncrypted)
            {
                ConfigureTls(connection);
            }
            else if (_settings.Auth == LdapAuth.Negotiate)
            {
                // Without TLS, a Negotiate bind is only "encrypted" (as AD requires for passwords) when sealed.
                try
                {
                    connection.SessionOptions.Signing = true;
                    connection.SessionOptions.Sealing = true;
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or LdapException or DirectoryOperationException)
                {
                    _sealed = false;
                    _logger.LogWarning("LDAP signing and sealing are not available here ({Error}); use protocol = \"ldaps\" to manage passwords.", ex.Message);
                }
            }
            if (_settings.Protocol == LdapProtocol.Ldaps)
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }

            _logger.LogDebug("Connecting to {Server}:{Port} using {Protocol} ({Auth})", _settings.Server, _settings.EffectivePort, _settings.Protocol, _settings.Auth);
            try
            {
                if (_settings.Protocol == LdapProtocol.StartTls)
                {
                    connection.SessionOptions.StartTransportLayerSecurity(null);
                }
                connection.Bind();
            }
            catch (LdapException ex)
            {
                connection.Dispose();
                throw ex.ErrorCode switch
                {
                    LdapResult.InvalidCredentials => new DirectoryException($"Authentication failed for '{_settings.Username}': invalid credentials. {ex.ServerErrorMessage}".Trim(), ex.ErrorCode, ex),
                    LdapResult.ServerDown => new DirectoryException(
                        $"Cannot reach LDAP server {_settings.Server}:{_settings.EffectivePort} ({_settings.Protocol}). Check the address, firewall and, for LDAPS, that the server certificate is trusted.",
                        ex.ErrorCode, ex),
                    _ => new DirectoryException($"Failed to bind to {_settings.Server}: {ex.Message} {ex.ServerErrorMessage}".Trim(), ex.ErrorCode, ex),
                };
            }
            catch (Exception ex) when (ex is DirectoryOperationException or PlatformNotSupportedException)
            {
                connection.Dispose();
                throw new DirectoryException($"Failed to connect to {_settings.Server}: {ex.Message}", 0, ex);
            }

            _connection = connection;
            ReadRootDse();
        }

        private void ConfigureTls(LdapConnection connection)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (_settings.InsecureSkipVerify)
                {
                    connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
                }
                return;
            }

            // On Linux/macOS the native OpenLDAP library performs certificate validation. A custom CA
            // directory can be supplied; skipping verification is done with LDAPTLS_REQCERT=never.
            if (_settings.CaCertDir != null)
            {
                connection.SessionOptions.TrustedCertificatesDirectory = _settings.CaCertDir;
                connection.SessionOptions.StartNewTlsSessionContext();
            }
            if (_settings.InsecureSkipVerify && Environment.GetEnvironmentVariable("LDAPTLS_REQCERT") == null)
            {
                _logger.LogWarning("insecure_skip_verify is only honoured natively on Windows. On Linux/macOS run with the environment variable LDAPTLS_REQCERT=never instead.");
            }
        }

        private void ReadRootDse()
        {
            var response = Send<SearchResponse>(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "configurationNamingContext", "dnsHostName"));
            string? namingContext = response.Entries.Count > 0 ? First(response.Entries[0], "defaultNamingContext") : null;
            _configurationNamingContext = response.Entries.Count > 0 ? First(response.Entries[0], "configurationNamingContext") : null;

            _baseDn = _settings.BaseDn ?? namingContext ?? (_settings.Domain != null ? Dn.FromDomain(_settings.Domain) : null)
                ?? throw new DirectoryException("Could not determine the domain naming context; set base_dn in the connection block.");
            _domainName = _settings.Domain ?? Dn.ToDomain(namingContext ?? _baseDn)
                ?? throw new DirectoryException("Could not determine the domain DNS name; set domain in the connection block.");
            _logger.LogDebug("Connected. Naming context {BaseDn}, domain {Domain}", _baseDn, _domainName);
        }

        public DirectoryObject? GetByGuid(Guid guid, IReadOnlyCollection<string> attributes, bool withSecurityDescriptor = false) =>
            GetByDn($"<GUID={guid}>", attributes, withSecurityDescriptor);

        public DirectoryObject? GetByDn(string dn, IReadOnlyCollection<string> attributes, bool withSecurityDescriptor = false)
        {
            var request = new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, WithIdentity(attributes, withSecurityDescriptor));
            if (withSecurityDescriptor)
            {
                request.Controls.Add(new SecurityDescriptorFlagControl(SecurityMasks.Dacl));
            }
            try
            {
                var response = Send<SearchResponse>(request);
                return response.Entries.Count == 0 ? null : ToObject(response.Entries[0]);
            }
            catch (DirectoryException ex) when (ex.ResultCode == LdapResult.NoSuchObject)
            {
                return null;
            }
        }

        public IReadOnlyList<DirectoryObject> Search(string baseDn, string filter, bool subtree, IReadOnlyCollection<string> attributes)
        {
            var results = new List<DirectoryObject>();
            var request = new SearchRequest(baseDn, filter, subtree ? SearchScope.Subtree : SearchScope.OneLevel, WithIdentity(attributes, false));
            var paging = new PageResultRequestControl(PageSize);
            request.Controls.Add(paging);
            while (true)
            {
                SearchResponse response;
                try
                {
                    response = Send<SearchResponse>(request);
                }
                catch (DirectoryException ex) when (ex.ResultCode == LdapResult.NoSuchObject)
                {
                    return results;
                }
                foreach (SearchResultEntry entry in response.Entries)
                {
                    results.Add(ToObject(entry));
                }
                var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (pageResponse == null || pageResponse.Cookie.Length == 0)
                {
                    return results;
                }
                paging.Cookie = pageResponse.Cookie;
            }
        }

        public void Add(string dn, IReadOnlyDictionary<string, object[]> attributes)
        {
            var request = new AddRequest(dn, attributes.Select(kv => new DirectoryAttribute(kv.Key, kv.Value)).ToArray());
            Send<AddResponse>(request);
        }

        public void Modify(string dn, IReadOnlyList<Modification> modifications)
        {
            if (modifications.Count == 0)
            {
                return;
            }
            var mods = modifications.Select(m =>
            {
                var mod = new DirectoryAttributeModification
                {
                    Name = m.Attribute,
                    Operation = m.Op switch
                    {
                        ModOp.Add => DirectoryAttributeOperation.Add,
                        ModOp.Delete => DirectoryAttributeOperation.Delete,
                        _ => DirectoryAttributeOperation.Replace,
                    },
                };
                foreach (var v in m.Values)
                {
                    switch (v)
                    {
                        case byte[] bytes: mod.Add(bytes); break;
                        default: mod.Add(v.ToString()!); break;
                    }
                }
                return mod;
            }).ToArray();
            Send<ModifyResponse>(new ModifyRequest(dn, mods));
        }

        public void SetSecurityDescriptor(string dn, byte[] securityDescriptor)
        {
            var mod = new DirectoryAttributeModification { Name = "nTSecurityDescriptor", Operation = DirectoryAttributeOperation.Replace };
            mod.Add(securityDescriptor);
            var request = new ModifyRequest(dn, mod);
            request.Controls.Add(new SecurityDescriptorFlagControl(SecurityMasks.Dacl));
            Send<ModifyResponse>(request);
        }

        public void Rename(string dn, string newRdn, string newParentDn)
        {
            Send<ModifyDNResponse>(new ModifyDNRequest(dn, newParentDn, newRdn) { DeleteOldRdn = true });
        }

        public void Delete(string dn)
        {
            Send<DeleteResponse>(new DeleteRequest(dn));
        }

        public DirectoryObject? GetDeletedByGuid(Guid guid, IReadOnlyCollection<string> attributes)
        {
            if (!RecycleBinEnabled)
            {
                return null;
            }
            var request = new SearchRequest($"<GUID={guid}>", "(isDeleted=TRUE)", SearchScope.Base,
                WithIdentity(attributes.Concat(new[] { "isDeleted", "isRecycled", "lastKnownParent" }).ToList(), false));
            request.Controls.Add(new ShowDeletedControl());
            try
            {
                var response = Send<SearchResponse>(request);
                if (response.Entries.Count == 0)
                {
                    return null;
                }
                var obj = ToObject(response.Entries[0]);
                // A recycled object has lost its attributes and memberships; it can't be restored meaningfully.
                return string.Equals(obj.Get("isRecycled"), "TRUE", StringComparison.OrdinalIgnoreCase) ? null : obj;
            }
            catch (DirectoryException ex) when (ex.ResultCode == LdapResult.NoSuchObject)
            {
                return null;
            }
        }

        public void Restore(string deletedDn, string newDn)
        {
            // Restoring is a modify of the deleted object, with the Show Deleted control: remove isDeleted and give it a DN.
            var request = new ModifyRequest(deletedDn, new[]
            {
                new DirectoryAttributeModification { Name = "isDeleted", Operation = DirectoryAttributeOperation.Delete },
                new DirectoryAttributeModification { Name = "distinguishedName", Operation = DirectoryAttributeOperation.Replace },
            });
            request.Modifications[1].Add(newDn);
            request.Controls.Add(new ShowDeletedControl());
            Send<ModifyResponse>(request);
        }

        /// <summary>Whether the forest has the Recycle Bin optional feature enabled (it is listed on the Partitions container).</summary>
        private bool RecycleBinEnabled => _recycleBinEnabled ??= ReadRecycleBinEnabled();

        private bool ReadRecycleBinEnabled()
        {
            if (_configurationNamingContext == null)
            {
                return false;
            }
            var partitions = GetByDn($"CN=Partitions,{_configurationNamingContext}", new[] { "msDS-EnabledFeature" });
            bool enabled = partitions?.GetAll("msDS-EnabledFeature").Any(f => f.StartsWith("CN=Recycle Bin Feature,", StringComparison.OrdinalIgnoreCase)) == true;
            _logger.LogDebug("AD Recycle Bin {State}", enabled ? "enabled" : "not enabled");
            return enabled;
        }

        private static string[] WithIdentity(IReadOnlyCollection<string> attributes, bool withSecurityDescriptor)
        {
            var set = new List<string>(attributes) { "objectGUID", "objectSid", "objectClass", "distinguishedName" };
            if (withSecurityDescriptor)
            {
                set.Add("nTSecurityDescriptor");
            }
            return set.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private DirectoryObject ToObject(SearchResultEntry entry)
        {
            var attributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            Guid guid = Guid.Empty;
            byte[]? sd = null;
            string? sid = null;
            var binary = new Dictionary<string, byte[][]>(StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryAttribute attr in entry.Attributes.Values)
            {
                string name = attr.Name;
                if (name.Equals("objectGUID", StringComparison.OrdinalIgnoreCase))
                {
                    guid = new Guid((byte[])attr.GetValues(typeof(byte[]))[0]);
                    continue;
                }
                if (name.Equals("nTSecurityDescriptor", StringComparison.OrdinalIgnoreCase))
                {
                    sd = (byte[])attr.GetValues(typeof(byte[]))[0];
                    continue;
                }
                if (name.Equals("objectSid", StringComparison.OrdinalIgnoreCase))
                {
                    sid = Sid.ToString((byte[])attr.GetValues(typeof(byte[]))[0]);
                    continue;
                }
                if (BinaryAttributes.Contains(name))
                {
                    binary[name] = attr.GetValues(typeof(byte[])).Cast<byte[]>().ToArray();
                    continue;
                }

                int rangeIndex = name.IndexOf(";range=", StringComparison.OrdinalIgnoreCase);
                if (rangeIndex >= 0)
                {
                    string baseName = name[..rangeIndex];
                    attributes[baseName] = ReadRanged(entry.DistinguishedName, baseName, attr);
                    continue;
                }
                attributes[name] = (string[])attr.GetValues(typeof(string));
            }
            return new DirectoryObject(entry.DistinguishedName, guid, attributes, sd, sid, binary);
        }

        /// <summary>AD returns large multi-valued attributes (e.g. group members over 1500) in ranges.</summary>
        private string[] ReadRanged(string dn, string attribute, DirectoryAttribute first)
        {
            var values = new List<string>((string[])first.GetValues(typeof(string)));
            string rangeName = first.Name;
            while (!rangeName.EndsWith("-*", StringComparison.Ordinal))
            {
                int start = values.Count;
                var response = Send<SearchResponse>(new SearchRequest(dn, "(objectClass=*)", SearchScope.Base, $"{attribute};range={start}-*"));
                var next = response.Entries.Count > 0
                    ? response.Entries[0].Attributes.Values.Cast<DirectoryAttribute>().FirstOrDefault(a => a.Name.StartsWith(attribute + ";range=", StringComparison.OrdinalIgnoreCase))
                    : null;
                if (next == null)
                {
                    break;
                }
                values.AddRange((string[])next.GetValues(typeof(string)));
                rangeName = next.Name;
            }
            return values.ToArray();
        }

        private static string? First(SearchResultEntry entry, string attribute) =>
            entry.Attributes.Contains(attribute) && entry.Attributes[attribute].Count > 0 ? entry.Attributes[attribute][0]?.ToString() : null;

        private T Send<T>(DirectoryRequest request) where T : DirectoryResponse
        {
            try
            {
                return (T)Connection.SendRequest(request);
            }
            catch (DirectoryOperationException ex)
            {
                int code = ex.Response != null ? (int)ex.Response.ResultCode : 0;
                string server = ex.Response?.ErrorMessage?.Trim('\0', ' ', '\n') ?? "";
                throw new DirectoryException($"{Describe(request)} failed: {ex.Message}{(server.Length > 0 ? $" ({server})" : "")}", code, ex);
            }
            catch (LdapException ex)
            {
                throw new DirectoryException($"{Describe(request)} failed: {ex.Message} {ex.ServerErrorMessage}".Trim(), ex.ErrorCode, ex);
            }
        }

        private static string Describe(DirectoryRequest request) => request switch
        {
            SearchRequest s => $"Search '{s.DistinguishedName}'",
            AddRequest a => $"Create '{a.DistinguishedName}'",
            ModifyRequest m => $"Modify '{m.DistinguishedName}'",
            ModifyDNRequest r => $"Rename '{r.DistinguishedName}'",
            DeleteRequest d => $"Delete '{d.DistinguishedName}'",
            _ => "LDAP request",
        };

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
