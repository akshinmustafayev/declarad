using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace entraform.AdEntities.Group
{
    public class AdGroup
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Members { get; set; } = new List<string>();
        public List<string> MemberOf { get; set; } = new List<string>();
    }
}
