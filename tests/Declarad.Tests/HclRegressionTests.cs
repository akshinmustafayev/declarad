using Declarad.Hcl;

namespace Declarad.Tests
{
    /// <summary>Regression tests for bugs found in review of the HCL layer.</summary>
    public class HclRegressionTests
    {
        private static Value Eval(string expr, Dictionary<string, Value>? vars = null) =>
            new Evaluator(NoReferences.Instance, new Functions(".", _ => null), vars).Evaluate(Parser.ParseExpression(expr, new SourcePos("t", 1, 1)));

        private static void Same(string expected, string actual) => Assert.Equal(Eval(expected), Eval(actual));

        [Fact]
        public void NewlinesAreIgnoredInsideParenthesesBracketsAndCalls()
        {
            var body = Parser.ParseFile("""
                a = (
                  length("ab") > 0
                  && true
                )
                b = (true
                  ? 1
                  : 2)
                c = max(1
                  + 2, 0)
                d = [
                  1
                  ,
                  2
                ]
                e = { x = 1
                  y = 2 }
                f = [1, 2][
                  0
                ]
                """, "t.dad");
            var ev = new Evaluator(NoReferences.Instance, new Functions("."));
            Assert.Equal(new[] { Value.True, Value.From(1), Value.From(3), Eval("[1, 2]"), Eval("{ x = 1, y = 2 }"), Value.From(1) },
                body.Attributes.Select(at => ev.Evaluate(at.Expr)));
            // Newlines still terminate attributes at the top level and inside blocks.
            Assert.Throws<DeclaradException>(() => Parser.ParseFile("a = 1 +\n\n", "t.dad"));
        }

        [Fact]
        public void SplatWrapsNonListValues()
        {
            Same("[{ a = 1, b = 2 }]", "{ a = 1, b = 2 }[*]");
            Same("[1]", "1[*]");
            Same("[1, 2]", "[{ a = [1, 2] }, { a = [3, 4] }].*.a[0]");
            Same("[1, 3]", "[{ a = [1, 2] }, { a = [3, 4] }][*].a[0]");
            Same("\"b\"", "[[\"a\", \"b\"]].0.1");
        }

        [Fact]
        public void QuoteEscapesTemplateSequences()
        {
            foreach (string s in new[] { "100%{x} done", "%{", "$${a} %%{b}", "${x}" })
            {
                Assert.Equal(Value.From(s), Eval(HclWriter.Quote(s)));
            }
        }

        [Fact]
        public void FormatterKeepsSpacesInsideQuotedLabels()
        {
            Assert.Equal("blk \"a   b\" {\n  x = 1\n}\n", Formatter.Format("blk   \"a   b\"  {\nx = 1\n}\n", "t.dad"));
        }

        [Fact]
        public void ExplicitNullIsAcceptedForRequiredObjectAttributes()
        {
            var type = TypeConstraint.Parse(Parser.ParseExpression("object({ a = string, b = optional(string, \"d\") })", default), e => new Evaluator(NoReferences.Instance, new Functions(".")).Evaluate(e));
            Assert.Equal(Eval("{ a = null, b = \"d\" }"), type.Convert(Eval("{ a = null, b = null }"), "v"));
            Assert.Throws<DeclaradException>(() => type.Convert(Eval("{ b = \"x\" }"), "v"));
        }

        [Fact]
        public void MatchesTerraformFunctionResults()
        {
            Assert.Equal(Value.From("\"é\\u003c\\u003e\\u0026'+\""), Eval("jsonencode(\"é<>&'+\")"));
            Assert.Equal(Value.From("1.5"), Eval("jsonencode(1.50)"));
            Assert.Equal(Value.From("2.5"), Eval("jsonencode(1.25 * 2)"));
            Same("{ k = \"abc\", v = \"12\" }", "regex(\"(?P<k>[a-z]+)=(?P<v>[0-9]+)\", \"abc=12\")");
            Same("18446744073709551615", "parseint(\"FFFFFFFFFFFFFFFF\", 16)");
            Same("-16", "parseint(\"-10\", 16)");
            Same("1295", "parseint(\"zz\", 36)");
            Assert.Throws<DeclaradException>(() => Eval("parseint(\"0x1F\", 16)"));
            Same("\"Hello+World%21\"", "urlencode(\"Hello World!\")");
            Same("\".\"", "dirname(\"foo\")");
            Same("[1, 2, 3]", "toset([3, 1, 2, 1])");
            Same("[1, 2, 3]", "setunion([2, 1], [3, 1])");
            Same("[\"a\", \"b\", \"c\"]", "split(\"\", \"abc\")");
            Same("\"b😀a\"", "strrev(\"a😀b\")");
            Same("\"b a\"", "format(\"%[2]s %[1]s\", \"a\", \"b\")");
            Assert.Throws<DeclaradException>(() => Eval("tonumber(\" 5\")"));
            Assert.Throws<DeclaradException>(() => Eval("base64decode(\"/w==\")"));
            Same("1024", "pow(2, 10)");
            Same("{ a = 2 }", "jsondecode(\"{\\\"a\\\":1,\\\"a\\\":2}\")");
        }

        [Theory]
        [InlineData("pow(2, 100)")]
        [InlineData("79228162514264337593543950335 + 1")]
        [InlineData("substr(\"abc\", 0, 99999999999)")]
        [InlineData("jsondecode(\"1e400\")")]
        [InlineData("csvdecode(\"a,a\\n1,2\\n\")")]
        [InlineData("log(0, 10)")]
        public void ReportsBadInputAsConfigurationErrors(string expr)
        {
            var ex = Record.Exception(() => Eval(expr));
            Assert.True(ex is null or DeclaradException, $"{expr} threw {ex?.GetType().Name}: {ex?.Message}");
        }

        [Theory]
        [InlineData("\"\\uD83D\"")]
        [InlineData("\"\\U00110000\"")]
        public void RejectsInvalidUnicodeEscapes(string literal) =>
            Assert.Throws<DeclaradException>(() => Eval(literal));

        [Fact]
        public void StrippedSingleInterpolationIsATemplate()
        {
            Assert.Equal(Value.From("x"), Eval("\"  ${~ \"x\" }\""));
            Assert.Throws<DeclaradException>(() => Eval("\"  ${~ [1, 2] }\""));
        }
    }
}
