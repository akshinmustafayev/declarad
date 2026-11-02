namespace Declarad.Engine
{
    public sealed class ApplyResult
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Restored { get; set; }
        public int Deleted { get; set; }
        public List<string> Errors { get; } = new();
        public bool Success => Errors.Count == 0;
    }
}
