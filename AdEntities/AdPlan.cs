namespace declarad.AdEntities
{
    public class AdPlan
    {
        public AdConnection Connection { get; set; } = new();
        public List<AdActionStep> ActionSteps { get; set; } = new();
        public List<AdResource> Resources { get; set; } = new();
        public List<AdVariable> Variables { get; set; } = new();
    }
}
