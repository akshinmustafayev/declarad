using System.Globalization;
using System.Text;
using System.Text.Json;
using Cocona;
using Declarad.Config;
using Declarad.Config.Modules;
using Declarad.Directory;
using Declarad.Engine;
using Declarad.Export;
using Declarad.Hcl;
using Declarad.Hcl.Values;
using Declarad.Resources;
using Declarad.Resources.DataSources;
using Declarad.Resources.Schema;
using Declarad.State;
using Declarad.State.Backends;
using Microsoft.Extensions.Logging;

namespace Declarad.Cli
{
    [HasSubCommands(typeof(StateCommands), "state", Description = "Inspect or modify the state")]
    public sealed class Commands
    {
        internal const int ExitOk = 0;
        internal const int ExitError = 1;
        internal const int ExitChanges = 2;
        internal const int ExitNeedsFormatting = 3;

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private readonly ConsoleUi _ui = new();

        public Commands(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger("declarad");
        }

        [Command("init", Description = "Prepare the working directory: install modules, configure the state backend (and create main.dad if missing)")]
        public int Init(
            GlobalOptions g,
            [Option("migrate-state", Description = "Copy existing state to a newly configured backend")] bool migrateState = false,
            [Option(Description = "Use the new backend without migrating existing state")] bool reconfigure = false,
            [Option("force-copy", Description = "With --migrate-state, overwrite state already in the new backend")] bool forceCopy = false,
            [Option(Description = "Download the latest version of remote modules")] bool upgrade = false)
        {
            return Run(g, () =>
            {
                System.IO.Directory.CreateDirectory(g.ConfigDir);
                if (ConfigLoader.FindFiles(g.ConfigDir).Count == 0)
                {
                    string path = Path.Combine(g.ConfigDir, "main" + ConfigLoader.FileExtension);
                    File.WriteAllText(path, Templates.Main);
                    _ui.WriteLine(_ui.Green($"Created {path}") + " — edit the connection block, then run declarad plan.");
                }

                var settings = ProjectSettings.Read(g.ConfigDir);
                settings.CheckVersion();

                int modules = new ModuleInstaller(g.ConfigDir, upgrade, message => _ui.WriteLine(message)).Install();
                if (modules > 0)
                {
                    _ui.WriteLine($"Modules: {modules} module call(s) ready.");
                }

                var desired = ConfiguredBackend(g, settings);
                var previous = ProjectSettings.ReadInitializedBackend(g.ConfigDir);
                if (previous == null && File.Exists(Path.Combine(g.ConfigDir, StateStore.DefaultFileName)))
                {
                    previous = Backends.LocalDefault;
                }
                var target = BackendFactory.Create(desired, g.ConfigDir, Environment.GetEnvironmentVariable);
                var current = target.Read();

                if (previous != null && previous.Hash() != desired.Hash() && !reconfigure)
                {
                    var source = BackendFactory.Create(previous, g.ConfigDir, Environment.GetEnvironmentVariable);
                    var old = source.Read();
                    if (old != null && (old.Resources.Count > 0 || old.Outputs.Count > 0))
                    {
                        if (!migrateState)
                        {
                            throw new DeclaradException(
                                $"The backend changed from {previous.Type} ({source.Location}) to {desired.Type} ({target.Location}), and the old backend has state.\n" +
                                "Run 'declarad init --migrate-state' to copy it, or 'declarad init --reconfigure' to start without it.");
                        }
                        if (current != null && current.Resources.Count > 0 && !forceCopy)
                        {
                            throw new DeclaradException($"{target.Location} already contains state ({current.Resources.Count} resources). Use --force-copy to overwrite it.");
                        }
                        var info = new LockInfo { Operation = "migrate", Path = target.Location };
                        bool locked = target.Lock(info);
                        try
                        {
                            target.Write(old);
                        }
                        finally
                        {
                            if (locked)
                            {
                                target.Unlock(info.Id);
                            }
                        }
                        current = target.Read();
                        _ui.WriteLine(_ui.Green($"Copied state ({old.Resources.Count} resources) from {source.Location} to {target.Location}."));
                        if (source is LocalBackend local && File.Exists(local.Path))
                        {
                            // Keep a copy, but don't leave live-looking state behind that could later be used by mistake.
                            File.Move(local.Path, local.Path + ".migrated", overwrite: true);
                            _ui.WriteLine($"The old state file was renamed to {local.Path}.migrated.");
                        }
                    }
                }

                ProjectSettings.WriteInitializedBackend(g.ConfigDir, desired, (settings.Backend ?? Backends.LocalDefault).Hash());
                _ui.WriteLine($"Backend: {desired.Type} ({target.Location}){(current != null ? $", {current.Resources.Count} resources in state" : ", no state yet")}.");
                _ui.WriteLine(_ui.Green(_ui.Bold("declarad has been successfully initialized!")));
                return ExitOk;
            });
        }

