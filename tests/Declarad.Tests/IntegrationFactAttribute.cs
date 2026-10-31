namespace Declarad.Tests
{
    /// <summary>A test that needs an external service; skipped unless the named environment variable is set.</summary>
    public sealed class IntegrationFactAttribute : FactAttribute
    {
        public IntegrationFactAttribute(string environmentVariable)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(environmentVariable)))
            {
                Skip = $"Set {environmentVariable} to run this integration test.";
            }
        }
    }
}
