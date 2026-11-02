using Declarad.Engine;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    public class RendererTests
    {
        [Fact]
        public void RendersReadableDiff()
        {
            var dir = new InMemoryDirectory();
            var state = new StateFile();
            const string v1 = """
                ou "staff" { name = "Staff" }
                user "jdoe" {
                  sam_account_name = "jdoe"
                  path             = ou.staff.dn
                  title            = "Engineer"
                  initial_password = "s3cret!"
                  groups           = ["Domain Admins"]
                }
                group "old" {
                  name = "old"
                }
                """;
            var plan = new Planner(dir, ConfigTests.Load(v1), state, new PlanOptions()).Build();
            var ui = new Declarad.Cli.ConsoleUi { Color = false };
            string text = new Declarad.Cli.PlanRenderer(ui).Render(plan);
            Assert.Contains("# user.jdoe will be created", text);
            Assert.Contains("initial_password", text);
            Assert.Contains("(sensitive value)", text);
            Assert.DoesNotContain("s3cret!", text);
            Assert.Contains("Plan: 3 to add, 0 to change, 0 to destroy.", text);
            new Applier(dir, state, _ => { }, NullLogger.Instance).Apply(plan);

            const string v2 = """
                ou "staff" { name = "Staff" }
                user "jdoe" {
                  sam_account_name = "jdoe"
                  path   = ou.staff.dn
                  title  = "Lead"
                  groups = []
                }
                """;
            plan = new Planner(dir, ConfigTests.Load(v2), state, new PlanOptions()).Build();
            text = new Declarad.Cli.PlanRenderer(ui).Render(plan);
            Assert.Contains("~ title", text);
            Assert.Contains("\"Engineer\" -> \"Lead\"", text);
            Assert.Contains("- \"CN=Domain Admins,CN=Users,DC=corp,DC=local\"", text);
            Assert.Contains("# group.old will be destroyed", text);
            Assert.Contains("Plan: 0 to add, 1 to change, 1 to destroy.", text);
        }
    }
}