        [Command("validate", Description = "Check the configuration for errors without contacting the directory")]
        public int Validate(GlobalOptions g)
        {
            return Run(g, () =>
            {
                var config = LoadConfig(g, online: false);
                foreach (var warning in config.Warnings)
                {
                    _ui.Warning(warning);
                }
                _ui.WriteLine(_ui.Green(_ui.Bold("Success!")) + $" The configuration is valid ({config.Resources.Count} {(config.Resources.Count == 1 ? "resource" : "resources")}).");
                return ExitOk;
            });
        }

        [Command("fmt", Description = "Rewrite configuration files in the canonical format")]
        public int Fmt(
            GlobalOptions g,
            [Argument(Description = "Files or directories (default: the configuration directory)")] string[]? paths = null,
            [Option(Description = "Only check; exit 3 if any file needs formatting")] bool check = false,
            [Option(Description = "Show the changes")] bool diff = false,
            [Option('r', Description = "Also process subdirectories (e.g. modules)")] bool recursive = false)
        {
            return Run(g, () =>
            {
                var files = new List<string>();
                foreach (string path in (paths is { Length: > 0 } ? paths : new[] { "." }).Select(g.Resolve))
                {
                    if (File.Exists(path))
                    {
                        files.Add(Path.GetFullPath(path));
                    }
                    else if (System.IO.Directory.Exists(path))
                    {
                        files.AddRange(System.IO.Directory.GetFiles(path, "*" + ConfigLoader.FileExtension, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ProjectSettings.WorkDirName + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
                    }
                    else
                    {
                        throw new DeclaradException($"'{path}' does not exist.");
                    }
                }

                int changed = 0;
                var errors = new List<DeclaradException>();
                foreach (string file in files.Order(StringComparer.Ordinal))
                {
                    string original = File.ReadAllText(file);
                    string relative = Path.GetRelativePath(g.ConfigDir, file);
                    string display = relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
                    string formatted;
                    try
                    {
                        formatted = Formatter.Format(original, display);
                    }
                    catch (DeclaradException ex)
                    {
                        errors.Add(ex);
                        continue;
                    }
                    if (formatted == original)
                    {
                        continue;
                    }
                    changed++;
                    _ui.WriteLine(display);
                    if (diff)
                    {
                        foreach (string line in Formatter.Diff(original, formatted))
                        {
                            _ui.WriteLine("  " + (line.StartsWith('+') ? _ui.Green(line) : _ui.Red(line)));
                        }
                    }
                    if (!check)
                    {
                        File.WriteAllText(file, formatted);
                    }
                }
                if (errors.Count > 0)
                {
                    throw new ConfigErrorsException(errors);
                }
                return check && changed > 0 ? ExitNeedsFormatting : ExitOk;
            });
        }

        [Command("plan", Description = "Show the changes needed to make the directory match the configuration")]
        public int Plan(
            GlobalOptions g,
            [Option(Description = "Take existing objects matching a new resource's DN under management")] bool adopt = false,
            [Option(Description = "Plan destroying every managed object")] bool destroy = false,
            [Option(Description = "Limit to these resources or modules and what they depend on (repeatable)")] string[]? target = null,
            [Option("detailed-exitcode", Description = "Exit 0 = no changes, 1 = error, 2 = changes present")] bool detailedExitCode = false)
        {
            return Run(g, () =>
            {
                var config = LoadConfig(g, online: true);
                var manager = OpenState(g, config.Settings);
                var state = manager.Load();
                using var pool = NewPool(config);
                var plan = BuildPlan(config, state, pool, adopt, destroy, target);
                var renderer = new PlanRenderer(_ui);
                Console.Out.Write(renderer.Render(plan));
                Console.Out.Write(renderer.RenderOutputs(config.Outputs, "Outputs"));
                return detailedExitCode && plan.HasChanges ? ExitChanges : ExitOk;
            });
        }

        [Command("apply", Description = "Apply the changes required to reach the configured state")]
        public int Apply(
            GlobalOptions g,
            [Option("auto-approve", Description = "Skip interactive approval")] bool autoApprove = false,
            [Option(Description = "Take existing objects matching a new resource's DN under management")] bool adopt = false,
            [Option(Description = "Limit to these resources or modules and what they depend on (repeatable)")] string[]? target = null)
        {
            return Run(g, () => ApplyInternal(g, autoApprove, adopt, destroy: false, target));
        }

        [Command("destroy", Description = "Delete every object managed by this configuration")]
        public int Destroy(
            GlobalOptions g,
            [Option("auto-approve", Description = "Skip interactive approval")] bool autoApprove = false,
            [Option(Description = "Limit to these resources or modules and what depends on them (repeatable)")] string[]? target = null)
        {
            return Run(g, () => ApplyInternal(g, autoApprove, adopt: false, destroy: true, target));
        }

        [Command("import", Description = "Take an existing directory object under management")]
        public int Import(
            GlobalOptions g,
            [Argument(Description = "Resource address from the configuration, e.g. user.jdoe or module.team.user.staff[\"jdoe\"]")] string address,
            [Argument(Description = "Distinguished name or objectGUID of the existing object")] string id)
        {
            return Run(g, () =>
            {
                var config = LoadConfig(g, online: true);
                var resource = config.Find(address)
                    ?? throw new DeclaradException($"No resource '{address}' in the configuration. Declare it first. Known: {string.Join(", ", config.Resources.Select(r => r.Address).Take(20))}");
                if (resource.Type is AttachmentType)
                {
                    throw new DeclaradException($"{address} is a {resource.Type.Name}; attachments need no import and are reconciled by apply.");
                }
                var manager = OpenState(g, config.Settings);
                using var stateLock = LockState(g, manager, "import");
                var state = manager.Load();
                if (state.Find(address) is { } existing)
                {
                    throw new DeclaradException($"{address} is already managed ({existing.Dn}). Use 'declarad state rm {address}' first to re-import.");
                }
                using var pool = NewPool(config);
                var directory = pool.Get(resource.Connection);
                var type = resource.Type;
                var obj = (Guid.TryParse(id, out var guid)
                        ? directory.GetByGuid(guid, type.LdapAttributes, type.NeedsSecurityDescriptor)
                        : directory.GetByDn(id, type.LdapAttributes, type.NeedsSecurityDescriptor))
                    ?? throw new DeclaradException($"Object '{id}' not found in the directory of connection '{resource.Connection}'.");
                if (!type.Matches(obj))
                {
                    throw new DeclaradException($"'{obj.Dn}' is a {obj.StructuralClass}, not a {type.ObjectClass}.");
                }
                if (state.Resources.FirstOrDefault(r => r.Guid == obj.Guid && r.Connection == resource.Connection) is { } other)
                {
                    throw new DeclaradException($"'{obj.Dn}' is already managed as {other.Address}.");
                }
                state.Upsert(new StateResource { Address = address, Type = type.Name, Connection = resource.Connection, Guid = obj.Guid, Dn = obj.Dn });
                manager.Save(state);
                _ui.WriteLine(_ui.Green($"Imported {obj.Dn} as {address}.") + " Run 'declarad plan' to see remaining differences.");
                return ExitOk;
            });
        }

        [Command("export", Description = "Generate configuration from existing directory objects")]
        public int Export(
            GlobalOptions g,
            [Option(Description = "Search base DN (default: domain root, or the DNS container for DNS types)")] string? @base = null,
            [Option('o', Description = "Write to this file instead of stdout")] string? output = null,
            [Option(Description = "Types to export (repeatable or comma-separated), e.g. ou,group,user,dns_zone,dns_record")] string[]? type = null,
            [Option(Description = "Connection to export from (default: default)")] string connection = StateFile.DefaultConnection,
            [Option("include-dynamic-dns", Description = "Also export DNS records registered dynamically by clients")] bool includeDynamicDns = false,
            [Option(Description = "Overwrite the output file")] bool force = false)
        {
            return Run(g, () =>
            {
                var types = (type == null || type.Length == 0)
                    ? Exporter.DefaultTypes.ToList()
                    : type.SelectMany(t => t.Split(',')).Select(t => ResourceRegistry.Find(t.Trim()) ?? throw new DeclaradException($"Unknown type '{t}'.")).ToList();
                var config = LoadConfig(g, online: true);
                using var pool = NewPool(config);
                string hcl = new Exporter(pool.Get(connection)) { IncludeDynamicDns = includeDynamicDns, Connection = connection }.Export(@base, types);
                if (output == null)
                {
                    Console.Out.Write(hcl);
                }
                else
                {
                    output = g.Resolve(output);
                    if (File.Exists(output) && !force)
                    {
                        throw new DeclaradException($"'{output}' exists. Use --force to overwrite it.");
                    }
                    File.WriteAllText(output, hcl);
                    _ui.WriteLine(_ui.Green($"Wrote {output}."));
                }
                return ExitOk;
            });
        }

        [Command("output", Description = "Show output values from the last apply")]
        public int Output(
            GlobalOptions g,
            [Argument(Description = "Output name (default: all)")] string? name = null,
            [Option(Description = "Print as JSON")] bool json = false)
        {
            return Run(g, () =>
            {
                var outputs = OpenState(g, ProjectSettings.Read(g.ConfigDir)).Load().Outputs;
                if (name != null)
                {
                    var value = outputs.TryGetValue(name, out var v) ? v : throw new DeclaradException($"No output '{name}' in the state (sensitive outputs are not stored).");
                    _ui.WriteLine(json ? value.ToJson()?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null" : value is StringValue s ? s.V : value.ToHcl());
                    return ExitOk;
                }
                if (json)
                {
                    var obj = new System.Text.Json.Nodes.JsonObject();
                    foreach (var (k, v) in outputs)
                    {
                        obj[k] = v.ToJson();
                    }
                    _ui.WriteLine(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return ExitOk;
                }
                foreach (var (k, v) in outputs)
                {
                    _ui.WriteLine($"{k} = {v.ToHcl()}");
                }
                return ExitOk;
            });
        }

        [Command("force-unlock", Description = "Release a stuck state lock")]
        public int ForceUnlock(GlobalOptions g, [Argument(Description = "Lock ID shown in the lock error")] string lockId, [Option(Description = "Do not ask for confirmation")] bool force = false)
        {
            return Run(g, () =>
            {
                var manager = OpenState(g, ProjectSettings.Read(g.ConfigDir));
                if (!force)
                {
                    if (!_ui.IsInteractive)
                    {
                        throw new DeclaradException("Input is not interactive; pass --force to unlock without confirmation.");
                    }
                    if (!_ui.Confirm($"Do you really want to force-unlock {manager.Backend.Location}? Only do this if no other run is in progress."))
                    {
                        _ui.WriteLine("Unlock cancelled.");
                        return ExitError;
                    }
                }
                manager.Backend.ForceUnlock(lockId);
                _ui.WriteLine(_ui.Green("The state has been unlocked."));
                return ExitOk;
            });
        }

        [Command("graph", Description = "Print the resource dependency graph in Graphviz DOT format")]
        public int Graph(GlobalOptions g)
        {
            return Run(g, () =>
            {
                var config = LoadConfig(g, online: false);
                var sb = new StringBuilder("digraph declarad {\n  rankdir = \"LR\";\n  node [shape = box];\n");
                var objects = config.Resources.Where(r => r.Type is not AttachmentType && !r.HasUnknowns).ToList();
                foreach (var r in config.Resources)
                {
                    sb.AppendLine($"  {HclWriter.Quote(r.Address)};");
                    var deps = new SortedSet<string>(r.DependsOn, StringComparer.Ordinal);
                    if (r.Type is not AttachmentType)
                    {
                        foreach (var parent in objects.Where(o => o != r && o.Connection == r.Connection && !r.HasUnknowns && Dn.IsDescendantOf(r.Dn, o.Dn)))
                        {
                            deps.Add(parent.Address);
                        }
                    }
                    foreach (var dep in deps)
                    {
                        sb.AppendLine($"  {HclWriter.Quote(r.Address)} -> {HclWriter.Quote(dep)};");
                    }
                }
                sb.AppendLine("}");
                Console.Out.Write(sb.ToString());
                return ExitOk;
            });
        }

        [Command("schema", Description = "Describe resource types, data sources, backends and functions")]
        public int Schema(
            GlobalOptions g,
            [Argument(Description = "A resource type or data source name")] string? name = null,
            [Option(Description = "Print the attribute table as Markdown")] bool markdown = false)
        {
            return Run(g, () =>
            {
                if (markdown && name != null)
                {
                    Console.Out.Write(ResourceRegistry.Find(name) is { } t ? SchemaDocs.Markdown(t)
                        : DataSourceRegistry.Find(name) is { } d ? SchemaDocs.Markdown(d)
                        : throw new DeclaradException($"Unknown resource type or data source '{name}'."));
                    return ExitOk;
                }
                if (name == null)
                {
                    _ui.WriteLine(_ui.Bold("Resource types:"));
                    foreach (var t in ResourceRegistry.All)
                    {
                        _ui.WriteLine($"  {t.Name,-18} {t.Description}");
                    }
                    _ui.WriteLine(_ui.Bold("\nData sources:"));
                    foreach (var d in DataSourceRegistry.All)
                    {
                        _ui.WriteLine($"  {d.Name,-18} {d.Description}");
                    }
                    _ui.WriteLine(_ui.Bold("\nState backends:"));
                    foreach (var (backend, settings) in BackendFactory.KnownSettings)
                    {
                        _ui.WriteLine($"  {backend,-18} {string.Join(", ", settings)}");
                    }
                    _ui.WriteLine(_ui.Bold("\nFunctions:"));
                    _ui.WriteLine("  " + string.Join(", ", new Functions(".").Names.Append("try").Append("can").Order(StringComparer.Ordinal)));
                    _ui.WriteLine("\nRun 'declarad schema <type>' for attributes.");
                    return ExitOk;
                }
                if (ResourceRegistry.Find(name) is { } type)
                {
                    _ui.WriteLine(_ui.Bold($"{type.Name}") + $" — {type.Description}" + (type is AttachmentType ? " (attachment)" : $" (objectClass {type.ObjectClass})"));
                    foreach (var spec in type.Schema)
                    {
                        _ui.WriteLine(DescribeSpec(spec));
                    }
                    _ui.WriteLine($"  {"connection",-36} (meta) connection.<name> to manage the object in another directory.");
                    if (type is not AttachmentType)
                    {
                        _ui.WriteLine($"  {"dn",-36} (computed) Distinguished name.");
                    }
                    return ExitOk;
                }
                if (DataSourceRegistry.Find(name) is { } data)
                {
                    _ui.WriteLine(_ui.Bold($"data \"{data.Name}\"") + $" — {data.Description}");
                    foreach (var spec in data.Arguments)
                    {
                        _ui.WriteLine(DescribeSpec(spec));
                    }
                    return ExitOk;
                }
                throw new DeclaradException($"Unknown resource type or data source '{name}'.");
            });
        }

        private static string DescribeSpec(AttributeSpec spec)
        {
            var flags = new List<string> { spec.Type switch { AttrType.StringList => "list(string)", _ => spec.Type.ToString().ToLowerInvariant() } };
            if (spec.Required)
            {
                flags.Add("required");
            }
            if (spec.Default != null)
            {
                flags.Add($"default {spec.Default.ToHcl()}");
            }
            if (spec.CreateOnly)
            {
                flags.Add("create-only");
            }
            if (spec.Immutable)
            {
                flags.Add("immutable");
            }
            if (spec.Sensitive)
            {
                flags.Add("sensitive");
            }
            if (spec.Additive)
            {
                flags.Add("additive");
            }
            if (spec.AllowedValues != null)
            {
                flags.Add("one of " + string.Join("|", spec.AllowedValues));
            }
            return $"  {spec.Name,-36} ({string.Join(", ", flags)}) {spec.Description}";
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        private int ApplyInternal(GlobalOptions g, bool autoApprove, bool adopt, bool destroy, string[]? targets)
        {
            var config = LoadConfig(g, online: true);
            var manager = OpenState(g, config.Settings);
            using var stateLock = LockState(g, manager, destroy ? "destroy" : "apply");
            var state = manager.Load();
            using var pool = NewPool(config);
            var plan = BuildPlan(config, state, pool, adopt, destroy, targets);
            var renderer = new PlanRenderer(_ui);
            Console.Out.Write(renderer.Render(plan));
            if (!plan.HasChanges)
            {
                SaveOutputs(manager, state, config, destroy, targets);
                Console.Out.Write(renderer.RenderOutputs(config.Outputs, "Outputs"));
                return ExitOk;
            }

            if (!autoApprove)
            {
                if (!_ui.IsInteractive)
                {
                    throw new DeclaradException("Input is not interactive; pass --auto-approve to apply without confirmation.");
                }
                string question = destroy ? "Do you really want to destroy all managed objects?" : "Do you want to perform these actions?";
                if (!_ui.Confirm(question))
                {
                    _ui.WriteLine("\nApply cancelled.");
                    return ExitError;
                }
            }
            _ui.WriteLine();

            var applier = new Applier(pool.Get, state, manager.Save, _logger,
                (address, message) => _ui.WriteLine($"{_ui.Bold(address)}: {message}"));
            var result = applier.Apply(plan);
            _ui.WriteLine();
            if (!result.Success)
            {
                foreach (var error in result.Errors)
                {
                    _ui.Error(error);
                }
                _ui.WriteLine($"Partially applied: {result.Created} added, {result.Updated} changed, {result.Deleted} destroyed. State has been saved; fix the error and run apply again.");
                return ExitError;
            }
            SaveOutputs(manager, state, config, destroy, targets);
            _ui.WriteLine(_ui.Green(_ui.Bold($"Apply complete! Resources: {result.Created} added, {result.Updated} changed, {result.Deleted} destroyed.")));
            Console.Out.Write(renderer.RenderOutputs(destroy ? new List<OutputValue>() : config.Outputs, "Outputs"));
            return ExitOk;
        }

        private static void SaveOutputs(StateManager manager, StateFile state, Configuration config, bool destroy, string[]? targets)
        {
            if (targets is { Length: > 0 })
            {
                return;
            }
            var outputs = destroy
                ? new Dictionary<string, Value>()
                : config.Outputs.Where(o => !o.Sensitive && !o.Value.ContainsUnknown()).ToDictionary(o => o.Name, o => o.Value);
            if (outputs.Count == state.Outputs.Count && outputs.All(o => state.Outputs.TryGetValue(o.Key, out var v) && v.Equals(o.Value)))
            {
                return;
            }
            state.Outputs.Clear();
            foreach (var (k, v) in outputs)
            {
                state.Outputs[k] = v;
            }
            manager.Save(state);
        }

        private Plan BuildPlan(Configuration config, StateFile state, DirectoryPool pool, bool adopt, bool destroy, string[]? targets)
        {
            // Open every connection the plan touches before checking encryption.
            foreach (string connection in config.Resources.Select(r => r.Connection).Concat(state.Resources.Select(s => s.Connection)).Distinct())
            {
                pool.Get(connection);
            }
            var options = new PlanOptions
            {
                Adopt = adopt,
                Destroy = destroy,
                Encrypted = pool.Encrypted,
                Targets = targets ?? Array.Empty<string>(),
            };
            var plan = new Planner(pool.Get, config, state, options).Build();
            foreach (var warning in plan.Warnings)
            {
                _ui.Warning(warning);
            }
            if (options.Targets.Count > 0)
            {
                _ui.Warning("--target applies only part of the configuration, which can leave it inconsistent. Run a full plan afterwards.");
            }
            return plan;
        }

        private Configuration LoadConfig(GlobalOptions g, bool online)
        {
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string v in g.Vars)
            {
                int eq = v.IndexOf('=');
                if (eq <= 0)
                {
                    throw new DeclaradException($"Invalid --var '{v}'; expected name=value.");
                }
                vars[v[..eq]] = v[(eq + 1)..];
            }
            return ConfigLoader.Load(g.ConfigDir, new LoadOptions
            {
                Vars = vars,
                Connect = online ? ConnectDirectory : null,
                Prompt = Prompt,
            });
        }

        private static class Backends
        {
            public static readonly BackendConfig LocalDefault = new("local", new Dictionary<string, string> { ["path"] = StateStore.DefaultFileName });
        }

        /// <summary>The backend the configuration asks for, with --backend-config overrides (or --state).</summary>
        private static BackendConfig ConfiguredBackend(GlobalOptions g, ProjectSettings settings)
        {
            if (g.State != null)
            {
                return new BackendConfig("local", new Dictionary<string, string> { ["path"] = g.Resolve(g.State) });
            }
            var config = settings.Backend ?? Backends.LocalDefault;
            return ProjectSettings.ApplyOverrides(config, g.BackendConfig, g.ConfigDir, Environment.GetEnvironmentVariable);
        }

        /// <summary>Opens the configured backend, insisting that 'declarad init' has been run for it.</summary>
        internal static StateManager OpenState(GlobalOptions g, ProjectSettings settings)
        {
            var desired = ConfiguredBackend(g, settings);
            if (g.State == null)
            {
                var initialized = ProjectSettings.ReadInitialized(g.ConfigDir);
                var block = settings.Backend ?? Backends.LocalDefault;
                // Like Terraform, settings given to 'init --backend-config' are remembered while the block is unchanged.
                if (g.BackendConfig.Length == 0 && initialized?.BlockHash == block.Hash())
                {
                    desired = initialized.Value.Config;
                }
                if (desired.Type != "local" && initialized == null)
                {
                    throw new DeclaradException($"The {desired.Type} backend is not initialized. Run 'declarad init'.");
                }
                if (initialized != null && initialized.Value.Config.Hash() != desired.Hash())
                {
                    throw new DeclaradException("The backend configuration changed since 'declarad init'. Run 'declarad init --migrate-state' to move the state, or 'declarad init --reconfigure'.");
                }
            }
            return new StateManager(BackendFactory.Create(desired, g.ConfigDir, Environment.GetEnvironmentVariable));
        }

        internal static IDisposable LockState(GlobalOptions g, StateManager manager, string operation)
        {
            if (!g.Lock)
            {
                return new NoLock();
            }
            return manager.Lock(operation, ParseDuration(g.LockTimeout));
        }

        private sealed class NoLock : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private static TimeSpan ParseDuration(string text)
        {
            var duration = ParseDurationUnchecked(text.Trim().ToLowerInvariant());
            return duration >= TimeSpan.Zero && duration <= TimeSpan.FromHours(24)
                ? duration
                : throw new DeclaradException($"--lock-timeout '{text}' must be between 0s and 24h.");
        }

        private static TimeSpan ParseDurationUnchecked(string text)
        {
            if (text.Contains("nan") || text.Contains("inf"))
            {
                throw new DeclaradException($"Invalid --lock-timeout '{text}'; use e.g. 30s or 5m.");
            }
            if (text.Length > 1 && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                if (Math.Abs(n) > 1e6)
                {
                    throw new DeclaradException($"--lock-timeout '{text}' must be between 0s and 24h.");
                }
                return text[^1] switch
                {
                    's' => TimeSpan.FromSeconds(n),
                    'm' => TimeSpan.FromMinutes(n),
                    'h' => TimeSpan.FromHours(n),
                    _ => throw new DeclaradException($"Invalid --lock-timeout '{text}'; use e.g. 30s or 5m."),
                };
            }
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && Math.Abs(seconds) <= 1e6
                ? TimeSpan.FromSeconds(seconds)
                : throw new DeclaradException($"Invalid --lock-timeout '{text}'; use e.g. 30s or 5m.");
        }

        private string? Prompt(string prompt) => _ui.Prompt(prompt, secret: prompt.StartsWith("Password", StringComparison.Ordinal));

        private IDirectory ConnectDirectory(ConnectionSettings settings)
        {
            var directory = new LdapDirectory(settings, _loggerFactory.CreateLogger<LdapDirectory>());
            directory.Connect();
            return directory;
        }

        private DirectoryPool NewPool(Configuration config) => new(config, ConnectDirectory, s => Prompt(s));

        internal int Run(GlobalOptions g, Func<int> body)
        {
            if (g.NoColor)
            {
                _ui.DisableColor();
            }
            try
            {
                return body();
            }
            catch (ConfigErrorsException ex)
            {
                foreach (var error in ex.Errors)
                {
                    _ui.Error(error.Describe());
                }
                return ExitError;
            }
            catch (DeclaradException ex)
            {
                _ui.Error(ex.Describe());
                _logger.LogDebug(ex, "Details");
                return ExitError;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ui.Error(ex.Message);
                _logger.LogDebug(ex, "Details");
                return ExitError;
            }
        }
    }
}
