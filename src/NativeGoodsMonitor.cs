using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using NativeBuilding = global::Building;

namespace NagoyaRoadDelivery
{
    // Observe the actual game economy without changing offers, stock, or truck assignments.
    public sealed class NativeGoodsMonitor : MonoBehaviour
    {
        private const string Owner = "sunwood.nagoya.native-goods-monitor";
        private const int MaxOffers = 2048;
        private const uint OfferHistoryFrames = 4096;
        private static volatile NativeGoodsMonitor current;
        private Harmony harmony;
        private readonly Dictionary<ushort, Offer> incoming = new Dictionary<ushort, Offer>();
        private readonly Dictionary<ushort, Offer> outgoing = new Dictionary<ushort, Offer>();
        private int sampleQueued;
        private volatile bool stopping;
        private string sample;
        private float nextSample;
        private string output;
        private string run;

        private struct Offer
        {
            public uint Frame;
            public uint BuildIndex;
            public int LoadCount;
            public int Priority;
        }

        public void Start()
        {
            output = Path.Combine(RuntimePaths.Data(@"citywide-live-v1"), "native-goods-state.json");
            run = DateTime.UtcNow.ToString("o");
            harmony = new Harmony(Owner);
            try
            {
                Install("AddIncomingOffer", "ObserveIncoming");
                Install("AddOutgoingOffer", "ObserveOutgoing");
                current = this;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(Owner);
                harmony = null;
                Debug.LogException(error);
            }
        }

        private void Install(string method, string hook)
        {
            MethodInfo target = typeof(TransferManager).GetMethod(method, new Type[]{
                typeof(TransferManager.TransferReason), typeof(TransferManager.TransferOffer)
            });
            if (target == null) throw new MissingMethodException("TransferManager", method);
            MethodInfo postfix = typeof(NativeGoodsMonitor).GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(target, null, new HarmonyMethod(postfix), null, null);
        }

        private static void ObserveIncoming(TransferManager.TransferReason __0, TransferManager.TransferOffer __1)
        {
            NativeGoodsMonitor monitor = current;
            if (monitor != null && __0 == TransferManager.TransferReason.Goods)
                monitor.Observe(__1, monitor.incoming);
        }

        private static void ObserveOutgoing(TransferManager.TransferReason __0, TransferManager.TransferOffer __1)
        {
            NativeGoodsMonitor monitor = current;
            if (monitor != null && __0 == TransferManager.TransferReason.Goods)
                monitor.Observe(__1, monitor.outgoing);
        }

        private void Observe(TransferManager.TransferOffer offer, Dictionary<ushort, Offer> offers)
        {
            ushort id = offer.Building;
            if (id == 0 || offer.Amount <= 0 || !Monitor.TryEnter(offers)) return;
            try
            {
                NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
                if (id >= buildings.Length || (buildings[id].m_flags & NativeBuilding.Flags.Created) == 0) return;
                if (!offers.ContainsKey(id) && offers.Count >= MaxOffers) return;
                offers[id] = new Offer {
                    Frame = SimulationManager.instance.m_currentFrameIndex,
                    BuildIndex = buildings[id].m_buildIndex,
                    LoadCount = offer.Amount,
                    Priority = offer.Priority
                };
            }
            finally { Monitor.Exit(offers); }
        }

        public void Update()
        {
            string observed = Interlocked.Exchange<string>(ref sample, null);
            if (observed != null)
            {
                try { SaveSnapshot(observed); }
                catch (Exception error) { Debug.LogException(error); }
            }
            if (current != this || Time.realtimeSinceStartup < nextSample) return;
            nextSample = Time.realtimeSinceStartup + 8;
            if (Interlocked.CompareExchange(ref sampleQueued, 1, 0) != 0) return;
            SimulationManager.instance.AddAction(delegate {
                try { if (!stopping) Interlocked.Exchange<string>(ref sample, BuildSnapshot()); }
                catch (Exception error) { Debug.LogException(error); }
                finally { Interlocked.Exchange(ref sampleQueued, 0); }
            });
        }

