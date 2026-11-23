// ExperimentRunner: the Phase 7 experiment. It runs every controller (RuleBased, Bug2, the 30 LLM controllers = 32) on every map
// (M1_Open, M2_Cluttered, M3_Corridor, M4_Trap) at every noise level (0, 0.02, 0.05, 0.10) for the trial seeds 1..30 = 15,360 runs,
// and writes ONE row per run to results/results.csv (written and flushed at once, so a crash loses nothing).
// Several ARENAS (default 8, built here at run time, 10 m apart) are simulated side by side in one physics scene to save time; the
// arenas never touch each other. A run = load the map, place the rover with TrialSpawner(trial seed), set the noise level, seed the
// SensorRig from (map, noise, trial seed) ONLY (so every controller sees the same noise, whatever the run order), plug in a fresh
// controller and simulate until goal / collision / 60 s. The two LLM files that did not compile (LLM_NoCompile/) get their rows
// (success 0, outcome "no_compile") without any simulation. RESUME: runs already in the CSV are skipped when the program starts.
// Command line (player) or results/runner_args.txt (Editor):  --quick | --full   --timescale N   --arenas N   --maps 1,4
//   --seeds A-B   --noises 0.05   --out file.csv   --results folder   --offset 70 (arena 0 position; keep 70 for the real runs).   Paths (x, z every 0.1 s) are saved for M4_Trap, noise 0.05, seeds 1-3.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public class ExperimentRunner : MonoBehaviour
{
    public SimConfig config;                 // the study's SimConfig asset (a runtime COPY with the viewing aids off is used)
    public GameObject roverPrefab;           // Assets/Prefabs/Rover.prefab
    public MapDefinition[] maps;             // M1_Open, M2_Cluttered, M3_Corridor, M4_Trap, in this order

    public const string Header = "controller,family,model_label,attempt,condition,map,noise,seed,success,collision,timeout,exceptions,path_m,straight_m,efficiency,time_s,min_clear_cm,outcome";
    public const string NA = "NA";           // value of a number that does not exist (e.g. efficiency of a run that failed)
    const float ArenaSpacing = 10f;
    const float DefaultArenaOffset = 70f;    // all arenas lie in the same float "binade" [64,128): the same rounding in every arena
    const int ArenaColumns = 4;
    const int PathEverySteps = 5;            // 5 x 0.02 s = 0.1 s
    const int PathMap = 3, PathNoiseIdx = 2, PathLastSeed = 3;   // M4_Trap, noise 0.05, seeds 1-3

    static readonly Dictionary<string, string> ModelLabels = new Dictionary<string, string>
    {
        { "ChatGPT", "ChatGPT (GPT-6)" }, { "Gemini", "Gemini (Auto mode)" }, { "Claude", "Claude Opus 5.5" }
    };
    static readonly Regex LlmName = new Regex(@"^LLM_([A-Za-z0-9]+)_([0-9]+)_(OS|FB)$");

    // ---------- settings ----------
    class Options
    {
        public bool quick;
        public float timeScale = 10f;
        public int arenas = 8;
        public float offset = DefaultArenaOffset;   // world position of arena 0 (x and z); --offset 0 puts arena 0 at the origin (test only)
        public List<int> mapFilter;          // null = all four
        public List<float> noiseFilter;      // null = all four levels (test only)
        public int seedFirst = -1, seedLast = -1;
        public string outCsv;                // null = results.csv (full) or quick.csv (quick)
        public string resultsDir;
    }

    struct Entry { public string name; public bool compiles; public string family, label, attempt, condition; }
    struct Spec { public int entry, map, noiseIdx, seed; }

    class Arena
    {
        public int index;
        public Vector3 offset;
        public Transform root, startZone, goalZone;
        public MapLoader loader;
        public TrialSpawner spawner;
        public RoverDrive drive;
        public SensorRig rig;
        public ControllerHost host;
        public RoverStatus status;
        public Rigidbody rb;
        public int loadedMap = -1;
        // current run
        public bool busy;
        public Spec spec;
        public int steps;
        public float straight;
        public List<Vector2> path;
    }

    Options opt;
    SimConfig runCfg;
    string resultsDir, csvPath, progressPath, pathsDir;
    Entry[] entries;
    float[] noises;
    readonly List<Arena> arenaList = new List<Arena>();
    readonly Queue<Spec> queue = new Queue<Spec>();
    StreamWriter csv;
    FileStream csvStream;
    int totalRuns, doneRuns, doneAtStart, simulatedThisSession, hangRows;
    HashSet<string> hangKeys = new HashSet<string>();      // runs listed in results/hang_runs.txt (written by tools/run_experiment.py)
    readonly HashSet<string> inflight = new HashSet<string>();   // runs started and not finished: results/inflight.txt (the watchdog reads it after a hang)
    float sessionStart, lastProgress;

    void Start() { StartCoroutine(Main()); }

    // =====================================================================================================================
    IEnumerator Main()
    {
        bool ok = false;
        string failure = null;
        IEnumerator inner = Run();
        while (true)
        {
            bool moved;
            try { moved = inner.MoveNext(); }
            catch (Exception e) { failure = e.ToString(); break; }
            if (!moved) { ok = true; break; }
            yield return inner.Current;
        }
        if (!ok)
        {
            Debug.LogError("ExperimentRunner failed: " + failure);
            try { File.AppendAllText(Path.Combine(resultsDir ?? ".", "runner_errors.txt"), DateTime.Now.ToString("s") + "  " + failure + "\n"); } catch { }
        }
        if (csv != null) { csv.Flush(); csv.Dispose(); }
        Quit(ok ? 0 : 2);
    }

    static void Quit(int code)
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(code);
#endif
    }

    IEnumerator Run()
    {
        opt = ParseOptions();
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Time.maximumDeltaTime = Mathf.Max(0.333f, 0.05f * opt.timeScale);

        resultsDir = opt.resultsDir;
        Directory.CreateDirectory(resultsDir);
        pathsDir = Path.Combine(resultsDir, "paths");
        Directory.CreateDirectory(pathsDir);
        csvPath = Path.Combine(resultsDir, opt.outCsv ?? (opt.quick ? "quick.csv" : "results.csv"));
        progressPath = Path.Combine(resultsDir, "progress.txt");

        runCfg = Instantiate(config);
        runCfg.SetExperimentMode();
        noises = runCfg.noiseLevels;
        entries = LoadEntries();

        int seedFirst = opt.seedFirst >= 0 ? opt.seedFirst : runCfg.firstTrialSeed;
        int seedLast = opt.seedLast >= 0 ? opt.seedLast : (opt.quick ? runCfg.firstTrialSeed + 1 : runCfg.firstTrialSeed + runCfg.trialCount - 1);
        var mapIdx = opt.mapFilter ?? new List<int> { 0, 1, 2, 3 };

        // ---------- the complete run list, and what is already done (RESUME) ----------
        var done = ReadDone(csvPath);
        hangKeys = ReadHangKeys();
        var all = new List<Spec>();
        foreach (int m in mapIdx)
            for (int n = 0; n < noises.Length; n++)
                if (opt.noiseFilter == null || opt.noiseFilter.Exists(x => Mathf.Abs(x - noises[n]) < 1e-4f))
                for (int s = seedFirst; s <= seedLast; s++)
                    for (int e = 0; e < entries.Length; e++)
                        all.Add(new Spec { entry = e, map = m, noiseIdx = n, seed = s });
        totalRuns = all.Count;
        OpenCsv();

        var todo = new List<Spec>();
        foreach (var sp in all)
        {
            if (done.Contains(Key(sp))) { doneRuns++; continue; }
            if (!entries[sp.entry].compiles) { WriteRow(NoCompileRow(sp)); doneRuns++; continue; }   // no simulation
            if (hangKeys.Contains(Key(sp))) { WriteRow(HangRow(sp)); doneRuns++; hangRows++; continue; }   // the watchdog found that this run hangs the program
            todo.Add(sp);
        }
        doneAtStart = doneRuns;
        foreach (var sp in todo) queue.Enqueue(sp);
        Debug.Log("ExperimentRunner: " + totalRuns + " runs in total, " + doneAtStart + " already done (incl. no_compile rows), " + todo.Count + " to simulate; arenas " + opt.arenas + ", timescale " + opt.timeScale + ", file " + csvPath);
        sessionStart = Time.realtimeSinceStartup;
        WriteProgress(true);

        if (todo.Count > 0)
        {
            int n = Mathf.Max(1, Mathf.Min(opt.arenas, todo.Count));
            for (int i = 0; i < n; i++) arenaList.Add(BuildArena(i));
            yield return null; yield return null;       // let every Start() of the new objects run first

            Time.timeScale = opt.timeScale;
            foreach (var a in arenaList) TryBegin(a);
            while (true)
            {
                yield return new WaitForFixedUpdate();   // after this physics step (collision callbacks have run)
                bool anyBusy = false;
                foreach (var a in arenaList)
                {
                    if (!a.busy) continue;
                    Step(a);
                    if (a.busy) anyBusy = true;
                    else TryBegin(a);
                    if (a.busy) anyBusy = true;
                }
                if (!anyBusy && queue.Count == 0) break;
                if (Time.realtimeSinceStartup - lastProgress > 5f) WriteProgress(false);
            }
            Time.timeScale = 1f;
        }
        WriteProgress(true, true);
        Debug.Log("ExperimentRunner: finished, " + doneRuns + " / " + totalRuns + " rows in " + csvPath);
    }

    // =====================================================================================================================
    // ---------- options ----------
    Options ParseOptions()
    {
        var o = new Options();
        string projectDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string exeDir = projectDir;
        o.resultsDir = Path.Combine(projectDir, "results");           // Editor
        var args = new List<string>(Environment.GetCommandLineArgs());
        if (!Application.isEditor)
        {
            o.resultsDir = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "results"));   // Build/Experiment/ -> project root/results
        }
        else
        {
            string f = Path.Combine(o.resultsDir, "runner_args.txt");   // the Editor has no command line of its own
            if (File.Exists(f)) args.AddRange(File.ReadAllText(f).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string next = i + 1 < args.Count ? args[i + 1] : null;
            switch (a)
            {
                case "--quick": o.quick = true; break;
                case "--full": o.quick = false; break;
                case "--timescale": o.timeScale = float.Parse(next, CultureInfo.InvariantCulture); i++; break;
                case "--offset": o.offset = float.Parse(next, CultureInfo.InvariantCulture); i++; break;
                case "--arenas": o.arenas = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                case "--out": o.outCsv = next; i++; break;
                case "--results": o.resultsDir = Path.GetFullPath(next); i++; break;
                case "--maps":
                    o.mapFilter = new List<int>();
                    foreach (var t in next.Split(','))
                    {
                        int k = -1;
                        if (int.TryParse(t, out int num)) k = num - 1;                 // "1" = M1_Open
                        else for (int j = 0; j < maps.Length; j++) if (maps[j].name.StartsWith(t, StringComparison.OrdinalIgnoreCase)) k = j;
                        if (k < 0 || k >= maps.Length) throw new ArgumentException("--maps: unknown map '" + t + "'");
                        o.mapFilter.Add(k);
                    }
                    i++; break;
                case "--noises":
                    o.noiseFilter = new List<float>();
                    foreach (var t in next.Split(',')) o.noiseFilter.Add(float.Parse(t, CultureInfo.InvariantCulture));
                    i++; break;
                case "--seeds":
                    var parts = next.Split('-');
                    o.seedFirst = int.Parse(parts[0]); o.seedLast = int.Parse(parts[parts.Length - 1]); i++; break;
            }
        }
        if (o.arenas < 1) o.arenas = 1;
        if (o.timeScale < 0.1f) o.timeScale = 0.1f;
        return o;
    }

    // ---------- the controller list ----------
    Entry[] LoadEntries()
    {
        var list = new List<Entry>();
        (string name, bool compiles)[] raw;
        if (Application.isEditor) raw = ControllerRegistry.ExperimentEntries();
        else
        {
            // a player cannot see the LLM_NoCompile folder: the build step wrote the list next to the program
            string manifest = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "controllers_manifest.txt");
            if (!File.Exists(manifest)) throw new FileNotFoundException("controllers_manifest.txt missing next to the player: " + manifest);
            var l = new List<(string, bool)>();
            foreach (var line in File.ReadAllLines(manifest))
            {
                var p = line.Trim().Split(',');
                if (p.Length == 2) l.Add((p[0], p[1] == "1"));
            }
            raw = l.ToArray();
            // the compiled controllers in the player must be exactly the ones the manifest says compile
            var inPlayer = new HashSet<string>(ControllerRegistry.ExperimentNames());
            foreach (var r in raw) if (r.Item2 && !inPlayer.Contains(r.Item1)) throw new Exception("manifest controller missing in the player: " + r.Item1);
            int nCompile = 0; foreach (var r in raw) if (r.Item2) nCompile++;
            if (nCompile != inPlayer.Count) throw new Exception("player has " + inPlayer.Count + " controllers, manifest lists " + nCompile);
        }
        foreach (var (name, compiles) in raw)
        {
            var e = new Entry { name = name, compiles = compiles };
            var m = LlmName.Match(name);
            if (m.Success)
            {
                e.family = "LLM";
                e.label = ModelLabels.TryGetValue(m.Groups[1].Value, out var lab) ? lab : m.Groups[1].Value;
                e.attempt = m.Groups[2].Value;
                e.condition = m.Groups[3].Value;
            }
            else
            {
                e.family = name == "Bug2Controller" ? "Bug2" : "Rule";
                e.label = e.family == "Bug2" ? "Bug2" : "Rule-based";
                e.attempt = NA; e.condition = NA;
            }
            list.Add(e);
        }
        return list.ToArray();
    }

    // ---------- csv ----------
    string Key(Spec s) => entries[s.entry].name + "|" + maps[s.map].name + "|" + noises[s.noiseIdx].ToString("0.00", CultureInfo.InvariantCulture) + "|" + s.seed;

    HashSet<string> ReadDone(string path)
    {
        var set = new HashSet<string>();
        if (!File.Exists(path)) return set;
        string text = File.ReadAllText(path);
        int lastNl = text.LastIndexOf('\n');
        if (lastNl < text.Length - 1)                       // the program was killed in the middle of a row: drop that row
        {
            text = lastNl >= 0 ? text.Substring(0, lastNl + 1) : "";
            File.WriteAllText(path, text);
            Debug.LogWarning("ExperimentRunner: removed an incomplete last row from " + path);
        }
        var lines = text.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            var p = lines[i].TrimEnd('\r').Split(',');
            if (p.Length < 8) continue;
            set.Add(p[0] + "|" + p[5] + "|" + p[6] + "|" + p[7]);   // controller|map|noise|seed
        }
        return set;
    }

    void OpenCsv()
    {
        bool fresh = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
        csvStream = new FileStream(csvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        csv = new StreamWriter(csvStream, new UTF8Encoding(false)) { NewLine = "\n" };
        if (fresh) { csv.WriteLine(Header); FlushRow(); }
    }

    void FlushRow() { csv.Flush(); csvStream.Flush(true); }

    void WriteRow(string row) { csv.WriteLine(row); FlushRow(); }

    static string F(float v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    string RowStart(Spec s)
    {
        var e = entries[s.entry];
        return e.name + "," + e.family + "," + e.label + "," + e.attempt + "," + e.condition + "," + maps[s.map].name + ","
               + F(noises[s.noiseIdx], "0.00") + "," + s.seed;
    }

    // runs the watchdog declared "hang" (the program froze twice in a row on exactly this run): one key per line, controller|map|noise|seed
    HashSet<string> ReadHangKeys()
    {
        var set = new HashSet<string>();
        string f = Path.Combine(resultsDir, "hang_runs.txt");
        if (File.Exists(f)) foreach (var line in File.ReadAllLines(f)) { string k = line.Trim(); if (k.Length > 0) set.Add(k); }
        return set;
    }

    // row of a run that hangs the program: success 0, no collision, no timeout, outcome "hang"; the numbers that need a simulation are NA (like no_compile)
    string HangRow(Spec s)
    {
        var map = maps[s.map];
        var pose = TrialSpawner.ComputePose(s.seed, map.startZoneCentre, map.goalZoneCentre, runCfg);
        float straight = DistanceToZone(pose.position, map.goalZoneCentre);
        return RowStart(s) + ",0,0,0,0," + NA + "," + F(straight, "0.000") + "," + NA + "," + NA + "," + NA + ",hang";
    }

    void WriteInflight()
    {
        try { File.WriteAllText(Path.Combine(resultsDir, "inflight.txt"), string.Join("\n", inflight) + "\n"); } catch { }
    }

    // straight_m of a no_compile row: distance from the start pose to the goal zone (needs no simulation, only the start pose)
    string NoCompileRow(Spec s)
    {
        var map = maps[s.map];
        var pose = TrialSpawner.ComputePose(s.seed, map.startZoneCentre, map.goalZoneCentre, runCfg);
        float straight = DistanceToZone(pose.position, map.goalZoneCentre);
        return RowStart(s) + ",0,0,0,0," + NA + "," + F(straight, "0.000") + "," + NA + "," + NA + "," + NA + "," + ControllerRegistry.NoCompileOutcome;
    }

    float DistanceToZone(Vector2 p, Vector2 zoneCentre)
    {
        float half = runCfg.zoneSize * 0.5f;
        float dx = Mathf.Max(Mathf.Abs(p.x - zoneCentre.x) - half, 0f), dz = Mathf.Max(Mathf.Abs(p.y - zoneCentre.y) - half, 0f);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ---------- progress ----------
    void WriteProgress(bool force, bool finished = false)
    {
        lastProgress = Time.realtimeSinceStartup;
        float elapsed = Mathf.Max(0.001f, Time.realtimeSinceStartup - sessionStart);
        float rate = simulatedThisSession / elapsed;                 // simulated runs per second
        int remaining = totalRuns - doneRuns;
        string eta = rate > 0.0001f ? TimeSpan.FromSeconds(remaining / rate).ToString(@"hh\:mm\:ss") : "--:--:--";
        string line = "run " + doneRuns + " / " + totalRuns + ", ETA " + eta + "  (" + F(rate * 60f, "0.0") + " runs/min, arenas " + arenaList.Count
                      + ", timescale " + opt.timeScale + ")" + (finished ? "  DONE" : "") + "  updated " + DateTime.Now.ToString("s");
        try { File.WriteAllText(progressPath, line + "\n"); } catch { }
        Debug.Log(line);
    }

    // =====================================================================================================================
    // ---------- arenas ----------
    Arena BuildArena(int i)
    {
        var a = new Arena { index = i };
        a.offset = new Vector3(opt.offset + ArenaSpacing * (i % ArenaColumns), 0f, opt.offset + ArenaSpacing * (i / ArenaColumns));
        a.root = new GameObject("Arena_" + i).transform;
        a.root.position = a.offset;

        float size = runCfg.arenaSize, t = runCfg.wallThickness, wh = runCfg.wallHeight, ft = runCfg.floorThickness;
        Block(a.root, "Floor", null, new Vector3(0f, -ft * 0.5f, 0f), new Vector3(size, ft, size), false);
        float w = size * 0.5f + t * 0.5f;
        Block(a.root, "Wall_East", "Wall", new Vector3(w, wh * 0.5f, 0f), new Vector3(t, wh, size), false);
        Block(a.root, "Wall_West", "Wall", new Vector3(-w, wh * 0.5f, 0f), new Vector3(t, wh, size), false);
        Block(a.root, "Wall_North", "Wall", new Vector3(0f, wh * 0.5f, w), new Vector3(size + 2f * t, wh, t), false);
        Block(a.root, "Wall_South", "Wall", new Vector3(0f, wh * 0.5f, -w), new Vector3(size + 2f * t, wh, t), false);
        a.startZone = Block(a.root, "StartZone", null, new Vector3(-2.5f, 0.01f, -2.5f), new Vector3(runCfg.zoneSize, 0.02f, runCfg.zoneSize), true);
        a.goalZone = Block(a.root, "GoalZone", "Goal", new Vector3(2.5f, 0.01f, 2.5f), new Vector3(runCfg.zoneSize, 0.02f, runCfg.zoneSize), true);

        // the rover: made inactive, given its config and its arena, then switched on (so Awake already sees the runtime config)
        var holder = new GameObject("holder");
        holder.SetActive(false);
        var rover = Instantiate(roverPrefab, holder.transform);
        rover.name = "Rover_" + i;
        a.drive = rover.GetComponent<RoverDrive>();
        a.drive.config = runCfg;
        a.status = rover.GetComponent<RoverStatus>();
        a.status.arenaRoot = a.root;
        a.host = rover.GetComponent<ControllerHost>();
        a.host.arenaRoot = a.root;
        a.rig = rover.GetComponent<SensorRig>();
        a.rb = rover.GetComponent<Rigidbody>();
        rover.transform.SetPositionAndRotation(a.offset + new Vector3(-2.5f, runCfg.roverHeight * 0.5f + 0.005f, -2.5f), Quaternion.identity);
        rover.transform.SetParent(null);                   // now it becomes active
        Destroy(holder);
        a.host.enabled = false;

        a.loader = a.root.gameObject.AddComponent<MapLoader>();
        a.loader.config = runCfg;
        a.loader.startZone = a.startZone;
        a.loader.goalZone = a.goalZone;
        a.loader.loadOnStart = false;
        a.loader.onlyStatus = a.status;

        a.spawner = a.root.gameObject.AddComponent<TrialSpawner>();
        a.spawner.config = runCfg;
        a.spawner.rover = a.drive;
        a.spawner.startZone = a.startZone;
        a.spawner.goalZone = a.goalZone;
        return a;
    }

    Transform Block(Transform parent, string name, string tag, Vector3 localPos, Vector3 scale, bool trigger)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (tag != null) go.tag = tag;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.GetComponent<BoxCollider>().isTrigger = trigger;
        go.GetComponent<Renderer>().enabled = false;          // nothing to look at: saves time
        return go.transform;
    }

    // ---------- one run ----------
    void TryBegin(Arena a)
    {
        if (queue.Count == 0) { a.busy = false; return; }
        var spec = queue.Dequeue();
        a.spec = spec;
        inflight.Add(Key(spec)); WriteInflight();              // before anything else can freeze: the watchdog needs to know which run it was
        var map = maps[spec.map];

        a.host.enabled = false;                                // hold the old controller while everything is swapped
        a.drive.SetCommand(0f, 0f);
        if (a.loadedMap != spec.map) { a.loader.Load(map); a.loadedMap = spec.map; }

        var pose = a.spawner.Spawn(spec.seed);                 // same trial seed = same start pose, whatever the controller/noise/map
        a.rig.NoiseLevel = noises[spec.noiseIdx];
        a.rig.SetSeed(SensorSeed(spec.map, spec.noiseIdx, spec.seed));
        a.host.SetController(entries[spec.entry].name);        // a fresh controller object for every run
        a.host.enabled = true;
        Physics.SyncTransforms();

        a.straight = DistanceToZone(pose.position, new Vector2(a.goalZone.position.x, a.goalZone.position.z));
        a.steps = 0;
        a.busy = true;
        bool wantPath = spec.map == PathMap && spec.noiseIdx == PathNoiseIdx && spec.seed <= PathLastSeed;
        a.path = wantPath ? new List<Vector2>() : null;
        if (wantPath) a.path.Add(new Vector2(a.rb.position.x - a.offset.x, a.rb.position.z - a.offset.z));
    }

    void Step(Arena a)
    {
        a.steps++;
        if (a.path != null && a.steps % PathEverySteps == 0)
            a.path.Add(new Vector2(a.rb.position.x - a.offset.x, a.rb.position.z - a.offset.z));
        if (a.status.Finished || a.status.ElapsedTime >= runCfg.maxRunTime) Finish(a);
    }

    void Finish(Arena a)
    {
        var s = a.status;
        bool goal = s.ReachedGoal, collided = !goal && s.Collided, timeout = !goal && !collided;
        string outcome = goal ? "goal" : collided ? "collision" : "timeout";
        float path = s.DistanceTravelled;
        string eff = goal && path > 1e-6f ? F(a.straight / path, "0.0000") : NA;
        string row = RowStart(a.spec) + "," + (goal ? 1 : 0) + "," + (collided ? 1 : 0) + "," + (timeout ? 1 : 0) + "," + a.host.ExceptionCount + ","
                     + F(path, "0.000") + "," + F(a.straight, "0.000") + "," + eff + "," + F(s.ElapsedTime, "0.00") + ","
                     + F(s.MinObstacleDistance * 100f, "0.0") + "," + outcome;
        WriteRow(row);
        if (a.path != null) SavePath(a);
        doneRuns++; simulatedThisSession++;

        a.host.enabled = false;
        a.drive.SetCommand(0f, 0f);
        a.busy = false;
        inflight.Remove(Key(a.spec)); WriteInflight();
    }

    void SavePath(Arena a)
    {
        var sb = new StringBuilder("t_s,x_m,z_m\n");
        for (int i = 0; i < a.path.Count; i++)
            sb.Append(F(i * PathEverySteps * Time.fixedDeltaTime, "0.00")).Append(',').Append(F(a.path[i].x, "0.000")).Append(',').Append(F(a.path[i].y, "0.000")).Append('\n');
        string file = entries[a.spec.entry].name + "_" + maps[a.spec.map].name + "_n" + F(noises[a.spec.noiseIdx], "0.00") + "_s" + a.spec.seed + ".csv";
        File.WriteAllText(Path.Combine(pathsDir, file), sb.ToString());
    }

    /// <summary>Seed of the sensor noise of one run: a fixed function of (map, noise level, trial seed) ONLY.</summary>
    public static int SensorSeed(int mapIdx, int noiseIdx, int trialSeed)
    {
        unchecked
        {
            uint h = (uint)(trialSeed + 1000 * (noiseIdx + 10 * mapIdx) + 7);
            h ^= h >> 16; h *= 0x85ebca6bu;
            h ^= h >> 13; h *= 0xc2b2ae35u;
            h ^= h >> 16;
            return (int)(h & 0x7fffffff);
        }
    }
}
