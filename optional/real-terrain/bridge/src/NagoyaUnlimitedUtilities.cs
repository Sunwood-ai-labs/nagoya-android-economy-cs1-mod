using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace SkylinesAgentBridge
{
    internal static class NagoyaUnlimitedUtilities
    {
        private const string Owner = "sunwood.nagoya.unlimited-utilities";
        private static Harmony patch;
        private static volatile bool enabled;
        private static long electricitySupplied;
        private static long waterSupplied;
        private static long sewageAccepted;

        internal static CommandResult SetEnabled(bool value)
        {
            if (!value)
            {
                Disable();
                return Status();
            }
            if (!NagoyaMapJob.CanEditNativeMap)
                return CommandResult.Fail("Unlimited utilities require a verified Nagoya map.");
            if (enabled)
                return Status();

            try
            {
                var position = typeof(Vector3);
                var amount = typeof(int);
                var yesNoOutput = typeof(bool).MakeByRefType();
                var pollutionOutput = typeof(byte).MakeByRefType();
                patch = new Harmony(Owner);
                Prefix(typeof(ElectricityManager), "CheckElectricity", typeof(void), "ElectricityAvailable", position, yesNoOutput);
                Prefix(typeof(ElectricityManager), "TryFetchElectricity", amount, "SupplyElectricity", position, amount, amount);
                Prefix(typeof(WaterManager), "CheckWater", typeof(void), "WaterAvailable", position, yesNoOutput, yesNoOutput, pollutionOutput);
                Prefix(typeof(WaterManager), "TryFetchWater", amount, "SupplyWater", position, amount, amount, pollutionOutput);
                Prefix(typeof(WaterManager), "TryDumpSewage", amount, "AcceptSewage", position, amount, amount);
                Interlocked.Exchange(ref electricitySupplied, 0);
                Interlocked.Exchange(ref waterSupplied, 0);
                Interlocked.Exchange(ref sewageAccepted, 0);
                enabled = true;
                Debug.Log("[NagoyaUnlimitedUtilities] Enabled for the verified Nagoya map.");
                return Status();
            }
            catch (Exception error)
            {
                Disable();
                return CommandResult.Fail(error.GetBaseException().Message);
            }
        }

        private static void Prefix(Type owner, string original, Type result, string replacement, params Type[] signature)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = owner.GetMethod(original, flags, null, signature, null);
            if (method == null || method.ReturnType != result)
                throw new MissingMethodException(owner.FullName, original);
            var prefix = typeof(NagoyaUnlimitedUtilities).GetMethod(replacement, BindingFlags.Static | BindingFlags.NonPublic);
            patch.Patch(method, new HarmonyMethod(prefix), null, null, null);
        }

        internal static void Disable()
        {
            enabled = false;
            if (patch == null)
                return;
            patch.UnpatchAll(Owner);
            patch = null;
        }

        internal static CommandResult Status()
        {
            return CommandResult.FromJson("{\"ok\":true,\"enabled\":" + JsonUtil.Bool(enabled)
                + ",\"electricitySupplied\":" + Interlocked.Read(ref electricitySupplied)
                + ",\"waterSupplied\":" + Interlocked.Read(ref waterSupplied)
                + ",\"sewageAccepted\":" + Interlocked.Read(ref sewageAccepted) + "}");
        }

        private static bool ElectricityAvailable(ref bool __1)
        {
            if (!enabled) return true;
            __1 = true;
            return false;
        }

        private static bool SupplyElectricity(int __2, ref int __result)
        {
            if (!enabled) return true;
            __result = Math.Max(0, __2);
            Interlocked.Add(ref electricitySupplied, __result);
            return false;
        }

        private static bool WaterAvailable(ref bool __1, ref bool __2, ref byte __3)
        {
            if (!enabled) return true;
            __1 = true;
            __2 = true;
            __3 = 0;
            return false;
        }

        private static bool SupplyWater(int __2, ref byte __3, ref int __result)
        {
            if (!enabled) return true;
            __result = Math.Max(0, __2);
            __3 = 0;
            Interlocked.Add(ref waterSupplied, __result);
            return false;
        }

        private static bool AcceptSewage(int __2, ref int __result)
        {
            if (!enabled) return true;
            __result = Math.Max(0, __2);
            Interlocked.Add(ref sewageAccepted, __result);
            return false;
        }
    }
}
