using System;
using System.Collections.Generic;

namespace NagoyaRoadDelivery
{
    internal sealed class NativeWardWorkTrips
    {
        private sealed class Totals
        {
            internal int Arrivals, PathFailures;
            internal readonly HashSet<uint> CitizensArrived = new HashSet<uint>();
            internal readonly HashSet<uint> CitizensWithFailure = new HashSet<uint>();
            internal object FirstFailure;

            internal void Record(uint id, ushort home, ushort work, uint path, bool arrived)
            {
                if (arrived)
                {
                    Arrivals++;
                    CitizensArrived.Add(id);
                    return;
                }
                PathFailures++;
                CitizensWithFailure.Add(id);
                if (FirstFailure == null)
                    FirstFailure = new Dictionary<string, object> {
                        {"citizenId",id}, {"home",home}, {"work",work}, {"pathId",path}
                    };
            }

            internal object Report()
            {
                return new Dictionary<string, object> {
                    {"arrivalCallbacks",Arrivals}, {"citizensArrived",CitizensArrived.Count},
                    {"pathFailureCallbacks",PathFailures}, {"citizensWithPathFailure",CitizensWithFailure.Count},
                    {"firstFailure",FirstFailure}
                };
            }
        }

        private sealed class Ward
        {
            internal readonly Totals Humans = new Totals();
            internal readonly Totals Androids = new Totals();
        }

        private readonly SortedDictionary<string, Ward> byHome = new SortedDictionary<string, Ward>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, Ward> byWork = new SortedDictionary<string, Ward>(StringComparer.Ordinal);

        internal void Record(uint id, ushort home, ushort work, uint path, bool android, bool arrived)
        {
            Get(byHome, home, android).Record(id, home, work, path, arrived);
            Get(byWork, work, android).Record(id, home, work, path, arrived);
        }

        internal object Report()
        {
            return new Dictionary<string, object> {{"byHomeWard",Report(byHome)}, {"byWorkWard",Report(byWork)}};
        }

        private static Totals Get(SortedDictionary<string, Ward> wards, ushort site, bool android)
        {
            string name = NativeCityLedger.RegisteredWard(site);
            if (String.IsNullOrEmpty(name)) name = "unassigned";
            Ward ward;
            if (!wards.TryGetValue(name, out ward)) wards.Add(name, ward = new Ward());
            return android ? ward.Androids : ward.Humans;
        }

        private static object Report(SortedDictionary<string, Ward> wards)
        {
            var result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, Ward> entry in wards)
                result.Add(entry.Key, new Dictionary<string, object> {
                    {"humans",entry.Value.Humans.Report()}, {"androids",entry.Value.Androids.Report()}
                });
            return result;
        }
    }
}
