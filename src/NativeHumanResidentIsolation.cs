using System;
using System.Reflection;
using HarmonyLib;

namespace NagoyaStationBlock
{
    // Excludes only the prototype NativeHuman cohort from ResidentAI transfer economics.
    // All non-owned citizens and all other ResidentAI calls run the original method.
    internal static class NativeHumanResidentIsolation
    {
        private const string Owner = "sunwood.nagoya.native-human-resident-isolation";
        private static Harmony harmony;
        private static bool installed;
        private static string error = "";
        public static bool Installed { get { return installed; } }
        public static string Error { get { return error; } }

        public static bool Install()
        {
            if (installed) return true;
            error = "";
            Harmony candidate = null;
            try
            {
                MethodInfo startTransfer = FindExact(typeof(ResidentAI), "StartTransfer", new Type[] { typeof(uint), typeof(Citizen).MakeByRefType(), typeof(TransferManager.TransferReason), typeof(TransferManager.TransferOffer) });
                candidate = new Harmony(Owner);
                candidate.Patch(startTransfer, new HarmonyMethod(typeof(NativeHumanResidentIsolation).GetMethod("StartTransferPrefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null, null);
                harmony = candidate;
                installed = true;
                return true;
            }
            catch (Exception ex)
            {
                try { if (candidate != null) candidate.UnpatchAll(Owner); } catch { }
                harmony = null;
                installed = false;
                error = "resident_isolation_install_failed:" + ex.GetBaseException().Message;
                return false;
            }
        }

        public static void Uninstall()
        {
            try { if (harmony != null) harmony.UnpatchAll(Owner); }
            catch (Exception ex) { error = "resident_isolation_uninstall_failed:" + ex.GetBaseException().Message; }
            harmony = null;
            installed = false;
        }

        private static bool StartTransferPrefix(uint __0, ref Citizen __1, TransferManager.TransferReason __2, TransferManager.TransferOffer __3)
        {
            ushort instanceId;
            string ownershipReason;
            if (!NativeHumanRouteLoop.TryGetOwnedResident(__0, ref __1, out instanceId, out ownershipReason)) return true;
            NativeHumanTrace.RecordResidentTransferSkipped(instanceId, __0, __2, __3, ownershipReason);
            return false;
        }

        private static MethodInfo FindExact(Type owner, string name, Type[] parameterTypes)
        {
            foreach (MethodInfo method in owner.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != name) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length) continue;
                bool same = true;
                for (int i = 0; i < parameters.Length; i++) if (parameters[i].ParameterType != parameterTypes[i]) same = false;
                if (same) return method;
            }
            throw new MissingMethodException(owner.FullName, name);
        }
    }
}
