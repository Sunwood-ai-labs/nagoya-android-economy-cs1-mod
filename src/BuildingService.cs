using System;
using System.Collections.Generic;

namespace NagoyaRoadDelivery
{
    public sealed partial class CitywideScene
    {
        private void ValidateBuildingService(List<RegionPlan> plans)
        {
            if (plans.Count != 56)
                throw new FormatException("Expected every citywide region");

            HashSet<int> configuredNodes = new HashSet<int>();
            foreach (object entry in buildingTargets)
                configuredNodes.Add(Int(Map(entry), "node"));

            HashSet<int> representedNodes = new HashSet<int>();
            HashSet<int> regionIds = new HashSet<int>();
            foreach (RegionPlan region in plans)
            {
                if (region.Id < 0 || region.Id >= 56 || !regionIds.Add(region.Id))
                    throw new FormatException("Invalid citywide region identity");
                if (region.Targets.Length != 3)
                    throw new FormatException("Three building service slots required in each region");
                HashSet<int> local = new HashSet<int>();
                foreach (int node in region.Targets)
                {
                    if (node < 0 || node >= graph.NodeCount || !configuredNodes.Contains(node) || !local.Add(node))
                        throw new FormatException("Unbound building service slot in region " + region.Id);
                    representedNodes.Add(node);
                }
            }
            if (!representedNodes.SetEquals(configuredNodes))
                throw new FormatException("Some building identities were not assigned to a region");
        }
    }
}
