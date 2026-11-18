using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using ICities;
using ColossalFramework;
using UnityEngine;

namespace NagoyaAndroidDemo
{
    public sealed class Mod : IUserMod
    {
        public string Name { get { return "Nagoya Android Economy Demo"; } }
        public string Description { get { return "Rule-based robot delivery and charging, with native pedestrian coexistence."; } }
    }

    public sealed class Loading : LoadingExtensionBase
    {
        private GameObject host;
        public override void OnLevelLoaded(LoadMode mode)
        {
            if (mode != LoadMode.NewGame && mode != LoadMode.LoadGame) return;
            host = new GameObject("NagoyaAndroidDemoController");
            host.AddComponent<Demo>().Mode = mode.ToString();
            host.AddComponent<NagoyaRoadDelivery.DistrictController>().Mode = mode.ToString();
            host.AddComponent<NagoyaRoadDelivery.HumanDistrictController>().Mode = mode.ToString();
            host.AddComponent<NagoyaRoadDelivery.CitywideScene>();
        }
        public override void OnLevelUnloading()
        {
            if (host != null) UnityEngine.Object.Destroy(host);
            host = null;
        }
    }

    public sealed class Demo : MonoBehaviour
    {
        public string Mode;
        private const string Folder = @"H:\CS1-PLATEAU\android";
        private readonly List<Robot> robots = new List<Robot>();
        private readonly List<Material> materials = new List<Material>();
        private GameObject scene;
        private DateTime lastCommand = DateTime.MinValue;
        private float nextPoll, nextState;
        private float originX, originZ, angle;
        private bool enabledDemo;
        private string runId;
        private string error = "";
        private Material white, dark, cyan, amber, green;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private sealed class Robot
        {
            public int id, deliveries, charges;
            public GameObject body, parcel, leftLeg, rightLeg;
            public string state = "waiting";
            public Vector3 position, target;
            public float battery = 100, wait, elapsed;
        }

        public void Start()
        {
            Directory.CreateDirectory(Folder);
            Poll();
        }

        public void Update()
        {
            if (Time.realtimeSinceStartup >= nextPoll)
            {
                nextPoll = Time.realtimeSinceStartup + 1;
                try { Poll(); } catch (Exception ex) { error = ex.Message; Debug.LogException(ex); }
            }
            if (!enabledDemo || scene == null) return;
            SimulationManager simulation = Singleton<SimulationManager>.instance;
            float dt = simulation.SimulationPaused ? 0 : Mathf.Min(Time.deltaTime, 0.1f) * Mathf.Clamp(simulation.FinalSimulationSpeed, 0, 3);
            for (int i = 0; i < robots.Count; i++) Step(robots[i], dt);
            if (Time.realtimeSinceStartup >= nextState)
            {
                nextState = Time.realtimeSinceStartup + 1;
                try { WriteState(); } catch (Exception ex) { error = ex.Message; }
            }
        }

        private void Poll()
        {
            string path = Path.Combine(Folder, "demo-command.ini");
            if (!File.Exists(path)) return;
            DateTime changed = File.GetLastWriteTimeUtc(path);
            if (changed == lastCommand) return;
            Dictionary<string, string> config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int sep = line.IndexOf('=');
                if (sep > 0 && !line.StartsWith("#")) config[line.Substring(0, sep).Trim()] = line.Substring(sep + 1).Trim();
            }
            float x = Number(config, "x", 0), z = Number(config, "z", 0), a = Number(config, "angle", 0);
            bool on = config.ContainsKey("enabled") && config["enabled"] == "true";
            if (Mathf.Abs(x) > 8500 || Mathf.Abs(z) > 8500) throw new ArgumentException("Origin is outside the map");
            lastCommand = changed;
            enabledDemo = on;
            if (scene != null) Destroy(scene);
            scene = null;
            robots.Clear();
            foreach (Material material in materials) if (material != null) Destroy(material);
            materials.Clear();
            if (on)
            {
                originX = x; originZ = z; angle = a;
                Build();
            }
            WriteState();
        }

        private static float Number(Dictionary<string, string> config, string key, float fallback)
        {
            float value;
            return config.ContainsKey(key) && float.TryParse(config[key], NumberStyles.Float, Invariant, out value) ? value : fallback;
        }

        private Material MakeMaterial(Color color)
        {
            Shader shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("No supported robot shader found");
            Material material = new Material(shader);
            material.color = color;
            materials.Add(material);
            return material;
        }

        private GameObject Shape(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            GameObject item = GameObject.CreatePrimitive(type);
            item.name = name;
            item.transform.parent = parent;
            item.transform.localPosition = position;
            item.transform.localScale = scale;
            item.GetComponent<Renderer>().sharedMaterial = material;
            Collider collider = item.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            return item;
        }

