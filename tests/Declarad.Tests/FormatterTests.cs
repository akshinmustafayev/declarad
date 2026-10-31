using Declarad.Hcl;

namespace Declarad.Tests
{
    public class FormatterTests
    {
        [Fact]
        public void IndentsAlignsAndTidies()
        {
            const string input = "\n\n# Users\nuser   \"jdoe\"   {\n\n      given_name=\"John\"\n  surname    =   \"Doe\" # trailing\n\tgroups = [\n\"a\",\n      \"b\",\n]\n  enabled = true\n\n\n\n  lifecycle {\n  ignore_changes = [phone]\n  }\n\n}\nlocals {\nx = { a = 1, bb = 2 }\n}";
            const string expected = "# Users\nuser \"jdoe\" {\n  given_name = \"John\"\n  surname    = \"Doe\" # trailing\n  groups = [\n    \"a\",\n    \"b\",\n  ]\n  enabled = true\n\n  lifecycle {\n    ignore_changes = [phone]\n  }\n}\nlocals {\n  x = { a = 1, bb = 2 }\n}\n";
            Assert.Equal(expected, Formatter.Format(input, "t.dad"));
            Assert.Equal(expected, Formatter.Format(expected, "t.dad"));
        }

        [Fact]
        public void IndentsOneLevelPerLineAndDoesNotAlignMultiLineValues()
        {
            const string input = "variable \"x\" {\ndescription = \"d\"\ntype = map(object({\nname = string\ntags = list(string)\n}))\ndefault = {}\n}\n";
            const string expected = "variable \"x\" {\n  description = \"d\"\n  type = map(object({\n    name = string\n    tags = list(string)\n  }))\n  default = {}\n}\n";
            Assert.Equal(expected, Formatter.Format(input, "t.dad"));
        }

        [Fact]
        public void LeavesHeredocsCommentsAndStringsAlone()
        {
            const string input = "x = <<EOT\n   keep   = this\n{ unbalanced\nEOT\n/* block\n     comment = here */\ny   =   \"a  =  b ${ \"{\" }\"\n";
            const string expected = "x = <<EOT\n   keep   = this\n{ unbalanced\nEOT\n/* block\n     comment = here */\ny = \"a  =  b ${ \"{\" }\"\n";
            Assert.Equal(expected, Formatter.Format(input, "t.dad"));
        }

        [Fact]
        public void RefusesInvalidInputAndProducesDiff()
        {
            Assert.Throws<DeclaradException>(() => Formatter.Format("user \"x\" {", "bad.dad"));
            var diff = Formatter.Diff("a=1\n", "a = 1\n").ToList();
            Assert.Equal(new[] { "-1: a=1", "+1: a = 1" }, diff);
        }

        [Fact]
        public void InitTemplateIsCanonical()
        {
            Assert.Equal(Declarad.Cli.Templates.Main, Formatter.Format(Declarad.Cli.Templates.Main, "main.dad"));
        }

        [Fact]
        public void FormatsExampleConfigurations()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples"));
            foreach (string file in System.IO.Directory.GetFiles(root, "*.dad", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(file);
                // The shipped examples are kept in canonical form.
                Assert.Equal(source, Formatter.Format(source, file));
            }
        }
    }
}
