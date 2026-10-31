using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using Declarad.Config;
using Declarad.Config.Modules;

namespace Declarad.Tests
{
    public class ModuleSourceTests
    {
        [Theory]
        [InlineData("./modules/x", ModuleSourceKind.Local, "./modules/x", null, null)]
        [InlineData("git::https://example.com/org/repo.git//modules/team?ref=v1.2.0", ModuleSourceKind.Git, "https://example.com/org/repo.git", "modules/team", "v1.2.0")]
        [InlineData("git::ssh://git@example.com/org/repo.git", ModuleSourceKind.Git, "ssh://git@example.com/org/repo.git", null, null)]
        [InlineData("git@github.com:org/repo.git//sub?ref=main", ModuleSourceKind.Git, "git@github.com:org/repo.git", "sub", "main")]
        [InlineData("github.com/org/repo//modules/dept?ref=abc123", ModuleSourceKind.Git, "https://github.com/org/repo.git", "modules/dept", "abc123")]
        [InlineData("https://example.com/repo.git", ModuleSourceKind.Git, "https://example.com/repo.git", null, null)]
        [InlineData("https://example.com/modules/team.zip//team", ModuleSourceKind.Archive, "https://example.com/modules/team.zip", "team", null)]
        [InlineData("https://example.com/download?archive=tar.gz", ModuleSourceKind.Archive, "https://example.com/download", null, null)]
        public void ParsesTerraformSourceAddresses(string source, ModuleSourceKind kind, string url, string? subdir, string? @ref)
        {
            var parsed = ModuleSource.Parse(source);
            Assert.Equal((kind, url, subdir, @ref), (parsed.Kind, parsed.Url, parsed.Subdir, parsed.Ref));
        }

        [Theory]
        [InlineData("hashicorp/consul/aws", "registry")]
        [InlineData("s3::https://bucket/module.zip", "not supported")]
        [InlineData("/abs/path", "not supported")]
        [InlineData("https://example.com/page", "not an archive")]
        [InlineData("git::https://example.com/repo.git?sshkey=x", "unsupported parameter")]
        public void RejectsUnsupportedSources(string source, string message) =>
            Assert.Contains(message, Assert.Throws<DeclaradException>(() => ModuleSource.Parse(source)).Message);

        private static void Git(string dir, params string[] args)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "-c", "user.email=test@example.com", "-c", "user.name=test", "-c", "init.defaultBranch=main", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false" }.Concat(args))
            {
                info.ArgumentList.Add(a);
            }
            using var p = Process.Start(info)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
        }

        private static string Root(string source) =>
            ConfigTests.Connection + $"module \"team\" {{\n  source = \"{source}\"\n  name   = \"Eng\"\n}}\n";

        [Fact]
        public void InstallsGitModulesAtARefAndDetectsSourceChanges()
        {
            string repo = System.IO.Directory.CreateTempSubdirectory("dad-repo").FullName;
            System.IO.Directory.CreateDirectory(Path.Combine(repo, "modules", "team"));
            File.WriteAllText(Path.Combine(repo, "modules", "team", "main.dad"), "variable \"name\" {}\ngroup \"team\" {\n  name = \"${var.name} v1\"\n}\nmodule \"nested\" {\n  source = \"../shared\"\n}\n");
            System.IO.Directory.CreateDirectory(Path.Combine(repo, "modules", "shared"));
            File.WriteAllText(Path.Combine(repo, "modules", "shared", "main.dad"), "group \"shared\" {}\n");
            Git(repo, "init", "--quiet");
            Git(repo, "add", ".");
            Git(repo, "commit", "--quiet", "-m", "v1");
            Git(repo, "tag", "v1");
            File.WriteAllText(Path.Combine(repo, "modules", "team", "main.dad"), "variable \"name\" {}\ngroup \"team\" {\n  name = \"${var.name} v2\"\n}\n");
            Git(repo, "commit", "--quiet", "-am", "v2");
            Git(repo, "tag", "v2");

            string root = System.IO.Directory.CreateTempSubdirectory("dad-root").FullName;
            string source = $"git::file://{repo.Replace('\\', '/')}//modules/team?ref=v1";
            File.WriteAllText(Path.Combine(root, "main.dad"), Root(source));

            var notInstalled = Assert.ThrowsAny<DeclaradException>(() => ConfigLoader.Load(root));
            Assert.Contains("declarad init", notInstalled is ConfigErrorsException c ? c.Errors[0].Message : notInstalled.Message);

            var log = new List<string>();
            Assert.Equal(2, new ModuleInstaller(root, upgrade: false, log.Add).Install());
            Assert.Single(log);
            var config = ConfigLoader.Load(root);
            Assert.Equal(Hcl.Values.Value.From("Eng v1"), config.Find("module.team.group.team")!.Get("name"));
            Assert.NotNull(config.Find("module.team.module.nested.group.shared"));

            // Re-running init does not download again.
            new ModuleInstaller(root, upgrade: false, log.Add).Install();
            Assert.Single(log);

            File.WriteAllText(Path.Combine(root, "main.dad"), Root(source.Replace("ref=v1", "ref=v2")));
            var changed = Assert.ThrowsAny<DeclaradException>(() => ConfigLoader.Load(root));
            Assert.Contains("changed", changed is ConfigErrorsException c2 ? c2.Errors[0].Message : changed.Message);
            new ModuleInstaller(root, upgrade: false, log.Add).Install();
            Assert.Equal(Hcl.Values.Value.From("Eng v2"), ConfigLoader.Load(root).Find("module.team.group.team")!.Get("name"));
        }

        [Fact]
        public async Task InstallsArchiveModulesOverHttp()
        {
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("team/main.dad").Open());
                writer.Write("variable \"name\" {}\ngroup \"team\" {\n  name = var.name\n}\n");
            }
            byte[] bytes = zip.ToArray();

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var server = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                ctx.Response.ContentType = "application/zip";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            });

            string root = System.IO.Directory.CreateTempSubdirectory("dad-zip").FullName;
            File.WriteAllText(Path.Combine(root, "main.dad"), Root($"http://127.0.0.1:{port}/team.zip//team"));
            new ModuleInstaller(root, upgrade: false, _ => { }).Install();
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Hcl.Values.Value.From("Eng"), ConfigLoader.Load(root).Find("module.team.group.team")!.Get("name"));
        }
    }
}
