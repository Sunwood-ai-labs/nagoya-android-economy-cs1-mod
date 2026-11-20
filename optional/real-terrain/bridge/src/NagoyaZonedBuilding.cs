using System;
using ColossalFramework.Math;
using UnityEngine;

namespace SkylinesAgentBridge
{
    internal static class NagoyaZonedBuilding
    {
        internal static CommandResult Place(string body)
        {
            string name = JsonUtil.GetString(body, "buildingPrefab", "");
            string zoneName = JsonUtil.GetString(body, "zone", "");
            ItemClass.Zone zone;
            try { zone = (ItemClass.Zone)Enum.Parse(typeof(ItemClass.Zone), zoneName); }
            catch (ArgumentException) { return CommandResult.Fail("Invalid zone name."); }
            if (zone == ItemClass.Zone.Unzoned || !Enum.IsDefined(typeof(ItemClass.Zone), zone))
                return CommandResult.Fail("A growable requires a valid assigned zone.");
            BuildingInfo prefab = PrefabCollection<BuildingInfo>.FindLoaded(name);
            if (prefab == null || AssetPolicy.IsBlockedBuildingPrefab(prefab))
                return CommandResult.Fail("The requested building prefab is unavailable.");
            int width = prefab.GetWidth(), depth = prefab.GetLength();
            if (width < 1 || width > 4 || depth < 1 || depth > 4)
                return CommandResult.Fail("The prefab footprint is outside the 1–4 cell zone size.");

            var requested = new Vector3(JsonUtil.GetNumber(body, "x", float.NaN), 0,
                JsonUtil.GetNumber(body, "z", float.NaN));
            if (float.IsNaN(requested.x) || float.IsNaN(requested.z)
                || Mathf.Abs(requested.x) > 8640 || Mathf.Abs(requested.z) > 8640)
                return CommandResult.Fail("Requested lot is outside the map.");

            var zoning = ZoneManager.instance;
            var best = new Vector3();
            float bestAngle = 0;
            float distance = 42 * 42;
            ushort chosenBlock = 0;
            int chosenRow = 0;
            for (ushort id = 1; id < zoning.m_blocks.m_buffer.Length; id++)
            {
                ZoneBlock block = zoning.m_blocks.m_buffer[id];
                var offset = block.m_position - requested;
                offset.y = 0;
                if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0 || offset.sqrMagnitude > 80 * 80)
                    continue;
                var across = new Vector3(Mathf.Cos(block.m_angle), 0, Mathf.Sin(block.m_angle)) * 8;
                var along = new Vector3(across.z, 0, -across.x);
                for (int row = 0; row + width <= block.RowCount; row++)
                {
                    if (!Available(block, row, width, depth, zone))
                        continue;
                    Vector3 center = block.m_position + across * ((depth - 1) * .5f - 3.5f)
                        + along * (row + (width - 1) * .5f - 3.5f);
                    offset = center - requested;
                    offset.y = 0;
                    if (offset.sqrMagnitude >= distance)
                        continue;
                    float angle = block.m_angle + Mathf.PI * .5f;
                    for (int direction = 0; direction < 4; direction++, angle += Mathf.PI * .5f)
                    {
                        int blocked;
                        if (!zoning.CheckSpace(center, angle, width, depth, out blocked))
                            continue;
                        distance = offset.sqrMagnitude;
                        chosenBlock = id;
                        chosenRow = row;
                        best = center;
                        bestAngle = angle;
                        break;
                    }
                }
            }
            if (chosenBlock == 0)
                return CommandResult.Fail("No free road-facing zone cells for this prefab within 42 m.");
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            ushort building = 0;
            if (!dryRun)
            {
                var sim = SimulationManager.instance;
                Randomizer randomizer = sim.m_randomizer;
                if (!BuildingManager.instance.CreateBuilding(out building, ref randomizer, prefab,
                    best, bestAngle, depth, sim.m_currentBuildIndex))
                    return CommandResult.Fail("Native building allocation failed.");
                sim.m_randomizer = randomizer;
                sim.m_currentBuildIndex++;
                for (int row = chosenRow; row < chosenRow + width; row++)
                    for (int col = 0; col < depth; col++)
                        zoning.m_blocks.m_buffer[chosenBlock].SetZone(col, row, zone);
                zoning.m_blocks.m_buffer[chosenBlock].RefreshZoning(chosenBlock);
                zoning.UpdateBlock(chosenBlock);
            }
            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":" + JsonUtil.Bool(dryRun)
                + ",\"buildingId\":" + building + ",\"buildingPrefab\":\"" + JsonUtil.Escape(name)
                + "\",\"zone\":\"" + zone + "\",\"block\":" + chosenBlock
                + ",\"assignedCells\":" + (width * depth) + ",\"x\":" + JsonUtil.Number(best.x)
                + ",\"y\":" + JsonUtil.Number(best.y) + ",\"z\":" + JsonUtil.Number(best.z) + "}");
        }

        private static bool Available(ZoneBlock block, int startRow, int width, int depth, ItemClass.Zone zone)
        {
            for (int row = startRow; row < startRow + width; row++)
            {
                for (int col = 0; col < depth; col++)
                {
                    ulong bit = 1UL << (row * 8 + col);
                    ItemClass.Zone current = block.GetZone(col, row);
                    if ((block.m_valid & bit) == 0 || ((block.m_shared | block.m_occupied1 | block.m_occupied2) & bit) != 0
                        || (current != ItemClass.Zone.Unzoned && current != zone))
                        return false;
                }
            }
            return true;
        }
    }
}
