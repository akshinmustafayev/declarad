using Declarad.Hcl;

namespace Declarad.Tests
{
    public class HclTests
    {
        private sealed class NoRefs : IReferenceResolver
        {
            public (Value Value, int Consumed) Resolve(ScopeTraversalExpr reference, Func<Expr, Value> evaluate) =>
                reference.Root == "var" ? (new ObjectValue(new[] { new KeyValuePair<string, Value>("name", Value.From("world")) }), 0)
                    : throw new DeclaradException("unknown " + reference.Root, reference.Pos);
        }

        private static Value Eval(string expr, Func<string, string?>? env = null) =>
            new Evaluator(new NoRefs(), new Functions(".", env ?? (_ => null))).Evaluate(Parser.ParseExpression(expr, new SourcePos("t", 1, 1)));

        [Fact]
        public void ParsesBlocksAttributesAndComments()
        {
            var body = Parser.ParseFile("""
                # comment
                // another
                /* block
                   comment */
                user "jdoe" {
                  name    = "John" # trailing
                  enabled = true
                  count   = 42
                  groups  = [
                    "a",
                    "b",
                  ]
                  lifecycle {
                    prevent_destroy = true
                  }
                }
                """, "main.dad");

            var block = Assert.Single(body.Blocks);
            Assert.Equal("user", block.Type);
            Assert.Equal(new[] { "jdoe" }, block.Labels);
            Assert.Equal(4, block.Body.Attributes.Count);
            Assert.Equal("lifecycle", Assert.Single(block.Body.Blocks).Type);
            Assert.Equal(6, block.Body.GetAttribute("name")!.Pos.Line);
        }

        [Fact]
        public void ReportsErrorsWithPosition()
        {
            var ex = Assert.Throws<DeclaradException>(() => Parser.ParseFile("user \"x\" {\n  name = \n}", "f.dad"));
            Assert.Equal("f.dad", ex.Pos.File);
            Assert.Equal(2, ex.Pos.Line);

            ex = Assert.Throws<DeclaradException>(() => Parser.ParseFile("user \"x\" {\n  name = \"a\"\n  name = \"b\"\n}", "f.dad"));
            Assert.Contains("more than once", ex.Message);

            ex = Assert.Throws<DeclaradException>(() => Parser.ParseFile("a = \"unterminated\n", "f.dad"));
            Assert.Contains("Unterminated string", ex.Message);
        }

        [Fact]
        public void EvaluatesTemplatesAndEscapes()
        {
            Assert.Equal(Value.From("hello world!"), Eval("\"hello ${var.name}!\""));
            Assert.Equal(Value.From("a\"b\\c\n"), Eval("\"a\\\"b\\\\c\\n\""));
            Assert.Equal(Value.From("literal ${x}"), Eval("\"literal $${x}\""));
            Assert.Equal(Value.From("nested {braces} world"), Eval("\"nested ${\"{braces}\"} ${var.name}\""));
            Assert.Equal(Value.From("CN=A\\,B"), Eval("\"CN=A\\\\,B\""));
        }

        [Fact]
        public void EvaluatesHeredoc()
        {
            var body = Parser.ParseFile("x = <<-EOT\n    line ${upper(\"one\")}\n      line two\n    EOT\n", "f");
            var v = new Evaluator(new NoRefs(), new Functions(".")).Evaluate(body.Attributes[0].Expr);
            Assert.Equal(Value.From("line ONE\n  line two\n"), v);
        }

        [Fact]
        public void EvaluatesOperatorsAndFunctions()
        {
            Assert.Equal(Value.From("b"), Eval("1 == 2 ? \"a\" : \"b\""));
            Assert.Equal(Value.True, Eval("!false && (true || false)"));
            Assert.Equal(Value.From("a-b-c"), Eval("join(\"-\", split(\",\", \"a,b,c\"))"));
            Assert.Equal(Value.From("x"), Eval("coalesce(null, \"\", \"x\")"));
            Assert.Equal(Value.From(3), Eval("length(concat([1], [2, 3]))"));
            Assert.Equal(Value.From("v"), Eval("lookup({ k = \"v\" }, \"k\")"));
            Assert.Equal(Value.From("fallback"), Eval("env(\"NOPE\", \"fallback\")"));
            Assert.Equal(Value.From("set"), Eval("env(\"YES\")", n => n == "YES" ? "set" : null));
            Assert.Equal(Value.From("b"), Eval("split(\",\", \"a,b\")[1]"));
            var ex = Assert.Throws<DeclaradException>(() => Eval("nosuch()"));
            Assert.Contains("Unknown function", ex.Message);
        }

        [Fact]
        public void EvaluatesForExpressions()
        {
            Assert.Equal(Eval("[\"A\", \"C\"]"), Eval("[for s in [\"a\", \"b\", \"c\"] : upper(s) if s != \"b\"]"));
            Assert.Equal(Eval("{ a = \"x-a\", b = \"x-b\" }"), Eval("{ for s in [\"a\", \"b\"] : s => \"x-${s}\" }"));
            Assert.Equal(Eval("[\"k=v\"]"), Eval("[for k, v in { k = \"v\" } : \"${k}=${v}\"]"));
            Assert.Throws<DeclaradException>(() => Eval("{ for s in [\"a\", \"a\"] : s => s }"));
        }

        [Fact]
        public void DecodesCsv()
        {
            var v = (ListValue)Eval("csvdecode(\"sam,name\\njdoe,\\\"Doe, John\\\"\\nasmith,Anna\\n\")");
            Assert.Equal(2, v.Items.Length);
            var first = (ObjectValue)v.Items[0];
            Assert.Equal(Value.From("Doe, John"), first.Fields["name"]);
            Assert.Equal(Value.From("asmith"), ((ObjectValue)v.Items[1]).Fields["sam"]);
        }

        [Fact]
        public void QuotesRoundTrip()
        {
            foreach (string s in new[] { "plain", "with \"quotes\"", "back\\slash", "${not interp}", "tab\tnew\nline" })
            {
                Assert.Equal(Value.From(s), Eval(HclWriter.Quote(s)));
            }
            Assert.Equal("sales_team", HclWriter.ToLabel("Sales Team!"));
            Assert.Equal("_2024", HclWriter.ToLabel("2024"));
        }
    }
}
