using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Declarad.Hcl;

namespace Declarad.Config
{
    public enum ModuleSourceKind
    {
        Local,
        Git,
        Archive,
    }

    /// <summary>
    /// A module source address, following Terraform's syntax:
    /// <c>./local/path</c>, <c>git::https://host/repo.git//subdir?ref=v1.2</c>, <c>git::ssh://git@host/repo.git</c>,
    /// <c>git@github.com:org/repo.git</c>, <c>github.com/org/repo//subdir?ref=main</c>, <c>bitbucket.org/org/repo</c>,
    /// and archives such as <c>https://host/module.zip</c> or <c>https://host/m?archive=tar.gz</c>.
    /// </summary>
    public sealed partial record ModuleSource(ModuleSourceKind Kind, string Url, string? Ref, string? Subdir, int? Depth, string? ArchiveType, string Original)
    {
        [GeneratedRegex(@"^[a-zA-Z0-9_-]+/[a-zA-Z0-9_-]+/[a-zA-Z0-9_-]+$")]
        private static partial Regex RegistryAddress();

        [GeneratedRegex(@"^[\w.-]+@[\w.-]+:")]
        private static partial Regex ScpLike();

        public static ModuleSource Parse(string source)
        {
            if (source.StartsWith("./", StringComparison.Ordinal) || source.StartsWith("../", StringComparison.Ordinal))
            {
                return new ModuleSource(ModuleSourceKind.Local, source, null, null, null, null, source);
            }
            if (RegistryAddress().IsMatch(source.Split("//")[0]))
            {
                throw new DeclaradException($"Module source '{source}' looks like a registry address; module registries are not supported. Use a git source, e.g. git::https://host/repo.git?ref=v1.0.");
            }
            foreach (string scheme in new[] { "hg::", "s3::", "gcs::", "mercurial::" })
            {
                if (source.StartsWith(scheme, StringComparison.Ordinal))
                {
                    throw new DeclaradException($"Module source '{source}' uses '{scheme}', which is not supported. Use git or an http(s) archive.");
                }
            }

            string rest = source;
            var kind = ModuleSourceKind.Archive;
            if (rest.StartsWith("git::", StringComparison.Ordinal))
            {
                kind = ModuleSourceKind.Git;
                rest = rest[5..];
            }
            else if (rest.StartsWith("github.com/", StringComparison.Ordinal) || rest.StartsWith("bitbucket.org/", StringComparison.Ordinal))
            {
                kind = ModuleSourceKind.Git;
                var (path, sub, q) = SplitAddress(rest);
                rest = "https://" + path + (path.EndsWith(".git", StringComparison.Ordinal) ? "" : ".git") + (sub != null ? "//" + sub : "") + (q != null ? "?" + q : "");
            }
            else if (ScpLike().IsMatch(rest))
            {
                kind = ModuleSourceKind.Git;
            }
            else if (!rest.StartsWith("https://", StringComparison.Ordinal) && !rest.StartsWith("http://", StringComparison.Ordinal))
            {
                throw new DeclaradException($"Module source '{source}' is not supported. Use a local path (./...), a git source (git::https://...) or an http(s) archive. Absolute paths are not allowed; use a relative path.");
            }

            var (url, subdir, query) = SplitAddress(rest);
            var pairs = (query ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToList();
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            var passThrough = new List<string>();
            foreach (var pair in pairs)
            {
                string key = pair[0];
                if (kind != ModuleSourceKind.Git && !url.EndsWith(".git", StringComparison.Ordinal) && key is not ("archive" or "ref" or "depth"))
                {
                    // Other query parameters of archive URLs (e.g. signed-URL tokens) belong to the download URL.
                    passThrough.Add(string.Join('=', pair));
                    continue;
                }
                if (!parameters.TryAdd(key, pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : ""))
                {
                    throw new DeclaradException($"Module source '{source}' repeats the parameter '{key}'.");
                }
            }
            if (passThrough.Count > 0)
            {
                url += "?" + string.Join('&', passThrough);
            }
            if (subdir != null && subdir.Split('/', '\\').Any(segment => segment == ".."))
            {
                throw new DeclaradException($"Module source '{source}': the subdirectory may not contain '..'.");
            }
            if (parameters.TryGetValue("ref", out var refValue) && refValue.Length == 0)
            {
                throw new DeclaradException($"Module source '{source}' has an empty ref.");
            }

            if (kind == ModuleSourceKind.Archive && url.EndsWith(".git", StringComparison.Ordinal))
            {
                kind = ModuleSourceKind.Git;
            }

            string? archive = null;
            if (kind == ModuleSourceKind.Archive)
            {
                archive = parameters.GetValueOrDefault("archive") ?? DetectArchive(url.Split('?')[0])
                    ?? throw new DeclaradException($"Module source '{source}' is an http(s) URL but not an archive (.zip, .tar.gz, .tgz) and not a git repository. Prefix git repositories with git::.");
                if (archive is not ("zip" or "tar.gz" or "tgz" or "tar"))
                {
                    throw new DeclaradException($"Unsupported archive type '{archive}' in '{source}'.");
                }
            }
            foreach (string p in parameters.Keys.Where(k => k is not ("ref" or "depth" or "archive")))
            {
                throw new DeclaradException($"Module source '{source}' has an unsupported parameter '{p}'.");
            }
            int? depth = parameters.TryGetValue("depth", out var d) ? int.TryParse(d, out int n) && n > 0 ? n : throw new DeclaradException($"Invalid depth '{d}' in '{source}'.") : null;
            if (kind != ModuleSourceKind.Git && (parameters.ContainsKey("ref") || depth != null))
            {
                throw new DeclaradException($"ref and depth only apply to git sources ('{source}').");
            }
            return new ModuleSource(kind, url, parameters.GetValueOrDefault("ref"), subdir, depth, archive, source);
        }

        private static string? DetectArchive(string url)
        {
            string path = url.Split('?')[0].ToLowerInvariant();
            return path.EndsWith(".zip") ? "zip"
                : path.EndsWith(".tar.gz") || path.EndsWith(".tgz") ? "tar.gz"
                : path.EndsWith(".tar") ? "tar"
                : null;
        }

        /// <summary>Splits "url//subdir?query" (the "//" of a scheme is not a subdirectory separator).</summary>
        private static (string Url, string? Subdir, string? Query) SplitAddress(string address)
        {
            string? query = null;
            int q = address.IndexOf('?');
            if (q >= 0)
            {
                query = address[(q + 1)..];
                address = address[..q];
            }
            int schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
            int searchFrom = schemeEnd >= 0 ? schemeEnd + 3 : 0;
            int split = address.IndexOf("//", searchFrom, StringComparison.Ordinal);
            if (split < 0)
            {
                return (address, null, query);
            }
            return (address[..split], address[(split + 2)..].Trim('/'), query);
        }
    }

