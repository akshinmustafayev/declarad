using System.Text.RegularExpressions;

namespace Declarad.Config.Modules
{
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
}
