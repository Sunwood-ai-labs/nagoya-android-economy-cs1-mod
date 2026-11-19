using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ICities;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    public sealed class NativeEmploymentTrialTick : ThreadingExtensionBase
    {
        public override void OnAfterSimulationFrame()
        {
            AndroidLifePilot.ObserveEmploymentTrialFrame();
        }
    }

    internal sealed class NativeEmploymentTrial
    {
        private const string Request = "native-employment-trial.json";
        public string CaseId;
        public int AdditionalResidents, Hours, Speed;
        public int? OpeningCash;
        public bool NormalEconomy;
        public bool ShareAndroidHouseholds, ShareMixedHouseholds;
        public uint[] CommuteAuditIds;
        public ushort[][] PedestrianLinks;
        public object PedestrianLinkResult;
        public bool TestLongWalkCommutes;
        public NativeCommuteRouteAudit CommuteAudit;
        public List<Dictionary<string, object>> SharedHouseholdAdmissions;
        public long OpeningCreditRaw;
        public Dictionary<string, object> OpeningEconomy;
        public DateTime Start, End, NextSample, Deadline;
        public uint StartFrame, EndFrame;
        public string Baseline, AfterAdmission;
        public object OpeningCapacity, AfterAdmissionCapacity;
        public bool Running;

        internal static NativeEmploymentTrial Prepare(string city)
        {
            string root = NativeCityRoadBuilder.Root;
            string path = Path.Combine(root, Request);
            if (!File.Exists(path)) return null;
            try
            {
                if (new FileInfo(path).Length > 4096) throw new FormatException("Employment trial request size");
                Dictionary<string, object> values = (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(path));
                if ((string)values["city"] != city) return null;
                string id = (string)values["caseId"];
                if (id.Length < 1 || id.Length > 60) throw new FormatException("Employment trial case name");
                foreach (char c in id)
                    if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '-')
                        throw new FormatException("Employment trial case name");
                SimulationManager simulation = SimulationManager.instance;
                DateTime expectedTime = DateTime.ParseExact((string)values["expectedGameTime"], "s", CultureInfo.InvariantCulture);
                // The display time is initialized after this callback. Use the saved frame clock.
                DateTime actualTime = simulation.FrameToTime(simulation.m_currentFrameIndex);
                if (Math.Abs((actualTime - expectedTime).TotalHours) > 2 ||
                    simulation.m_currentBuildIndex != (double)values["expectedBuildIndex"] ||
                    NetManager.instance.m_segmentCount != (double)values["expectedNetworkSegments"])
                    throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                        "Employment checkpoint mismatch (actual: {0:s}, build {1}, network segments {2})",
                        actualTime, simulation.m_currentBuildIndex, NetManager.instance.m_segmentCount));
                bool funded = values.ContainsKey("openingCash");
                object keepCash;
                bool keepOpeningCash = values.TryGetValue("keepOpeningCash", out keepCash) && (bool)keepCash;
                if (funded && keepOpeningCash) throw new FormatException("The trial cannot both keep and replace the opening cash");
                bool normalEconomy = funded || keepOpeningCash;
                int hours = ReadInt(values, "durationHours", 1, normalEconomy ? 720 : 96);
                int additional = ReadInt(values, "additionalAndroids", 0, NativeCityLedger.MaxResidents);
                object shareHomes;
                bool shareAndroidHouseholds = values.TryGetValue("shareAndroidHouseholds", out shareHomes) && (bool)shareHomes;
                object shareMixed;
                bool shareMixedHouseholds = values.TryGetValue("shareMixedHouseholds", out shareMixed) && (bool)shareMixed;
                if ((shareAndroidHouseholds && shareMixedHouseholds) || ((shareAndroidHouseholds || shareMixedHouseholds) && additional == 0))
                    throw new FormatException("Select one household sharing mode with additional Android residents");
                int? openingCash = funded ? (int?)ReadInt(values, "openingCash", 100000, 5000000) : null;
                uint[] commuteAuditIds = ReadCommuteAuditIds(values);
                ushort[][] pedestrianLinks = ReadPedestrianLinks(values);
                object longWalk;
                bool testLongWalk = values.TryGetValue("testLongWalkCommutes", out longWalk) && (bool)longWalk;
                if (testLongWalk && commuteAuditIds == null)
                    throw new FormatException("Specify the citizens participating in the long walk trial");
                File.Move(path, Path.Combine(root, "native-employment-" + id + ".request.json"));
                simulation.SimulationPaused = true;
                return new NativeEmploymentTrial {
                    CaseId=id, AdditionalResidents=additional, Hours=hours, OpeningCash=openingCash,
                    NormalEconomy=normalEconomy, Speed=normalEconomy ? 2 : 1, ShareAndroidHouseholds=shareAndroidHouseholds,
                    ShareMixedHouseholds=shareMixedHouseholds, CommuteAuditIds=commuteAuditIds, TestLongWalkCommutes=testLongWalk,
                    PedestrianLinks=pedestrianLinks
                };
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                return null;
            }
        }

        private static ushort[][] ReadPedestrianLinks(Dictionary<string, object> values)
        {
            object value;
            if (!values.TryGetValue("pedestrianLinks", out value)) return null;
            var candidates = (List<object>)value;
            if (candidates.Count == 0 || candidates.Count > NativePedestrianLink.MaxCandidates)
                throw new FormatException("Invalid count of pedestrian link candidates");
            var pairs = new List<ushort[]>(candidates.Count);
            foreach (List<object> candidate in candidates)
            {
                if (candidate.Count != 2) throw new FormatException("A pedestrian link needs exactly two node IDs");
                var pair = new ushort[2];
                for (int i = 0; i < pair.Length; i++)
                {
                    double id = (double)candidate[i];
                    if (id < 1 || id > ushort.MaxValue || Math.Floor(id) != id)
                        throw new FormatException("Invalid pedestrian link node ID");
                    pair[i] = (ushort)id;
                }
                if (pair[0] == pair[1]) throw new FormatException("A pedestrian link must connect different nodes");
                pairs.Add(pair);
            }
            return pairs.ToArray();
        }

        private static uint[] ReadCommuteAuditIds(Dictionary<string, object> values)
        {
            object value;
            if (!values.TryGetValue("commuteAuditIds", out value)) return null;
            var ids = (List<object>)value;
            if (ids.Count == 0 || ids.Count > NativeCommuteRouteAudit.MaxCitizens)
                throw new FormatException("Invalid number of citizens for the commute route audit");
            var unique = new HashSet<uint>();
            foreach (double id in ids)
                if (id <= 0 || id >= CitizenManager.instance.m_citizens.m_buffer.Length || Math.Floor(id) != id || !unique.Add((uint)id))
                    throw new FormatException("Invalid citizen ID for the commute route audit");
            var ordered = new List<uint>(unique);
            ordered.Sort();
            return ordered.ToArray();
        }

        private static int ReadInt(Dictionary<string, object> values, string key, int minimum, int maximum)
        {
            double raw = (double)values[key];
            if (raw < minimum || raw > maximum || raw != Math.Floor(raw))
                throw new FormatException("Employment trial value: " + key);
            return (int)raw;
        }

        internal void PrepareOpeningEconomy()
        {
            if (!NormalEconomy) return;
            OpeningEconomy = ReadEconomy();
            if (!OpeningCash.HasValue) return;
            EconomyManager economy = EconomyManager.instance;
            long before = economy.InternalCashAmount;
            long target = OpeningCash.Value * 100L;
            long credit = target - before;
            if (credit < 0 || credit > Int32.MaxValue)
                throw new InvalidOperationException("Opening cash falls outside the applicable one-time grant");
            if (credit > 0)
                economy.AddResource(EconomyManager.Resource.RewardAmount, (int)credit,
                    ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Level.None);
            if (economy.InternalCashAmount != target)
                throw new InvalidOperationException("The game's opening cash did not reach the requested balance");
            OpeningCreditRaw = credit;
        }

        private static Dictionary<string, object> ReadEconomy()
        {
            EconomyManager economy = EconomyManager.instance;
            long taxAndTransit = 0, operatingExpenses = 0;
            foreach (ItemClass.Service service in Enum.GetValues(typeof(ItemClass.Service)))
            {
                if (service == ItemClass.Service.None) continue;
                long income, expenses;
                economy.GetIncomeAndExpenses(service, ItemClass.SubService.None, ItemClass.Level.None, out income, out expenses);
                if (service == ItemClass.Service.Residential || service == ItemClass.Service.Commercial ||
                    service == ItemClass.Service.Industrial || service == ItemClass.Service.Office ||
                    service == ItemClass.Service.PublicTransport) taxAndTransit += income;
                operatingExpenses += expenses;
            }
            return new Dictionary<string, object> {
                {"cashRaw",economy.InternalCashAmount}, {"taxAndTransitIncomeRaw",taxAndTransit},
                {"operatingExpensesRaw",operatingExpenses},
                {"loanExpensesRaw",economy.GetLoanExpenses()}, {"policyExpensesRaw",economy.GetPolicyExpenses()}
            };
        }

        internal void SaveResult(string terminalSample, string status)
        {
            Dictionary<string, object> result = new Dictionary<string, object> {
                {"caseId",CaseId}, {"status",status}, {"requestedAdditionalAndroids",AdditionalResidents},
                {"durationHours",Hours}, {"startTicks",Start.Ticks.ToString(CultureInfo.InvariantCulture)},
                {"endTicks",End.Ticks.ToString(CultureInfo.InvariantCulture)}, {"startFrame",StartFrame}, {"endFrame",EndFrame},
                {"baseline",JsonData.Parse(Baseline)}, {"afterAdmission",JsonData.Parse(AfterAdmission)},
                {"end",JsonData.Parse(terminalSample)},
                {"urbanCapacity",new Dictionary<string, object> {
                    {"before",OpeningCapacity}, {"afterAdmission",AfterAdmissionCapacity}, {"end",NativeCityRoster.AuditCapacity()}
                }}
            };
            if (NormalEconomy)
                result["economy"] = new Dictionary<string, object> {
                    {"openingCashTargetRaw",OpeningCash.HasValue ? OpeningCash.Value * 100L : OpeningEconomy["cashRaw"]},
                    {"openingCreditRaw",OpeningCreditRaw},
                    {"beforeOpeningCredit",OpeningEconomy}, {"end",ReadEconomy()}
                };
            if (SharedHouseholdAdmissions != null)
            {
                CitizenManager manager = CitizenManager.instance;
                foreach (Dictionary<string, object> admission in SharedHouseholdAdmissions)
                {
                    ushort home = (ushort)admission["homeBuilding"];
                    uint unit = (uint)admission["homeUnit"];
                    admission["originalHumansStillInHousehold"] = AndroidLifePilot.CountResidentsAtAddress(manager, home, unit,
                        (uint[])admission["existingHumanIds"]);
                    admission["originalAndroidsStillInHousehold"] = AndroidLifePilot.CountResidentsAtAddress(manager, home, unit,
                        (uint[])admission["existingAndroidIds"]);
                }
                result["sharedHouseholdAdmissions"] = SharedHouseholdAdmissions;
            }
            if (ShareMixedHouseholds) result["sharedHouseholdMode"] = "existingMixedHousehold";
            if (CommuteAudit != null) result["commuteRouteAudit"] = CommuteAudit.Finish();
            if (PedestrianLinkResult != null) result["pedestrianLink"] = PedestrianLinkResult;
            string path = Path.Combine(NativeCityRoadBuilder.Root, "native-employment-" + CaseId + ".json");
            string pending = path + ".tmp";
            File.WriteAllText(pending, JsonData.Stringify(result), new UTF8Encoding(false));
            File.Move(pending, path);
        }
    }
}
