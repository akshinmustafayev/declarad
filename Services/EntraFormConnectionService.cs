using entraform.AdEntities;
using entraform.Utils;
using Microsoft.Extensions.Logging;
using Naos.Recipes.WinRM;

namespace entraform.Services
{
    public interface IEntraFormConnectionService
    {
        void SetAdPlan(AdPlan adPlan);
        void InitMachineManager();
        MachineManager GetMachineManager();
        void CheckServerConnection();
    }


    public class EntraFormConnectionService : IEntraFormConnectionService
    {
        private readonly ILogger<EntraFormConnectionService> _logger;
        private AdPlan _adPlan = new AdPlan();
        MachineManager _machineManager;

        public EntraFormConnectionService(ILogger<EntraFormConnectionService> logger)
        {
            _logger = logger;
        }

        public void SetAdPlan(AdPlan adPlan)
        {
            _adPlan = adPlan;
            _logger.LogDebug("AD Plan set with connection details: {Server}, {User}", adPlan.Connection.Server, adPlan.Connection.User);
        }

        public void InitMachineManager()
        {
            _machineManager = new MachineManager(
                _adPlan.Connection.Server,
                _adPlan.Connection.User,
                _adPlan.Connection.Password.ToSecureString(),
                autoManageTrustedHosts: true);
            _logger.LogDebug("Connection manager initialized");
        }

        public MachineManager GetMachineManager()
        {
            return _machineManager;
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