    /// <summary>Installed remote modules, keyed by module call path ("team", "team.sub"), stored in .declarad/modules/modules.json.</summary>
    public sealed class ModuleManifest
    {
        public sealed record Entry(string Key, string Source, string Dir);

        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);

        public static string ModulesDir(string rootDir) => Path.Combine(rootDir, ProjectSettings.WorkDirName, "modules");

        private static string FilePath(string rootDir) => Path.Combine(ModulesDir(rootDir), "modules.json");

        public static ModuleManifest Load(string rootDir)
        {
            var manifest = new ModuleManifest();
            string path = FilePath(rootDir);
            if (!File.Exists(path))
            {
                return manifest;
            }
            try
            {
                foreach (var node in JsonNode.Parse(File.ReadAllText(path))?["modules"]?.AsArray() ?? new JsonArray())
                {
                    var entry = new Entry(node!["key"]!.GetValue<string>(), node["source"]!.GetValue<string>(), node["dir"]!.GetValue<string>());
                    manifest.Entries[entry.Key] = entry;
                }
            }
            catch (Exception ex) when (ex is JsonException or NullReferenceException or InvalidOperationException)
            {
                throw new DeclaradException($"{path} is corrupt ({ex.Message}). Delete it and run 'declarad init'.");
            }
            return manifest;
        }

