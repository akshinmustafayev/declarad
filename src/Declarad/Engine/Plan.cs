namespace Declarad.Engine
{
    public sealed class Plan
    {
        public List<ResourceChange> Changes { get; } = new();
        public List<string> Warnings { get; } = new();

        public bool HasChanges => Changes.Any(c => c.Action != ChangeAction.NoOp || c.Adopt);

        public int Count(ChangeAction action) => Changes.Count(c => c.Action == action);

        public int ToAdd => Changes.Count(c => c.Action is ChangeAction.Create or ChangeAction.Replace);
        public int ToChange => Count(ChangeAction.Update);
        public int ToDestroy => Changes.Count(c => c.Action is ChangeAction.Delete or ChangeAction.Replace);
    }
}