        private string BuildSnapshot()
        {
            uint frame = SimulationManager.instance.m_currentFrameIndex;
            NativeBuilding[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            Dictionary<ushort, Offer> recentIncoming = RecentOffers(incoming, buildings, frame);
            Dictionary<ushort, Offer> recentOutgoing = RecentOffers(outgoing, buildings, frame);
            List<object> shops = new List<object>();
            List<object> suppliers = new List<object>();
            int shopCount = 0, supplierCount = 0, stocked = 0, workingShops = 0;
            for (int index = 1; index < buildings.Length; index++)
            {
                ushort id = (ushort)index;
                NativeBuilding building = buildings[index];
                if ((building.m_flags & NativeBuilding.Flags.Created) == 0 || (building.m_flags & NativeBuilding.Flags.Deleted) != 0) continue;
                BuildingInfo prefab = building.Info;
                if (prefab == null) continue;
                BuildingAI ai = prefab.m_buildingAI;
                WarehouseAI warehouse = ai as WarehouseAI;
                bool shop = ai is CommercialBuildingAI;
                bool active = (building.m_flags & NativeBuilding.Flags.Active) != 0;
                if (shop)
                {
                    shopCount++;
                    if (active) workingShops++;
                }
                else if (ai is IndustrialBuildingAI || warehouse != null) supplierCount++;
                else continue;

                int stock, capacity;
                if (warehouse != null)
                {
                    if (warehouse.GetActualTransferReason(id, ref building) != TransferManager.TransferReason.Goods) continue;
                    stock = building.m_customBuffer1 * 100;
                    capacity = warehouse.m_storageCapacity;
                }
                else ai.GetMaterialAmount(id, ref building, TransferManager.TransferReason.Goods, out stock, out capacity);
                if (capacity <= 0 && stock <= 0 && !shop) continue;
                if (active && !shop && stock > 0) stocked++;
                List<object> rows = shop ? shops : suppliers;
                if (rows.Count >= 128) continue;
                Dictionary<string, object> row = new Dictionary<string, object> {
                    {"buildingId", id}, {"buildIndex", building.m_buildIndex}, {"prefab", prefab.name},
                    {"active", active}, {"flags", building.m_flags.ToString()}, {"stock", stock}, {"capacity", capacity},
                    {"x", building.m_position.x}, {"z", building.m_position.z}
                };
                Offer offer;
                if ((shop ? recentIncoming : recentOutgoing).TryGetValue(id, out offer))
                {
                    row["observedOfferFrame"] = offer.Frame;
                    row["observedOfferLoads"] = offer.LoadCount;
                    row["observedOfferPriority"] = offer.Priority;
                }
                rows.Add(row);
            }
            Dictionary<string, object> snapshot = new Dictionary<string, object> {
                {"utc", DateTime.UtcNow.ToString("o")}, {"runStartedUtc", run},
                {"assemblyMvid", GetType().Assembly.ManifestModule.ModuleVersionId.ToString()},
                {"simulationFrame", frame}, {"mode", "native-observation"},
                {"offerHistoryFrames", OfferHistoryFrames}, {"recentIncomingBuildingOffers", recentIncoming.Count},
                {"recentOutgoingBuildingOffers", recentOutgoing.Count}, {"commercialBuildings", shopCount},
                {"activeCommercialBuildings", workingShops}, {"industrialOrWarehouseBuildings", supplierCount},
                {"activeGoodsSuppliersWithStock", stocked}, {"shops", shops}, {"goodsSuppliers", suppliers}
            };
            return JsonData.Stringify(snapshot);
        }

        private void SaveSnapshot(string observed)
        {
            string temp = output + ".tmp";
            File.WriteAllText(temp, observed, new UTF8Encoding(false));
            if (File.Exists(output)) File.Replace(temp, output, null);
            else File.Move(temp, output);
        }

        private static Dictionary<ushort, Offer> RecentOffers(Dictionary<ushort, Offer> source, NativeBuilding[] buildings, uint frame)
        {
            Dictionary<ushort, Offer> recent = new Dictionary<ushort, Offer>();
            lock (source)
            {
                List<ushort> expired = new List<ushort>();
                foreach (KeyValuePair<ushort, Offer> entry in source)
                {
                    ushort id = entry.Key;
                    if (unchecked(frame - entry.Value.Frame) >= OfferHistoryFrames ||
                        (buildings[id].m_flags & NativeBuilding.Flags.Created) == 0 || buildings[id].m_buildIndex != entry.Value.BuildIndex)
                        expired.Add(id);
                    else recent.Add(id, entry.Value);
                }
                foreach (ushort id in expired) source.Remove(id);
            }
            return recent;
        }

        public void OnDestroy()
        {
            stopping = true;
            if (current == this) current = null;
            if (harmony != null) harmony.UnpatchAll(Owner);
        }
    }
}
