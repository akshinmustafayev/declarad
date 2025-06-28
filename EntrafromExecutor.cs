using Microsoft.Extensions.Logging;
using Octopus.CoreParsers.Hcl;
using Sprache;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace entraform
{
    public class EntrafromExecutor
    {
        /*
        //private readonly IHclMapper _mapper;
        private readonly ILogger<EntrafromExecutor> _logger;
        private readonly ExecutionOptions _options;

        public EntrafromExecutor(
            //IHclMapper mapper, 
            ILogger<EntrafromExecutor> logger, 
            ExecutionOptions options)
        {
            //_mapper = mapper;
            _logger = logger;
            _options = options;
        }

        public void Run()
        {
            // Init variables
            StringBuilder hclCommand = new StringBuilder();

            // Combine parsed files in one line
            if (_options.Files.Count() > 0)
            {
                foreach(string file in _options.Files)
                {
                    if (File.Exists(file))
                    {
                        hclCommand.Append(File.ReadAllText(file));
                    }
                }
            }

            // Log combined HCL command
            _logger.LogDebug("Parsed HCL:\n{Options}", hclCommand.ToString());

            /*
            var filePath = args.FirstOrDefault() ?? "config.hcl";

            if (!File.Exists(filePath))
            {
                _logger.LogError("HCL file '{FilePath}' not found.", filePath);
                return;
            }

            var hclText = File.ReadAllText(filePath);
            var parsed = HclParser.HclTemplate.Parse(hclText);

            //var config = _mapper.Map(parsed);

            //_logger.LogInformation("Parsed {UserCount} users, {GroupCount} groups, {OuCount} OUs.",
            //   config.Users.Count, config.Groups.Count, config.Ous.Count);

            // Do something meaningful with the parsed config
            //foreach (var user in config.Users)
            //{
            //    _logger.LogInformation("User: {Username}, Email: {Email}", user.Username, user.Email);
            //}
        }
        */
    }
}
