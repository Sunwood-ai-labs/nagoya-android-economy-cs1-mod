using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework.Math;
using HarmonyLib;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeCommuteRouteAudit : IDisposable
    {
        private const string PatchId = "sunwood.nagoya.native-commute-route-audit";
        internal const int MaxCitizens = 48;
        private static NativeCommuteRouteAudit active;
        private readonly Harmony harmony;
        private readonly bool testLongWalk;
        private readonly HashSet<uint> remaining;
        private readonly List<Probe> probes = new List<Probe>();
        private uint startingCitizen;

        private sealed class Probe
        {
            public uint Citizen, Frame, StockPath, WalkingPath;
            public ushort From, To;
            public Vector3 Start, End;
            public PathUnit.Position StartLane, EndLane;
            public string UnqueuedReason;
            public bool WalkingPathAssigned;
        }

        internal NativeCommuteRouteAudit(uint[] citizens, bool allowLongWalk)
        {
            testLongWalk = allowLongWalk;
            remaining = new HashSet<uint>(citizens);
            MethodInfo method = typeof(CitizenAI).GetMethod("StartPathFind",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new Type[] {typeof(ushort), typeof(CitizenInstance).MakeByRefType(), typeof(Vector3), typeof(Vector3),
                    typeof(VehicleInfo), typeof(bool), typeof(bool)}, null);
            if (method == null) throw new MissingMethodException("CitizenAI.StartPathFind");
            MethodInfo postfix = typeof(NativeCommuteRouteAudit).GetMethod("AfterStockPathRequest", BindingFlags.Static | BindingFlags.NonPublic);
            harmony = new Harmony(PatchId);
            harmony.Patch(method, null, new HarmonyMethod(postfix), null, null);
            active = this;
        }

        internal static bool StartMovement(HumanAI ai, uint id, ref Citizen citizen, ushort from, ushort to)
        {
            NativeCommuteRouteAudit audit = active;
            if (audit == null || citizen.CurrentLocation != Citizen.Location.Home || to != citizen.m_workBuilding ||
                from != citizen.m_homeBuilding || !audit.remaining.Contains(id))
                return ai.StartMoving(id, ref citizen, from, to);
            audit.startingCitizen = id;
            try { return ai.StartMoving(id, ref citizen, from, to); }
            finally { audit.startingCitizen = 0; }
        }

        private static void AfterStockPathRequest(ref CitizenInstance __1, Vector3 __2, Vector3 __3, bool __result)
        {
            NativeCommuteRouteAudit audit = active;
            if (audit == null || audit.startingCitizen == 0 || __1.m_citizen != audit.startingCitizen ||
                __1.m_sourceBuilding == 0 || __1.m_targetBuilding == 0 || !audit.remaining.Remove(__1.m_citizen)) return;

            SimulationManager sim = SimulationManager.instance;
            PathManager paths = PathManager.instance;
            var probe = new Probe {Citizen=__1.m_citizen, From=__1.m_sourceBuilding, To=__1.m_targetBuilding,
                Frame=sim.m_currentFrameIndex, Start=__2, End=__3};
            audit.probes.Add(probe);
            if (__result && __1.m_path != 0 && paths.AddPathReference(__1.m_path)) probe.StockPath = __1.m_path;
            PathUnit.Position secondStart, secondEnd;
            bool start = FindSidewalk(__2, out probe.StartLane, out secondStart);
            bool end = FindSidewalk(__3, out probe.EndLane, out secondEnd);
            if (!start || !end)
            {
                probe.UnqueuedReason = !start && !end ? "missingBothSidewalks" : !start ? "missingStartSidewalk" : "missingEndSidewalk";
                return;
            }
            var random = new Randomizer(probe.Citizen);
            uint walkingPath;
            if (!paths.CreatePath(out walkingPath, ref random, sim.m_currentBuildIndex, probe.StartLane, secondStart,
                probe.EndLane, secondEnd, NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None,
                VehicleInfo.VehicleCategory.None, 20000f, false, false, audit.testLongWalk, false))
            {
                probe.UnqueuedReason = "notQueued";
                return;
            }
            probe.WalkingPath = walkingPath;
            if (!audit.testLongWalk || probe.StockPath == 0 || !paths.AddPathReference(walkingPath)) return;
            // The citizen and audit each own a reference. Native movement still handles spawned travel and arrival.
            paths.ReleasePath(__1.m_path);
            __1.m_path = walkingPath;
            __1.m_flags |= CitizenInstance.Flags.WaitingPath;
            probe.WalkingPathAssigned = true;
        }

        private static bool FindSidewalk(Vector3 position, out PathUnit.Position first, out PathUnit.Position second)
        {
            float firstDistance, secondDistance;
            return PathManager.FindPathPosition(position, ItemClass.Service.Road, ItemClass.Service.Beautification,
                NetInfo.LaneType.Pedestrian, VehicleInfo.VehicleType.None, VehicleInfo.VehicleCategory.None, VehicleInfo.VehicleType.None,
                false, false, 40f, false, true, out first, out second, out firstDistance, out secondDistance);
        }

        internal object Finish()
        {
            var rows = new List<object>(probes.Count);
            foreach (Probe probe in probes)
                rows.Add(new Dictionary<string, object> {
                    {"citizenId",probe.Citizen}, {"frame",probe.Frame}, {"from",probe.From}, {"to",probe.To},
                    {"start",new float[] {probe.Start.x,probe.Start.y,probe.Start.z}},
                    {"end",new float[] {probe.End.x,probe.End.y,probe.End.z}},
                    {"startSidewalkSegment",probe.StartLane.m_segment}, {"endSidewalkSegment",probe.EndLane.m_segment},
                    {"stock",Describe(probe.StockPath,"notRetained")}, {"pedestrian",Describe(probe.WalkingPath,probe.UnqueuedReason)},
                    {"walkingPathAssigned",probe.WalkingPathAssigned}
                });
            var unobserved = new List<uint>(remaining);
            unobserved.Sort();
            Dispose();
            return new Dictionary<string, object> {
                {"allowLongWalk",testLongWalk}, {"probes",rows}, {"unobservedCitizens",unobserved}
            };
        }

        private static object Describe(uint id, string unqueuedReason)
        {
            if (id == 0) return new Dictionary<string, object> {{"result",unqueuedReason}};
            PathUnit unit = PathManager.instance.m_pathUnits.m_buffer[id];
            bool ready = (unit.m_pathFindFlags & PathUnit.FLAG_READY) != 0;
            string result = ready ? "ready" : (unit.m_pathFindFlags & PathUnit.FLAG_FAILED) != 0 ? "failed" : "pending";
            return new Dictionary<string, object> {{"result",result},{"lengthMeters",ready ? (object)unit.m_length : null}};
        }

        public void Dispose()
        {
            if (active != this) return;
            active = null;
            harmony.UnpatchAll(PatchId);
            PathManager paths = PathManager.instance;
            foreach (Probe probe in probes)
            {
                if (probe.StockPath != 0) paths.ReleasePath(probe.StockPath);
                if (probe.WalkingPath != 0) paths.ReleasePath(probe.WalkingPath);
            }
        }
    }
}
