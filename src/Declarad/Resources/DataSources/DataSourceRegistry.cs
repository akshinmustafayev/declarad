namespace Declarad.Resources.DataSources
{
    public static class DataSourceRegistry
    {
        public static IReadOnlyList<DataSourceType> All { get; } = ResourceRegistry.All
            .Where(t => t is not AttachmentType)
            .Select(t => (DataSourceType)new ObjectDataSource(t))
            .Concat(new DataSourceType[] { new GpoDataSource(), new DomainDataSource(), new ObjectsDataSource() })
            .ToList();

        public static DataSourceType? Find(string name) => All.FirstOrDefault(d => d.Name == name);
    }
}
