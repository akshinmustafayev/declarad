using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace entraform.Services
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
