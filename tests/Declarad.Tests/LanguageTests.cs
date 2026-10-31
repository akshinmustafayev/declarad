using Declarad.Hcl;

namespace Declarad.Tests
{
    public class LanguageTests
    {
        private static Value Eval(string expr, Dictionary<string, Value>? vars = null, string baseDir = ".") =>
            new Evaluator(NoReferences.Instance, new Functions(baseDir, _ => null), vars).Evaluate(Parser.ParseExpression(expr, new SourcePos("t", 1, 1)));

        private static void Same(string expected, string actual) => Assert.Equal(Eval(expected), Eval(actual));

        [Theory]
        [InlineData("7", "1 + 2 * 3")]
        [InlineData("9", "(1 + 2) * 3")]
        [InlineData("1", "7 % 3")]
        [InlineData("2.5", "5 / 2")]
        [InlineData("-4", "-(2 + 2)")]
        [InlineData("true", "1 + 1 == 2 && 3 > 2")]
        [InlineData("true", "2 >= 2 && 1 <= 1 && !(1 < 1)")]
        [InlineData("false", "false || 1 > 2")]
        [InlineData("\"big\"", "10 > 5 ? \"big\" : \"small\"")]
        [InlineData("1.5e3", "1500")]
        [InlineData("8", "\"5\" + 3")]
        public void EvaluatesOperators(string expected, string expr) => Same(expected, expr);

        [Fact]
        public void ReportsArithmeticErrors()
        {
            Assert.Contains("Division by zero", Assert.Throws<DeclaradException>(() => Eval("1 / 0")).Message);
            Assert.Contains("Expected a number", Assert.Throws<DeclaradException>(() => Eval("\"x\" + 1")).Message);
        }

        [Fact]
        public void SupportsSplatAndExpansion()
        {
            var vars = new Dictionary<string, Value>
            {
                ["users"] = Eval("[{ name = \"a\", tags = [\"x\"] }, { name = \"b\", tags = [\"y\", \"z\"] }]"),
            };
            Assert.Equal(Eval("[\"a\", \"b\"]"), Eval("users[*].name", vars));
            Assert.Equal(Eval("[\"a\", \"b\"]"), Eval("users.*.name", vars));
            Assert.Equal(Eval("[[\"x\"], [\"y\", \"z\"]]"), Eval("users[*].tags", vars));
            Assert.Equal(Eval("[\"x\", \"y\", \"z\"]"), Eval("flatten(users[*].tags)", vars));
            Same("3", "max([1, 3, 2]...)");
            Same("\"a-b-c\"", "join(\"-\", [\"a\"], [\"b\", \"c\"])");
            Same("[]", "null[*]");
        }

        [Fact]
        public void GroupsForExpressionValues()
        {
            var vars = new Dictionary<string, Value> { ["rows"] = Eval("[{ d = \"eng\", u = \"a\" }, { d = \"ops\", u = \"b\" }, { d = \"eng\", u = \"c\" }]") };
            Assert.Equal(Eval("{ eng = [\"a\", \"c\"], ops = [\"b\"] }"), Eval("{ for r in rows : r.d => r.u... }", vars));
            var ex = Assert.Throws<DeclaradException>(() => Eval("{ for r in rows : r.d => r.u }", vars));
            Assert.Contains("'...'", ex.Message);
        }

        [Fact]
        public void RendersTemplateDirectives()
        {
            var vars = new Dictionary<string, Value> { ["names"] = Eval("[\"a\", \"b\"]"), ["admin"] = Value.True };
            Assert.Equal(Value.From("a,b,"), Eval("\"%{ for n in names }${n},%{ endfor }\"", vars));
            Assert.Equal(Value.From("yes"), Eval("\"%{ if admin }yes%{ else }no%{ endif }\"", vars));
            Assert.Equal(Value.From("x"), Eval("\"%{ if !admin }y%{ endif }x\"", vars));
            Assert.Equal(Value.From("literal %{ x }"), Eval("\"literal %%{ x }\""));

            var body = Parser.ParseFile("t = <<-EOT\n  %{~ for n in names ~}\n  - ${n}\n  %{~ endfor ~}\n  EOT\n", "f");
            var v = new Evaluator(NoReferences.Instance, new Functions("."), vars).Evaluate(body.Attributes[0].Expr);
            Assert.Equal(Value.From("- a- b"), v);

            Assert.Contains("endif", Assert.Throws<DeclaradException>(() => Eval("\"%{ if true }x\"")).Message);
        }

        [Fact]
        public void TryAndCanCatchErrors()
        {
            var vars = new Dictionary<string, Value> { ["obj"] = Eval("{ a = 1 }") };
            Same("2", "try(obj.missing, 2)");
            Assert.Equal(Value.From(1), Eval("try(obj.a, 2)", vars));
            Assert.Equal(Value.False, Eval("can(obj.missing)", vars));
            Assert.Equal(Value.True, Eval("can(obj.a)", vars));
        }

