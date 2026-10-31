using Declarad.Directory;
using Microsoft.Extensions.Logging;

namespace Declarad.Resources
{
    public sealed class ApplyContext
    {
        public required IDirectory Directory { get; init; }
        public required ILogger Logger { get; init; }
    }
}