        public void Save(string rootDir)
        {
            System.IO.Directory.CreateDirectory(ModulesDir(rootDir));
            var modules = new JsonArray(Entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => (JsonNode)new JsonObject { ["key"] = e.Key, ["source"] = e.Source, ["dir"] = e.Dir.Replace('\\', '/') }).ToArray());
            File.WriteAllText(FilePath(rootDir), new JsonObject { ["modules"] = modules }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Downloads the remote modules a configuration uses (declarad init), recursively.</summary>
    public sealed class ModuleInstaller
    {
        private readonly string _rootDir;
        private readonly bool _upgrade;
        private readonly Action<string> _log;
        private readonly ModuleManifest _previous;
        private readonly ModuleManifest _next = new();

        public ModuleInstaller(string rootDir, bool upgrade, Action<string> log)
        {
            _rootDir = Path.GetFullPath(rootDir);
            _upgrade = upgrade;
            _log = log;
            _previous = ModuleManifest.Load(_rootDir);
        }

        /// <summary>Installs all remote modules and returns how many module calls were found.</summary>
        public int Install()
        {
            int count = Walk(_rootDir, "", 0);
            _next.Save(_rootDir);
            // Remove downloads no longer referenced.
            foreach (var stale in _previous.Entries.Values.Where(e => !_next.Entries.ContainsKey(e.Key)))
            {
                string dir = Path.Combine(ModuleManifest.ModulesDir(_rootDir), stale.Key);
                if (System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                }
            }
            return count;
        }

        private int Walk(string dir, string keyPrefix, int depth)
        {
            if (depth > ConfigLoader.MaxModuleDepth)
            {
                throw new DeclaradException($"Modules are nested more than {ConfigLoader.MaxModuleDepth} levels deep under '{keyPrefix}'.");
            }
            int count = 0;
            foreach (string file in ConfigLoader.FindFiles(dir))
            {
                var body = Parser.ParseFile(File.ReadAllText(file), Path.GetRelativePath(_rootDir, file));
                foreach (var block in body.Blocks.Where(b => b.Type == "module" && b.Labels.Count == 1))
                {
                    if (block.Body.GetAttribute("source")?.Expr is not LiteralExpr { Value: StringValue source })
                    {
                        continue;
                    }
                    count++;
                    string key = (keyPrefix.Length > 0 ? keyPrefix + "." : "") + block.Labels[0];
                    var parsed = ModuleSource.Parse(source.V);
                    string childDir;
                    if (parsed.Kind == ModuleSourceKind.Local)
                    {
                        childDir = Path.GetFullPath(Path.Combine(dir, parsed.Url));
                    }
                    else
                    {
                        childDir = Path.Combine(_rootDir, Fetch(key, parsed));
                    }
                    if (!System.IO.Directory.Exists(childDir))
                    {
                        throw new DeclaradException($"Module '{key}': directory '{childDir}' does not exist.", block.Pos);
                    }
                    count += Walk(childDir, key, depth + 1);
                }
            }
            return count;
        }

        /// <summary>Downloads a remote module (unless already installed) and returns its directory relative to the root.</summary>
        private string Fetch(string key, ModuleSource source)
        {
            string target = Path.Combine(ModuleManifest.ModulesDir(_rootDir), key);
            string relativeDir = Path.GetRelativePath(_rootDir, source.Subdir != null ? Path.Combine(target, source.Subdir) : target);
            var entry = new ModuleManifest.Entry(key, source.Original, relativeDir);
            if (!_upgrade && _previous.Entries.TryGetValue(key, out var existing) && existing.Source == source.Original && System.IO.Directory.Exists(target))
            {
                _next.Entries[key] = existing;
                return existing.Dir;
            }

            _log($"Downloading {source.Original} for module.{key}...");
            if (System.IO.Directory.Exists(target))
            {
                ForceDelete(target);
            }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (source.Kind == ModuleSourceKind.Git)
            {
                var clone = new List<string> { "clone", "--quiet" };
                if (source.Depth != null)
                {
                    clone.AddRange(new[] { "--depth", source.Depth.ToString()! });
                    if (source.Ref != null)
                    {
                        clone.AddRange(new[] { "--branch", source.Ref });
                    }
                }
                clone.AddRange(new[] { source.Url, target });
                RunGit(clone, null);
                if (source.Ref != null && source.Depth == null)
                {
                    RunGit(new[] { "-c", "advice.detachedHead=false", "checkout", "--quiet", source.Ref }, target);
                }
            }
            else
            {
                DownloadArchive(source, target);
            }
            if (!System.IO.Directory.Exists(Path.Combine(_rootDir, relativeDir)))
            {
                throw new DeclaradException($"Module '{key}': '{source.Subdir}' does not exist in {source.Url}.");
            }
            _next.Entries[key] = entry;
            return entry.Dir;
        }

        private static void ForceDelete(string dir)
        {
            // Git marks pack files read-only, which blocks deletion on Windows.
            foreach (string file in System.IO.Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            System.IO.Directory.Delete(dir, recursive: true);
        }

        private static void RunGit(IEnumerable<string> args, string? workingDir)
        {
            var info = new ProcessStartInfo("git")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            };
            foreach (string a in args)
            {
                info.ArgumentList.Add(a);
            }
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            Process process;
            try
            {
                process = Process.Start(info) ?? throw new DeclaradException("Could not start git.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new DeclaradException("git is required to install git module sources but was not found on PATH.");
            }
            using (process)
            {
                var stderr = process.StandardError.ReadToEndAsync();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new DeclaradException($"git {string.Join(' ', info.ArgumentList)} failed: {stderr.Result.Trim()}");
                }
            }
        }

        private static void DownloadArchive(ModuleSource source, string target)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            byte[] data;
            try
            {
                data = http.GetByteArrayAsync(source.Url).GetAwaiter().GetResult();
            }
            catch (HttpRequestException ex)
            {
                throw new DeclaradException($"Downloading {source.Url} failed: {ex.Message}", default, ex);
            }
            System.IO.Directory.CreateDirectory(target);
            using var stream = new MemoryStream(data);
            switch (source.ArchiveType)
            {
                case "zip":
                    ZipFile.ExtractToDirectory(stream, target, overwriteFiles: true);
                    break;
                case "tar":
                    TarFile.ExtractToDirectory(stream, target, overwriteFiles: true);
                    break;
                default:
                    using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                    {
                        TarFile.ExtractToDirectory(gzip, target, overwriteFiles: true);
                    }
                    break;
            }
        }
    }
}