        [Theory]
        [InlineData("\"Hello, World\"", "format(\"Hello, %s\", \"World\")")]
        [InlineData("\"007|  x|y  |3.14\"", "format(\"%03d|%3s|%-3s|%.2f\", 7, \"x\", \"y\", 3.14159)")]
        [InlineData("\"ff\"", "format(\"%x\", 255)")]
        [InlineData("[\"u-a\", \"u-b\"]", "formatlist(\"u-%s\", [\"a\", \"b\"])")]
        [InlineData("\"Hello World\"", "title(\"hello world\")")]
        [InlineData("\"abc\"", "trimprefix(\"xabc\", \"x\")")]
        [InlineData("\"lo\"", "substr(\"hello\", -2, -1)")]
        [InlineData("\"a_b\"", "replace(\"a-b\", \"/[^a-z]/\", \"_\")")]
        [InlineData("\"42\"", "regex(\"[0-9]+\", \"id42\")")]
        [InlineData("{ user = \"jdoe\" }", "regex(\"(?<user>[a-z]+)@\", \"jdoe@corp\")")]
        [InlineData("[\"1\", \"22\"]", "regexall(\"[0-9]+\", \"a1b22\")")]
        [InlineData("{ a = 1, b = 3 }", "merge({ a = 1, b = 2 }, { b = 3 })")]
        [InlineData("[\"a\", \"b\"]", "distinct([\"a\", \"b\", \"a\"])")]
        [InlineData("[\"a\", \"b\", \"c\"]", "setunion([\"c\", \"a\"], [\"b\", \"a\"])")]
        [InlineData("[\"a\"]", "setintersection([\"a\", \"b\"], [\"a\"])")]
        [InlineData("[\"b\"]", "setsubtract([\"a\", \"b\"], [\"a\"])")]
        [InlineData("[0, 1, 2]", "range(3)")]
        [InlineData("[[1, 2], [3]]", "chunklist([1, 2, 3], 2)")]
        [InlineData("{ a = 1, b = 2 }", "zipmap([\"a\", \"b\"], [1, 2])")]
        [InlineData("[\"a\"]", "compact([\"a\", \"\", null])")]
        [InlineData("\"b\"", "element([\"a\", \"b\"], 3)")]
        [InlineData("1", "index([\"a\", \"b\"], \"b\")")]
        [InlineData("6", "sum([1, 2, 3])")]
        [InlineData("null", "one([])")]
        [InlineData("true", "alltrue([true, \"true\"])")]
        [InlineData("{ b = 2 }", "jsondecode(\"{\\\"b\\\": 2}\")")]
        [InlineData("\"{\\\"a\\\":[1,true]}\"", "jsonencode({ a = [1, true] })")]
        [InlineData("\"aGk=\"", "base64encode(\"hi\")")]
        [InlineData("\"5d41402abc4b2a76b9719d911017c592\"", "md5(\"hello\")")]
        [InlineData("255", "parseint(\"ff\", 16)")]
        [InlineData("\"OU=a\\\\,b,DC=x\"", "dnjoin(\"OU\", \"a,b\", \"DC=x\")")]
        [InlineData("\"DC=corp,DC=local\"", "domaindn(\"corp.local\")")]
        [InlineData("\"OU=x,DC=y\"", "dnparent(\"CN=u,OU=x,DC=y\")")]
        [InlineData("\"list\"", "type([])")]
        public void ImplementsStandardFunctions(string expected, string expr) => Same(expected, expr);

        [Fact]
        public void RendersTemplateFiles()
        {
            string dir = System.IO.Directory.CreateTempSubdirectory("dad").FullName;
            File.WriteAllText(Path.Combine(dir, "desc.tpl"), "Team ${name}%{ for m in members }, ${m}%{ endfor }");
            var v = Eval("templatefile(\"desc.tpl\", { name = \"eng\", members = [\"a\", \"b\"] })", baseDir: dir);
            Assert.Equal(Value.From("Team eng, a, b"), v);
            Assert.Equal(Value.True, Eval("fileexists(\"desc.tpl\")", baseDir: dir));
            Assert.Equal(Eval("[\"desc.tpl\"]"), Eval("fileset(\".\", \"*.tpl\")", baseDir: dir));
        }

        [Fact]
        public void UnknownValuesPropagate()
        {
            var vars = new Dictionary<string, Value> { ["u"] = Value.Unknown };
            Assert.True(Eval("u.name", vars).IsUnknown);
            Assert.True(Eval("\"x${u}\"", vars).IsUnknown);
            Assert.True(Eval("upper(u)", vars).IsUnknown);
            Assert.True(Eval("u ? 1 : 2", vars).IsUnknown);
            Assert.True(Eval("[for x in u : x]", vars).IsUnknown);
            Assert.True(Eval("u + 1", vars).IsUnknown);
            Assert.Equal(Value.False, Eval("false && u", vars));
        }

        [Fact]
        public void ConvertsTypeConstraints()
        {
            TypeConstraint Type(string source) => TypeConstraint.Parse(Parser.ParseExpression(source, new SourcePos("t", 1, 1)), e => Eval(e is LiteralExpr l ? l.Value.ToHcl() : "null"));

            Assert.Equal(Value.From(5), Type("number").Convert(Value.From("5"), "v"));
            Assert.Equal(Eval("[\"1\", \"2\"]"), Type("list(string)").Convert(Eval("[1, 2]"), "v"));
            Assert.Equal(Eval("[\"a\", \"b\"]"), Type("set(string)").Convert(Eval("[\"b\", \"a\", \"b\"]"), "v"));
            Assert.Equal(Eval("{ x = true }"), Type("map(bool)").Convert(Eval("{ x = \"true\" }"), "v"));

            var obj = Type("object({ name = string, dept = optional(string, \"none\"), tags = optional(list(string)) })");
            Assert.Equal(Eval("{ name = \"a\", dept = \"none\", tags = null }"), obj.Convert(Eval("{ name = \"a\", extra = 1 }"), "v"));
            Assert.Contains("'name' is required", Assert.Throws<DeclaradException>(() => obj.Convert(Eval("{ dept = \"x\" }"), "v")).Message);
            Assert.Contains("expected number", Assert.Throws<DeclaradException>(() => Type("number").Convert(Value.From("x"), "v")).Message);
            Assert.True(Type("tuple([string, number])").IsStructured);
        }
    }
}
