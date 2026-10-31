using System.Text.Json;
using System.Text.Json.Nodes;
using Declarad.Hcl;
using Declarad.State.Backends;

namespace Declarad.Config
{
    /// <summary>
    /// The root module's <c>declarad { required_version = "...", backend "s3" { ... } }</c> block. Like Terraform's
    /// terraform block, it may not reference variables or resources; only functions such as env() are available.
    /// </summary>
    public sealed record ProjectSettings(string? RequiredVersion, BackendConfig? Backend)
    {
        public const string WorkDirName = ".declarad";

        public static string CurrentVersion => typeof(ProjectSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public static ProjectSettings Read(string rootDir, Func<string, string?>? getEnv = null)
        {
            var bodies = ConfigLoader.FindFiles(rootDir).Select(f => Parser.ParseFile(File.ReadAllText(f), Path.GetFileName(f)));
            return FromBodies(bodies, new Functions(rootDir, getEnv));
        }

        public static ProjectSettings FromBodies(IEnumerable<Body> bodies, Functions functions)
        {
            var blocks = bodies.SelectMany(b => b.Blocks).Where(b => b.Type == "declarad").ToList();
            if (blocks.Count > 1)
            {
                throw new DeclaradException("Only one declarad block is allowed.", blocks[1].Pos);
            }
            if (blocks.Count == 0)
            {
                return new ProjectSettings(null, null);
            }
            var block = blocks[0];
            var evaluator = new Evaluator(NoReferences.Instance, functions);
            foreach (var attr in block.Body.Attributes.Where(a => a.Name != "required_version"))
            {
                throw new DeclaradException($"Unknown declarad setting '{attr.Name}'. Expected required_version or a backend block.", attr.Pos);
            }
            string? version = block.Body.GetAttribute("required_version") is { } rv ? Evaluator.ToStringValue(evaluator.Evaluate(rv.Expr), rv.Pos, "required_version") : null;

            BackendConfig? backend = null;
            foreach (var nested in block.Body.Blocks)
            {
                if (nested.Type != "backend" || nested.Labels.Count != 1)
                {
                    throw new DeclaradException("Expected backend \"<type>\" { ... } inside the declarad block.", nested.Pos);
                }
                if (backend != null)
                {
                    throw new DeclaradException("Only one backend block is allowed.", nested.Pos);
                }
                var settings = new Dictionary<string, string>(StringComparer.Ordinal);
                Flatten(nested.Body, "", settings, evaluator);
                backend = new BackendConfig(nested.Labels[0], settings);
            }
            return new ProjectSettings(version, backend);
        }

        private static void Flatten(Body body, string prefix, Dictionary<string, string> settings, Evaluator evaluator)
        {
            foreach (var attr in body.Attributes)
            {
                var value = evaluator.Evaluate(attr.Expr);
                if (!value.IsNull)
                {
                    settings[prefix + attr.Name] = Evaluator.ToStringValue(value, attr.Pos, $"backend setting '{attr.Name}'");
                }
            }
            foreach (var nested in body.Blocks)
            {
                Flatten(nested.Body, prefix + nested.Type + ".", settings, evaluator);
            }
        }

        /// <summary>Applies --backend-config overrides: "key=value" pairs or files of attributes.</summary>
        public static BackendConfig ApplyOverrides(BackendConfig config, IEnumerable<string> overrides, string rootDir, Func<string, string?> getEnv)
        {
            var settings = new Dictionary<string, string>(config.Settings, StringComparer.Ordinal);
            foreach (string item in overrides)
            {
                int eq = item.IndexOf('=');
                if (eq >= 0)
                {
                    string key = item[..eq].Trim();
                    if (key.Length == 0)
                    {
                        throw new DeclaradException($"--backend-config '{item}' has no setting name.");
                    }
                    settings[key] = item[(eq + 1)..];
                    continue;
                }
                string path = Path.Combine(rootDir, item);
                if (!File.Exists(path))
                {
                    throw new DeclaradException($"--backend-config '{item}' is neither key=value nor an existing file.");
                }
                Flatten(Parser.ParseFile(File.ReadAllText(path), item), "", settings, new Evaluator(NoReferences.Instance, new Functions(rootDir, getEnv)));
            }
            return config with { Settings = settings };
        }

        public void CheckVersion()
        {
            if (RequiredVersion != null && !VersionConstraint.Satisfied(RequiredVersion, CurrentVersion))
            {
                throw new DeclaradException($"This configuration requires declarad {RequiredVersion}, but this is {CurrentVersion}.");
            }
        }

        // ---- .declarad/backend.json: the backend the working directory was initialized with ----------

        private static string BackendFile(string rootDir) => Path.Combine(rootDir, WorkDirName, "backend.json");

        /// <summary>The initialized backend (with --backend-config values merged) and the hash of the block alone.</summary>
        public static (BackendConfig Config, string? BlockHash)? ReadInitialized(string rootDir)
        {
            var config = ReadInitializedBackend(rootDir);
            if (config == null)
            {
                return null;
            }
            var node = JsonNode.Parse(File.ReadAllText(BackendFile(rootDir)));
            return (config, node?["block_hash"]?.GetValue<string>());
        }

        public static BackendConfig? ReadInitializedBackend(string rootDir)
        {
            string path = BackendFile(rootDir);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return BackendConfig.FromJson(JsonNode.Parse(File.ReadAllText(path))!);
            }
            catch (Exception ex) when (ex is JsonException or NullReferenceException or InvalidOperationException)
            {
                throw new DeclaradException($"{path} is corrupt ({ex.Message}). Run 'declarad init --reconfigure'.");
            }
        }

