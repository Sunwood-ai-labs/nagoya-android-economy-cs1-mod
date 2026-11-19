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
        public int AdditionalResidents, Hours;
        public DateTime Start, End, NextSample, Deadline;
        public uint StartFrame, EndFrame;
        public string Baseline, AfterAdmission;
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
                int hours = ReadInt(values, "durationHours", 1, 96);
                int additional = ReadInt(values, "additionalAndroids", 0, NativeCityLedger.MaxResidents);
                File.Move(path, Path.Combine(root, "native-employment-" + id + ".request.json"));
                simulation.SimulationPaused = true;
                return new NativeEmploymentTrial { CaseId=id, AdditionalResidents=additional, Hours=hours };
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                return null;
            }
        }

        private static int ReadInt(Dictionary<string, object> values, string key, int minimum, int maximum)
        {
            double raw = (double)values[key];
            if (raw < minimum || raw > maximum || raw != Math.Floor(raw))
                throw new FormatException("Employment trial value: " + key);
            return (int)raw;
        }

        internal void SaveResult(string terminalSample, string status)
        {
            Dictionary<string, object> result = new Dictionary<string, object> {
                {"caseId",CaseId}, {"status",status}, {"requestedAdditionalAndroids",AdditionalResidents},
                {"durationHours",Hours}, {"startTicks",Start.Ticks.ToString(CultureInfo.InvariantCulture)},
                {"endTicks",End.Ticks.ToString(CultureInfo.InvariantCulture)}, {"startFrame",StartFrame}, {"endFrame",EndFrame},
                {"baseline",JsonData.Parse(Baseline)}, {"afterAdmission",JsonData.Parse(AfterAdmission)},
                {"end",JsonData.Parse(terminalSample)}
            };
            string path = Path.Combine(NativeCityRoadBuilder.Root, "native-employment-" + CaseId + ".json");
            string pending = path + ".tmp";
            File.WriteAllText(pending, JsonData.Stringify(result), new UTF8Encoding(false));
            File.Move(pending, path);
        }
    }
}
