using System.Text.Json;

namespace declarad.AdEntities
{
    [JsonInterfaceConverter(typeof(InterfaceConverter<IAdItem>))]
    public interface IAdItem { }
}