        private void Build()
        {
            runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", Invariant);
            scene = new GameObject("Android delivery and charging test - " + runId);
            scene.transform.position = new Vector3(originX, 0, originZ);
            scene.transform.rotation = Quaternion.Euler(0, angle, 0);
            white = MakeMaterial(new Color(0.78f, 0.84f, 0.89f));
            dark = MakeMaterial(new Color(0.04f, 0.08f, 0.13f));
            cyan = MakeMaterial(new Color(0.08f, 0.85f, 1));
            amber = MakeMaterial(new Color(1, 0.56f, 0.08f));
            green = MakeMaterial(new Color(0.1f, 0.95f, 0.4f));
            Station("CHARGE", new Vector3(0, 0, 0), green);
            Station("PICKUP", new Vector3(8, 0, 0), cyan);
            Station("DELIVERY", new Vector3(8, 0, 12), amber);
            for (int i = 0; i < 3; i++)
            {
                Robot robot = new Robot();
                robot.id = i + 1; robot.wait = i * 4 + 1;
                robot.position = new Vector3(-1 + i, 0, 0);
                robot.body = new GameObject("Android " + robot.id);
                robot.body.transform.parent = scene.transform;
                Shape("torso", PrimitiveType.Cube, robot.body.transform, new Vector3(0, 1.0f, 0), new Vector3(0.48f, 0.58f, 0.3f), white);
                Shape("head", PrimitiveType.Cube, robot.body.transform, new Vector3(0, 1.51f, 0), new Vector3(0.36f, 0.33f, 0.32f), white);
                Shape("visor", PrimitiveType.Cube, robot.body.transform, new Vector3(0, 1.54f, 0.17f), new Vector3(0.29f, 0.09f, 0.025f), cyan);
                Shape("left arm", PrimitiveType.Cube, robot.body.transform, new Vector3(-0.32f, 0.98f, 0), new Vector3(0.12f, 0.5f, 0.13f), dark);
                Shape("right arm", PrimitiveType.Cube, robot.body.transform, new Vector3(0.32f, 0.98f, 0), new Vector3(0.12f, 0.5f, 0.13f), dark);
                robot.leftLeg = Shape("left leg", PrimitiveType.Cube, robot.body.transform, new Vector3(-0.13f, 0.36f, 0), new Vector3(0.16f, 0.65f, 0.19f), dark);
                robot.rightLeg = Shape("right leg", PrimitiveType.Cube, robot.body.transform, new Vector3(0.13f, 0.36f, 0), new Vector3(0.16f, 0.65f, 0.19f), dark);
                robot.parcel = Shape("parcel", PrimitiveType.Cube, robot.body.transform, new Vector3(0, 0.98f, 0.33f), new Vector3(0.39f, 0.28f, 0.29f), amber);
                robot.parcel.SetActive(false);
                robots.Add(robot);
                Place(robot);
                Event(robot, "created");
            }
            error = "";
        }

        private float Ground(Vector3 local)
        {
            Vector3 world = scene.transform.TransformPoint(local);
            return Singleton<TerrainManager>.instance.SampleRawHeightSmoothWithWater(world, false, 0);
        }

        private void Station(string name, Vector3 point, Material material)
        {
            point.y = Ground(point) + 0.06f;
            Shape(name, PrimitiveType.Cube, scene.transform, point, new Vector3(3.8f, 0.1f, 2.4f), material);
            if (name == "CHARGE")
            {
                Shape("charging post", PrimitiveType.Cube, scene.transform, point + new Vector3(-1.6f, 0.75f, 0), new Vector3(0.35f, 1.5f, 0.35f), dark);
                Shape("charging light", PrimitiveType.Cube, scene.transform, point + new Vector3(-1.6f, 1.5f, 0), new Vector3(0.4f, 0.2f, 0.4f), green);
            }
        }

