using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using Declarad.Hcl;
using Declarad.Hcl.Syntax;
using Declarad.Hcl.Values;

namespace Declarad.Config.Modules
{
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
