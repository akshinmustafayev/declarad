namespace entraform.AdEntities
{
    public class AdActionStep
    {
        public Guid Id { get; set; }
        public int Ordering { get; set; }
        public AdItemType Type { get; set; }
        public AdItemOperation Operation { get; set; }
        public IAdItem AdItem { get; set; }
    }
}
