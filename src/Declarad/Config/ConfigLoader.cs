using Declarad.Directory;
using Declarad.Hcl;
using Declarad.Resources;

namespace Declarad.Config
{
    /// <summary>
    /// Loads the <c>*.dad</c> files of a root module (and any local child modules) into a <see cref="Configuration"/>.
    /// References are resolved lazily per attribute, so <c>ou.sales.dn</c> only evaluates the OU's name and path;
    /// this keeps mutual references (e.g. group members) free of cycles.
    /// </summary>
    public sealed class ConfigLoader
    {
        public const string FileExtension = ".dad";
        internal const int MaxModuleDepth = 16;

        internal readonly LoadOptions Options;
        internal readonly Functions Functions;
        internal readonly string RootDir;
        internal readonly List<DeclaradException> Errors = new();
        internal readonly List<string> Warnings = new();
        internal readonly Stack<Frame> Frames = new();
        private readonly Dictionary<string, List<Body>> _parsedModules = new(StringComparer.Ordinal);

        /// <summary>
        /// One step of the evaluation stack. Resource-attribute frames turn the dependencies they collect into
        /// ordering edges; collector frames (locals, outputs, module inputs, data sources) pass them on.
        /// </summary>
        internal sealed class Frame
        {
            public Instance? Instance { get; init; }
            public string? Attribute { get; init; }
            public bool Collector { get; init; }
            public HashSet<string> Deps { get; } = new(StringComparer.Ordinal);
        }

        private ConfigLoader(string rootDir, LoadOptions options)
        {
            RootDir = Path.GetFullPath(rootDir);
            Options = options;
            Functions = new Functions(RootDir, options.GetEnv ?? Environment.GetEnvironmentVariable);
        }

