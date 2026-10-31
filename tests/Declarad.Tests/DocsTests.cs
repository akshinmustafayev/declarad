using System.Text.RegularExpressions;
using Declarad.Cli;
using Declarad.Config;
using Declarad.Hcl;
using Declarad.Resources;

namespace Declarad.Tests
{
    /// <summary>Keeps docs/ and examples/ honest: generated tables current, links valid, snippets and examples valid.</summary>
    public class DocsTests
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        private static string Docs => Path.Combine(Root, "docs");
        private static string Examples => Path.Combine(Root, "examples");

        [Fact]
        public void EveryResourceTypeHasACurrentReferencePage()
        {
            foreach (var type in ResourceRegistry.All)
            {
                string path = Path.Combine(Docs, "resources", type.Name + ".md");
                Assert.True(File.Exists(path), $"Missing {path}");
                var match = Regex.Match(File.ReadAllText(path), $@"<!-- schema:{type.Name} -->\n(.*?)<!-- /schema -->", RegexOptions.Singleline);
                Assert.True(match.Success, $"{path} has no schema marker");
                Assert.True(match.Groups[1].Value == SchemaDocs.Markdown(type),
                    $"{path} is out of date; regenerate the table with: declarad schema {type.Name} --markdown");
            }
        }

        [Fact]
        public void RelativeLinksResolve()
        {
            var markdown = System.IO.Directory.GetFiles(Docs, "*.md", SearchOption.AllDirectories)
                .Append(Path.Combine(Root, "README.md")).Append(Path.Combine(Examples, "README.md"));
            var broken = new List<string>();
            foreach (string file in markdown)
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\]\(([^)#\s]+)(#[^)]*)?\)"))
                {
                    string target = m.Groups[1].Value;
                    if (target.Contains("://") || target.StartsWith("mailto:", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, target));
                    if (!File.Exists(resolved) && !System.IO.Directory.Exists(resolved))
                    {
                        broken.Add($"{Path.GetRelativePath(Root, file)} -> {target}");
                    }
                }
            }
            Assert.Empty(broken);
        }

        [Fact]
        public void HclSnippetsInTheDocsParse()
        {
            var failures = new List<string>();
            foreach (string file in System.IO.Directory.GetFiles(Docs, "*.md", SearchOption.AllDirectories).Append(Path.Combine(Root, "README.md")))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(file), "```hcl\n(.*?)```", RegexOptions.Singleline))
                {
                    try
                    {
                        Parser.ParseFile(m.Groups[1].Value, Path.GetFileName(file));
                    }
                    catch (DeclaradException ex)
                    {
                        failures.Add($"{Path.GetRelativePath(Root, file)}: {ex.Describe()}");
                    }
                }
            }
            Assert.Empty(failures);
        }

        public static IEnumerable<object[]> ExampleDirectories() =>
            System.IO.Directory.GetFiles(Path.Combine(Root, "examples"), "main.dad", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(d => !d!.Contains($"{Path.DirectorySeparatorChar}modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(d => new object[] { Path.GetRelativePath(Path.Combine(Root, "examples"), d!) });

        [Theory]
        [MemberData(nameof(ExampleDirectories))]
        public void ExamplesValidateAndAreFormatted(string example)
        {
            string dir = Path.Combine(Examples, example);
            foreach (string file in System.IO.Directory.GetFiles(dir, "*.dad", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(file);
                Assert.True(source == Formatter.Format(source, file), $"{file} is not formatted; run declarad fmt -r");
            }
            if (System.IO.Directory.GetFiles(dir, "*.dad").Any(f => File.ReadAllText(f).Contains("git::") || File.ReadAllText(f).Contains("https://artifacts")))
            {
                return; // Needs 'declarad init' to download its modules.
            }
            // Every env() returns a placeholder; variables keep their defaults except secrets.
            string? Env(string name) => name.StartsWith("DECLARAD_VAR_", StringComparison.Ordinal)
                ? name.EndsWith("password", StringComparison.Ordinal) ? "Placeholder-Passw0rd!" : null
                : "placeholder";
            var config = ConfigLoader.Load(dir, new LoadOptions { GetEnv = Env });
            Assert.NotEmpty(config.Resources);
        }
    }
}
