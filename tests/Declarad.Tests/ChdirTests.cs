using Declarad.Cli;

namespace Declarad.Tests
{
    public class ChdirTests
    {
        [Theory]
        [InlineData(new[] { "-chdir=env/prod", "plan" }, "env/prod", new[] { "plan" })]
        [InlineData(new[] { "--chdir=env/prod", "plan", "--adopt" }, "env/prod", new[] { "plan", "--adopt" })]
        [InlineData(new[] { "-chdir", "env/prod", "apply" }, "env/prod", new[] { "apply" })]
        [InlineData(new[] { "-C", "env/prod", "validate" }, "env/prod", new[] { "validate" })]
        [InlineData(new[] { "-v", "-chdir=x", "plan" }, "x", new[] { "-v", "plan" })]
        [InlineData(new[] { "plan", "-C", "x" }, null, new[] { "plan", "-C", "x" })]
        public void ParsesTheGlobalOptionBeforeTheSubcommand(string[] args, string? directory, string[] rest)
        {
            var result = Chdir.Parse(args);
            Assert.Equal(directory, result.Directory);
            Assert.Equal(rest, result.Args);
        }

        [Theory]
        [InlineData("plan", "-chdir=x")]
        [InlineData("-chdir=")]
        [InlineData("-chdir=a", "-chdir=b", "plan")]
        [InlineData("-chdir")]
        public void RejectsMisplacedOrIncompleteOptions(params string[] args) =>
            Assert.Throws<DeclaradException>(() => Chdir.Parse(args));
    }
}