        public static IReadOnlyList<string> FindFiles(string dir)
        {
            if (!System.IO.Directory.Exists(dir))
            {
                throw new DeclaradException($"Directory '{dir}' does not exist.");
            }
            return System.IO.Directory.GetFiles(dir, "*" + FileExtension, SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToList();
        }

        private static IReadOnlyList<(string File, string Source)> ReadFromDisk(string dir) =>
            FindFiles(dir).Select(f => (Path.GetFileName(f), File.ReadAllText(f))).ToList();

        public static Configuration Load(string dir, LoadOptions? options = null)
        {
            var sources = (options?.ReadModule ?? ReadFromDisk)(dir);
            if (sources.Count == 0)
            {
                throw new DeclaradException($"No {FileExtension} files found in '{dir}'. Run 'declarad init' to create one.");
            }
            return LoadSources(dir, sources, options);
        }

        public static Configuration LoadSources(string dir, IEnumerable<(string File, string Source)> sources, LoadOptions? options = null)
        {
            var loader = new ConfigLoader(dir, options ?? new LoadOptions());
            var bodies = loader.Parse(sources);
            loader.ThrowIfErrors();
            return loader.Build(bodies);
        }

        public static Configuration LoadSources(
            string dir,
            IEnumerable<(string File, string Source)> sources,
            IReadOnlyDictionary<string, string>? vars,
            Func<string, string?>? getEnv = null) =>
            LoadSources(dir, sources, new LoadOptions { Vars = vars, GetEnv = getEnv });

        private List<Body> Parse(IEnumerable<(string File, string Source)> sources)
        {
            var bodies = new List<Body>();
            foreach (var (file, source) in sources)
            {
                try
                {
                    bodies.Add(Parser.ParseFile(source, file));
                }
                catch (DeclaradException ex)
                {
                    Errors.Add(ex);
                }
            }
            return bodies;
        }

        internal List<Body> ParseModule(string dir, SourcePos pos)
        {
            string full = Path.GetFullPath(dir);
            if (_parsedModules.TryGetValue(full, out var cached))
            {
                return cached;
            }
            IReadOnlyList<(string File, string Source)> sources;
            try
            {
                sources = (Options.ReadModule ?? ReadFromDisk)(full);
            }
            catch (DeclaradException ex)
            {
                throw new DeclaradException($"Cannot load module: {ex.Message}", pos);
            }
            if (sources.Count == 0)
            {
                throw new DeclaradException($"Module directory '{full}' contains no {FileExtension} files.", pos);
            }
            string relative = Path.GetRelativePath(RootDir, full);
            var bodies = Parse(sources.Select(s => (Path.Combine(relative, s.File), s.Source)));
            _parsedModules[full] = bodies;
            return bodies;
        }

        private ModuleManifest? _manifest;

        /// <summary>Directory of a module call: local sources relative to the caller, remote ones from the installed manifest.</summary>
        internal string ResolveModuleDir(string source, string callerDir, string key, SourcePos pos)
        {
            var parsed = ModuleSource.Parse(source);
            if (parsed.Kind == ModuleSourceKind.Local)
            {
                return Path.GetFullPath(Path.Combine(callerDir, parsed.Url));
            }
            _manifest ??= ModuleManifest.Load(RootDir);
            if (!_manifest.Entries.TryGetValue(key, out var entry))
            {
                throw new DeclaradException($"Module '{key}' ({source}) is not installed. Run 'declarad init'.", pos);
            }
            if (entry.Source != source)
            {
                throw new DeclaradException($"The source of module '{key}' changed from {entry.Source} to {source}. Run 'declarad init' to install it.", pos);
            }
            return Path.GetFullPath(Path.Combine(RootDir, entry.Dir));
        }

        internal void ThrowIfErrors()
        {
            if (Errors.Count > 0)
            {
                throw new ConfigErrorsException(Errors.ToList());
            }
        }

        /// <summary>Records an error once; module instances share files, so the same problem would repeat per instance.</summary>
        internal void Error(string message, SourcePos pos)
        {
            if (!Errors.Any(e => e.Message == message && e.Pos == pos))
            {
                Errors.Add(new DeclaradException(message, pos));
            }
        }

        internal void AddError(DeclaradException ex, SourcePos fallback, string? prefix = null)
        {
            if (ex is ConfigErrorsException many)
            {
                Errors.AddRange(many.Errors);
                return;
            }
            string message = prefix != null && !ex.Message.StartsWith(prefix, StringComparison.Ordinal) ? $"{prefix}: {ex.Message}" : ex.Message;
            if (!Errors.Any(e => e.Message == message && e.Pos == (ex.Pos.Line > 0 ? ex.Pos : fallback)))
            {
                Errors.Add(new DeclaradException(message, ex.Pos.Line > 0 ? ex.Pos : fallback));
            }
        }

        // ---- Dependency tracking ---------------------------------------------------------------------

        internal void RecordDependency(string address)
        {
            if (Frames.Count > 0)
            {
                Frames.Peek().Deps.Add(address);
            }
        }

        /// <summary>Runs <paramref name="compute"/> in a collector frame; the dependencies it saw are returned and passed to the caller.</summary>
        internal Value Collect(Func<Value> compute, out HashSet<string> deps, Instance? instance = null)
        {
            var frame = new Frame { Collector = true, Instance = instance };
            Frames.Push(frame);
            try
            {
                return compute();
            }
            finally
            {
                Frames.Pop();
                deps = frame.Deps;
                Replay(deps);
            }
        }

        internal void Replay(IEnumerable<string> deps)
        {
            if (Frames.Count > 0)
            {
                Frames.Peek().Deps.UnionWith(deps);
            }
        }

        /// <summary>
        /// Recorded like a dependency when a sensitive variable is read, so that everything computed from it
        /// (attributes, locals, outputs) knows it is sensitive. Never an address.
        /// </summary>
        internal const string SensitiveMarker = "\u0000sensitive";

        internal static IEnumerable<string> Addresses(IEnumerable<string> deps) => deps.Where(d => d != SensitiveMarker);

        /// <summary>
        /// The innermost instance being evaluated (for each/count). A collector frame without an instance (a local,
        /// an output, a for_each expression) is a barrier: those are shared by all instances and must not see one.
        /// </summary>
        internal Instance? CurrentInstance
        {
            get
            {
                foreach (var frame in Frames)
                {
                    if (frame.Instance != null)
                    {
                        return frame.Instance;
                    }
                    if (frame.Collector)
                    {
                        return null;
                    }
                }
                return null;
            }
        }

        // ---- Build -----------------------------------------------------------------------------------

        internal readonly Dictionary<string, ConnectionConfig> Connections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IDirectory> _directories = new(StringComparer.Ordinal);
        private Configuration? _config;

        internal SchemaContext Schema(string connection) =>
            Connections.TryGetValue(connection, out var c) ? c.Schema : new SchemaContext("", "");

        /// <summary>The directory of a connection, opened on first use; null when loading offline.</summary>
        internal IDirectory? Directory(string connection)
        {
            if (_directories.TryGetValue(connection, out var existing))
            {
                return existing;
            }
            if (Options.Connect == null || !Connections.TryGetValue(connection, out var config))
            {
                return null;
            }
            var directory = Options.Connect(config.Resolver(Options.GetEnv ?? Environment.GetEnvironmentVariable, Options.Prompt));
            _directories[connection] = directory;
            _config?.OpenDirectories.TryAdd(connection, directory);
            return directory;
        }

        private Configuration Build(List<Body> bodies)
        {
            var settings = ProjectSettings.FromBodies(bodies, Functions);
            settings.CheckVersion();

            var root = new ModuleScope(this, RootDir, "", null, null, 0);
            root.Register(bodies);
            ThrowIfErrors();

            if (root.ConnectionBlocks.Count == 0)
            {
                throw new DeclaradException("No connection block found. Add: connection { server = \"dc01.corp.local\" domain = \"corp.local\" }");
            }
            foreach (var (name, block) in root.ConnectionBlocks)
            {
                var (domain, baseDn) = ResolveDomain(root, block);
                string? server = null;
                string forestBaseDn = baseDn;
                try
                {
                    server = root.EvalOptional(block.Body.GetAttribute("server")) is { } sv ? V.Str(sv) : (Options.GetEnv ?? Environment.GetEnvironmentVariable)("DECLARAD_SERVER");
                    forestBaseDn = root.EvalOptional(block.Body.GetAttribute("forest_base_dn")) is { } f ? V.Str(f)! : baseDn;
                }
                catch (DeclaradException ex)
                {
                    AddError(ex, block.Pos);
                }
                Connections[name] = new ConnectionConfig
                {
                    Name = name,
                    Domain = domain,
                    BaseDn = baseDn,
                    Server = server,
                    ForestBaseDn = forestBaseDn,
                    Pos = block.Pos,
                    Resolver = (env, prompt) => ResolveConnection(root, block, domain, baseDn, env, prompt),
                };
            }
            ThrowIfErrors();

            _config = new Configuration
            {
                Directory = RootDir,
                Connections = Connections,
                Settings = settings,
            };
            foreach (var (name, directory) in _directories)
            {
                _config.OpenDirectories[name] = directory;
            }

            root.EvaluateAll(_config);
            ThrowIfErrors();
            ValidateUniqueness(_config.Resources);
            ThrowIfErrors();
            _config.Warnings.AddRange(Warnings.Distinct());
            return _config;
        }

        private (string Domain, string BaseDn) ResolveDomain(ModuleScope root, Block connection)
        {
            try
            {
                string? domain = root.EvalOptional(connection.Body.GetAttribute("domain")) is { } d ? V.Str(d) : null;
                string? baseDn = root.EvalOptional(connection.Body.GetAttribute("base_dn")) is { } b ? V.Str(b) : null;
                if (baseDn != null && !Dn.IsValid(baseDn))
                {
                    Error($"base_dn '{baseDn}' is not a valid distinguished name.", connection.Pos);
                    return ("", "");
                }
                domain = domain?.Trim().TrimEnd('.');
                if (domain?.Length == 0)
                {
                    Error("The connection's domain must not be empty.", connection.Pos);
                    return ("", "");
                }
                domain ??= baseDn != null ? Dn.ToDomain(baseDn) : null;
                if (domain == null)
                {
                    Error("The connection block needs 'domain' (e.g. \"corp.local\").", connection.Pos);
                    return ("", "");
                }
                return (domain, baseDn ?? Dn.FromDomain(domain));
            }
            catch (DeclaradException ex)
            {
                AddError(ex, connection.Pos);
                return ("", "");
            }
        }

        private static ConnectionSettings ResolveConnection(ModuleScope root, Block connection, string domain, string baseDn, Func<string, string?> getEnv, Func<string, string?>? prompt)
        {
            Value? Eval(string name) => root.EvalOptional(connection.Body.GetAttribute(name));
            string? Str(string name) => Eval(name) is { } v ? V.Str(v) : null;

            string server = Str("server") ?? getEnv("DECLARAD_SERVER")
                ?? throw new DeclaradException("The connection block needs 'server'.", connection.Pos);
            string protocolText = (Str("protocol") ?? "ldaps").ToLowerInvariant();
            var protocol = protocolText switch
            {
                "ldap" => LdapProtocol.Ldap,
                "ldaps" => LdapProtocol.Ldaps,
                "starttls" => LdapProtocol.StartTls,
                _ => throw new DeclaradException($"Invalid protocol '{protocolText}'. Use ldaps, starttls or ldap.", connection.Pos),
            };
            string authText = (Str("auth") ?? "simple").ToLowerInvariant();
            var auth = authText switch
            {
                "simple" => LdapAuth.Simple,
                "negotiate" or "kerberos" => LdapAuth.Negotiate,
                _ => throw new DeclaradException($"Invalid auth '{authText}'. Use simple or negotiate.", connection.Pos),
            };

            string? username = Str("username") ?? getEnv("DECLARAD_USERNAME");
            string? password = Str("password") ?? getEnv("DECLARAD_PASSWORD");
            if (auth == LdapAuth.Simple)
            {
                username ??= prompt?.Invoke("Username: ") ?? throw new DeclaradException("No username: set username in the connection block or DECLARAD_USERNAME.", connection.Pos);
                password ??= prompt?.Invoke($"Password for {username}: ") ?? throw new DeclaradException("No password: set DECLARAD_PASSWORD or password = env(\"...\") in the connection block.", connection.Pos);
            }

            int? port = Eval("port") is { } p ? WholeNumber(p, "port", 1, 65535) : null;
            int timeout = Eval("timeout") is { } t ? WholeNumber(t, "timeout", 1, 3600) : 30;
            int WholeNumber(Value v, string what, int min, int max)
            {
                decimal n = Evaluator.ToNumber(v, connection.Pos);
                return decimal.Truncate(n) == n && n >= min && n <= max
                    ? (int)n
                    : throw new DeclaradException($"The connection's {what} must be a whole number between {min} and {max}.", connection.Pos);
            }
            bool insecure = Eval("insecure_skip_verify") is { } i && Evaluator.ToBool(i, connection.Pos);

            return new ConnectionSettings
            {
                Server = server,
                Port = port,
                Protocol = protocol,
                Auth = auth,
                Username = username,
                Password = password,
                Domain = domain,
                BaseDn = baseDn,
                InsecureSkipVerify = insecure,
                CaCertDir = Str("ca_cert_dir"),
                TimeoutSeconds = timeout,
            };
        }

        private void ValidateUniqueness(List<ResourceInstance> resources)
        {
            void Unique(IEnumerable<ResourceInstance> items, Func<ResourceInstance, string?> key, string what)
            {
                foreach (var group in items.Select(r => (r, k: key(r))).Where(x => !string.IsNullOrEmpty(x.k)).GroupBy(x => x.k!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                {
                    var list = group.ToList();
                    // Keys are prefixed with the connection name for grouping; don't show it.
                    Error($"{list[1].r.Address} has the same {what} as {list[0].r.Address}: '{group.Key.Split(':', 2)[^1]}'.", list[1].r.Pos);
                }
            }

            // Names are unique per connection (directory).
            var objects = resources.Where(r => r.Type is not AttachmentType && !r.HasUnknowns).ToList();
            Unique(objects, r => $"{r.Connection}:{Dn.Normalize(r.Dn)}", "distinguished name");
            // Users, groups, computers and service accounts share the sAMAccountName namespace.
            Unique(objects.Where(r => r.Type.Find("sam_account_name") != null), r => V.Str(r.Get("sam_account_name")) is { } sam ? $"{r.Connection}:{sam}" : null, "sam_account_name");
            Unique(objects.Where(r => r.Type is UserResource), r => V.Str(r.Get("user_principal_name")) is { } upn ? $"{r.Connection}:{upn}" : null, "user_principal_name");
            Unique(resources.Where(r => r.Type is GpoLinkResource && !r.HasUnknowns), r => $"{r.Connection}:{V.Str(r.Get("gpo"))} on {Dn.Normalize(r.Dn)}", "GPO link");
            Unique(resources.Where(r => r.Type is DnsRecordResource && !r.HasUnknowns), r => $"{r.Connection}:{V.Str(r.Get("type"))} {Dn.Normalize(r.Dn)}", "DNS record set");

            var records = resources.Where(r => r.Type is DnsRecordResource && !r.HasUnknowns).ToList();
            foreach (var cname in records.Where(r => V.Str(r.Get("type")) == "CNAME"))
            {
                if (records.FirstOrDefault(o => o != cname && o.Connection == cname.Connection && Dn.AreEqual(o.Dn, cname.Dn)) is { } other)
                {
                    Error($"{cname.Address}: a CNAME cannot share its name with other records ({other.Address}).", cname.Pos);
                }
            }
            var zones = resources.Where(r => r.Type is DnsZoneResource && !r.HasUnknowns).ToList();
            foreach (var ns in records.Where(r => V.Str(r.Get("type")) == "NS" && V.Str(r.Get("name")) == "@"))
            {
                if (zones.FirstOrDefault(z => z.Connection == ns.Connection && Dn.AreEqual(Dn.Parent(ns.Dn), z.Dn)) is { } zone)
                {
                    Error($"{ns.Address}: the zone's apex NS records are managed by {zone.Address}.name_servers.", ns.Pos);
                }
            }
        }

        internal static string Suggest(string name, IEnumerable<string> candidates)
        {
            var best = candidates
                .Select(c => (c, d: Levenshtein(name.ToLowerInvariant(), c)))
                .Where(x => x.d <= Math.Max(2, name.Length / 3))
                .OrderBy(x => x.d)
                .FirstOrDefault();
            return best.c != null ? $" (did you mean '{best.c}'?)" : "";
        }

        private static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++)
            {
                d[i, 0] = i;
            }
            for (int j = 0; j <= b.Length; j++)
            {
                d[0, j] = j;
            }
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                }
            }
            return d[a.Length, b.Length];
        }
    }
}