        private void Step(Robot robot, float dt)
        {
            if (dt <= 0) return;
            robot.elapsed += dt;
            bool moving = robot.state == "to_pickup" || robot.state == "to_delivery" || robot.state == "to_charge";
            if (moving)
            {
                robot.battery = Mathf.Max(0, robot.battery - dt * 1.6f);
                Vector3 delta = robot.target - robot.position;
                if (delta.sqrMagnitude <= 0.02f)
                {
                    if (robot.state == "to_pickup") SetState(robot, "loading", 1.5f);
                    else if (robot.state == "to_delivery")
                    {
                        robot.deliveries++;
                        robot.parcel.SetActive(false);
                        SetState(robot, "unloading", 2);
                    }
                    else SetState(robot, "charging", 0);
                }
                else
                {
                    robot.position = Vector3.MoveTowards(robot.position, robot.target, dt * 1.6f);
                    robot.body.transform.localRotation = Quaternion.LookRotation(delta);
                    float stride = Mathf.Sin(robot.elapsed * 8) * 24;
                    robot.leftLeg.transform.localRotation = Quaternion.Euler(stride, 0, 0);
                    robot.rightLeg.transform.localRotation = Quaternion.Euler(-stride, 0, 0);
                }
            }
            else if (robot.state == "charging")
            {
                robot.battery = Mathf.Min(100, robot.battery + dt * 7);
                if (robot.battery >= 100)
                {
                    robot.charges++;
                    SetState(robot, "waiting", 2);
                }
            }
            else
            {
                robot.wait -= dt;
                if (robot.wait <= 0)
                {
                    if (robot.state == "waiting") Move(robot, "to_pickup", new Vector3(8, 0, 0));
                    else if (robot.state == "loading")
                    {
                        robot.parcel.SetActive(true);
                        Move(robot, "to_delivery", new Vector3(8, 0, 12));
                    }
                    else if (robot.state == "unloading") Move(robot, "to_charge", new Vector3(0, 0, 0));
                }
            }
            Place(robot);
        }

        private void Place(Robot robot)
        {
            Vector3 position = robot.position;
            position.x += (robot.id - 2) * 0.55f;
            position.y = Ground(position) + 0.12f;
            robot.body.transform.localPosition = position;
        }

        private void Move(Robot robot, string state, Vector3 target)
        {
            robot.target = target;
            SetState(robot, state, 0);
        }
        private void SetState(Robot robot, string state, float wait)
        {
            robot.state = state; robot.wait = wait;
            Event(robot, state);
        }
        private static string N(float value) { return value.ToString("0.###", Invariant); }
        private void Event(Robot robot, string action)
        {
            File.AppendAllText(Path.Combine(Folder, "events.jsonl"), "{\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"runId\":\"" + runId + "\",\"robot\":" + robot.id + ",\"event\":\"" + action + "\",\"battery\":" + N(robot.battery) + ",\"deliveries\":" + robot.deliveries + ",\"charges\":" + robot.charges + "}\n");
        }

        private void WriteState()
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"implementation\":\"in-game Unity waypoint experiment; not native citizen pathfinding\",\"enabled\":").Append(enabledDemo ? "true" : "false");
            json.Append(",\"mode\":\"").Append(Mode).Append("\",\"assemblyMvid\":\"").Append(GetType().Assembly.ManifestModule.ModuleVersionId).Append("\"");
            json.Append(",\"runId\":\"").Append(runId).Append("\",\"utc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",\"robots\":[");
            for (int i = 0; i < robots.Count; i++)
            {
                Robot r = robots[i];
                if (i > 0) json.Append(',');
                json.Append("{\"id\":").Append(r.id).Append(",\"state\":\"").Append(r.state).Append("\",\"battery\":").Append(N(r.battery));
                json.Append(",\"x\":").Append(N(r.position.x)).Append(",\"z\":").Append(N(r.position.z));
                Vector3 world = r.body.transform.position;
                json.Append(",\"worldX\":").Append(N(world.x)).Append(",\"worldY\":").Append(N(world.y)).Append(",\"worldZ\":").Append(N(world.z));
                json.Append(",\"objectActive\":").Append(r.body.activeInHierarchy ? "true" : "false");
                json.Append(",\"deliveries\":").Append(r.deliveries).Append(",\"charges\":").Append(r.charges).Append('}');
            }
            json.Append("]}");
            string temp = Path.Combine(Folder, "state.tmp");
            File.WriteAllText(temp, json.ToString());
            string target = Path.Combine(Folder, "state.json");
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }

        public void OnGUI()
        {
            if (!enabledDemo) return;
            GUI.Box(new Rect(12, 90, 390, 152), "ANDROID DELIVERY / CHARGING TEST");
            GUI.Label(new Rect(24, 116, 370, 22), "Rule-based waypoints (not citizen pathfinding)");
            for (int i = 0; i < robots.Count; i++)
            {
                Robot r = robots[i];
                GUI.Label(new Rect(24, 140 + i * 24, 370, 24), "A" + r.id + "  " + r.state + "  " + N(r.battery) + "%  D:" + r.deliveries + " C:" + r.charges);
            }
            if (error.Length > 0) GUI.Label(new Rect(24, 213, 370, 24), error);
        }

        public void OnDestroy()
        {
            enabledDemo = false;
            try { WriteState(); } catch (Exception ex) { Debug.LogException(ex); }
            if (scene != null) Destroy(scene);
            foreach (Material material in materials) if (material != null) Destroy(material);
        }
    }
}