        /// <param name="blockHash">Hash of the backend block from the files, without command-line overrides.</param>
        public static void WriteInitializedBackend(string rootDir, BackendConfig config, string? blockHash = null)
        {
            string path = BackendFile(rootDir);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = config.ToJson();
            if (blockHash != null)
            {
                json["block_hash"] = blockHash;
            }
            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Terraform-style version constraints: "&gt;= 0.2, &lt; 1.0", "~&gt; 0.2", "= 0.2.1", "!= 0.3".</summary>
    public static class VersionConstraint
    {
        public static bool Satisfied(string constraints, string version)
        {
            var v = Parse(version);
            foreach (string part in constraints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string op = new string(part.TakeWhile(c => "<>=!~".Contains(c)).ToArray());
                string text = part[op.Length..].Trim();
                var target = Parse(text);
                int cmp = Compare(v, target);
                bool ok = op switch
                {
                    "" or "=" => cmp == 0,
                    "!=" => cmp != 0,
                    ">" => cmp > 0,
                    ">=" => cmp >= 0,
                    "<" => cmp < 0,
                    "<=" => cmp <= 0,
                    // ~> 1.2 allows >= 1.2, < 2.0; ~> 1.2.3 allows >= 1.2.3, < 1.3.0.
                    "~>" => cmp >= 0 && Compare(v, Bump(target, text.Count(c => c == '.'))) < 0,
                    _ => throw new DeclaradException($"Invalid version constraint operator '{op}' in \"{constraints}\"."),
                };
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        private static int[] Parse(string text)
        {
            var parts = text.Split('-')[0].Split('.');
            var result = new int[3];
            for (int i = 0; i < Math.Min(3, parts.Length); i++)
            {
                if (!int.TryParse(parts[i], out result[i]))
                {
                    throw new DeclaradException($"Invalid version '{text}'.");
                }
            }
            return result;
        }

        private static int[] Bump(int[] v, int dots)
        {
            var bumped = new int[3];
            int index = Math.Max(0, dots - 1);
            Array.Copy(v, bumped, index);
            bumped[index] = v[index] + 1;
            return bumped;
        }

        private static int Compare(int[] a, int[] b)
        {
            for (int i = 0; i < 3; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i].CompareTo(b[i]);
                }
            }
            return 0;
        }
    }
}
