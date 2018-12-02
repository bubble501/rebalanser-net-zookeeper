using System.Collections.Generic;

namespace Rebalanser.ZooKeeper.Zk
{
    public class ResourcesZnode
    {
        public ResourcesZnodeData ResourceAssignments { get; set; }
        public List<string> Resources { get; set; } 
        public int Version { get; set; }
    }
}