using System.Text.Json;

namespace entraform.AdEntities
{
    [JsonInterfaceConverter(typeof(InterfaceConverter<IAdItem>))]
    public interface IAdItem { }
}
