using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    public sealed class NativeCityModelAlignment : MonoBehaviour
    {
        private readonly HashSet<int> aligned = new HashSet<int>();
        private Vector2 center;
        private float scale, deadline, nextPass;
        private int expected;

        public void Start()
        {
            string path = Path.Combine(NativeCityRoadBuilder.Root, "model-alignment.json");
            if (!File.Exists(path))
            {
                enabled = false;
                return;
            }
            try
            {
                if (new FileInfo(path).Length > 4096)
                    throw new InvalidDataException("Alignment file is too large");
                Dictionary<string, object> config = (Dictionary<string, object>)JsonData.Parse(File.ReadAllText(path));
                if ((string)config["city"] != SimulationManager.instance.m_metaData.m_CityName)
                    throw new InvalidDataException("Alignment belongs to another city");
                scale = (float)(double)config["scale"];
                center = new Vector2((float)(double)config["centerX"], (float)(double)config["centerZ"]);
                expected = checked((int)(double)config["expectedObjects"]);
                if (scale <= 0 || scale > 1 || expected < 1 || expected > 6000 ||
                    Single.IsNaN(scale) || Single.IsNaN(center.x) || Single.IsNaN(center.y) ||
                    Mathf.Abs(center.x) > 25000 || Mathf.Abs(center.y) > 25000)
                    throw new InvalidDataException("Alignment transform is outside the source map bounds");
                deadline = Time.realtimeSinceStartup + 150f;
            }
            catch (Exception error)
            {
                Finish("rejected", error.GetBaseException().Message);
            }
        }

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextPass) return;
            nextPass = now + 4f;
            foreach (MeshRenderer renderer in FindObjectsOfType<MeshRenderer>())
            {
                if (renderer == null || !renderer.gameObject.name.StartsWith("PLATEAU_Station_bldg_", StringComparison.Ordinal) ||
                    !aligned.Add(renderer.gameObject.GetInstanceID()))
                    continue;
                Transform model = renderer.transform;
                Vector3 original = model.position;
                model.position = new Vector3((original.x - center.x) * scale, 140 + (original.y - 140) * scale,
                    (original.z - center.y) * scale);
                model.localScale *= scale;
            }
            if (aligned.Count >= expected)
                Finish(aligned.Count == expected ? "aligned" : "unexpected_model_count", null);
            else if (now >= deadline)
                Finish("timed_out", "Expected the complete active PLATEAU model set");
        }

        private void Finish(string phase, string error)
        {
            enabled = false;
            string path = Path.Combine(NativeCityRoadBuilder.Root, "model-alignment-runtime.json");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonData.Stringify(new Dictionary<string, object> {
                {"phase",phase}, {"error",error}, {"scale",scale}, {"expectedObjects",expected},
                {"observedObjects",aligned.Count}, {"utc",DateTime.UtcNow.ToString("o")}
            }));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
