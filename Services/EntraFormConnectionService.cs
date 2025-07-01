using declarad.AdEntities;
using declarad.Utils;
using Microsoft.Extensions.Logging;

namespace declarad.Services
{
    public interface IEntraFormConnectionService
    {
        void SetAdPlan(AdPlan adPlan);
        void CheckServerConnection();
    }


    public class EntraFormConnectionService : IEntraFormConnectionService
    {
        private readonly ILogger<EntraFormConnectionService> _logger;
        private AdPlan _adPlan = new AdPlan();
        private AdManager _adManager;

        public EntraFormConnectionService(ILogger<EntraFormConnectionService> logger)
        {
            _logger = logger;
        }

        public void SetAdPlan(AdPlan adPlan)
        {
            _adPlan = adPlan;
            _logger.LogDebug("AD Plan set with connection details: {Server}, {User}", adPlan.Connection.Server, adPlan.Connection.User);
            _adManager = new AdManager(_adPlan.Connection);
            _logger.LogDebug("AdManager was set within specified connection");
        }

        
        public void CheckServerConnection()
        {
            if (_adPlan.Connection.Server == null || _adPlan.Connection.User == null || _adPlan.Connection.Password == null)
            {
                _logger.LogError("AD connection details are not provided. Please provide server, user, and password details.");
                Environment.Exit(1);
            }

            bool isConnected = TcpConnectionChecker.CheckTcpConnection(_adPlan.Connection.Server, 5985, 5000);
            if (isConnected == false)
            {
                _logger.LogError("Failed to connect to the server {Server} on port 5985. Please check your connection details for {Name} connection.", _adPlan.Connection.Server, _adPlan.Connection.Name);
                Environment.Exit(1);
            }
        }
    }
}
