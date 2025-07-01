using Microsoft.Extensions.Logging;

namespace declarad.Services
{
    public interface IUserService
    {
        void Add(string username, string ou);
    }

    public class UserService : IUserService
    {
        private readonly ILogger<UserService> _logger;

        public UserService(ILogger<UserService> logger) {
            _logger = logger;
        }

        public void Add(string username, string ou)
        {
            Console.WriteLine($"✅ User '{username}' would be added to OU '{ou}'");
        }
    }
}
