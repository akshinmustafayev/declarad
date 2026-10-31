namespace Declarad.Hcl.Syntax
{
    public sealed class Body
    {
        public List<Attribute> Attributes { get; } = new();
        public List<Block> Blocks { get; } = new();
        public SourcePos Pos { get; init; }

        public Attribute? GetAttribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);
    }
}
