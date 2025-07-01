using declarad.AdEntities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace declarad.Utils
{
    public class AdManager
    {
        private static AdConnection _adConnection;
        private const int LDAP_PORT = 389;
        private const int LDAPS_PORT = 636;

        public AdManager(AdConnection adConnection)
        {
            _adConnection = adConnection;
        }


    }
}
