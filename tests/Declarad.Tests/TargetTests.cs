using Declarad.Engine;
using Declarad.State;
using Declarad.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Declarad.Tests
{
    public class TargetTests
    {
        private const string Src = """
            ou "a" {
              name = "a"
              protected_from_accidental_deletion = false
            }
            user "u" {
              sam_account_name = "u"
              path    = ou.a.dn
              enabled = false
            }
            group "g" {
              name = "g"
              members = [user.u.dn]
            }
            group "other" {
              name = "other"
            }
            """;

        [Fact]
        public void TargetIncludesDependenciesAndDestroyIncludesDependents()
        {
            var dir = new InMemoryDirectory();
            var state = new StateFile();
            var config = ConfigTests.Load(Src);

            var plan = new Planner(dir, config, state, new PlanOptions { Targets = new[] { "group.g" } }).Build();
            Assert.Equal(new[] { "group.g", "ou.a", "user.u" }, plan.Changes.Select(c => c.Address).Order(StringComparer.Ordinal));

            var all = new Planner(dir, config, state, new PlanOptions()).Build();
            new Applier(dir, state, _ => { }, NullLogger.Instance).Apply(all);

            var destroy = new Planner(dir, config, state, new PlanOptions { Destroy = true, Targets = new[] { "ou.a" } }).Build();
            Assert.Equal(new[] { "group.g", "ou.a", "user.u" }, destroy.Changes.Where(c => c.Action == ChangeAction.Delete).Select(c => c.Address).Order(StringComparer.Ordinal));
        }
    }
}
