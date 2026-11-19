// MapWindow: Editor-only tool, menu "Rover > Load Map". Lists every MapDefinition in Assets/Maps; a click builds that map
// in the open scene (using a MapLoader, which is added to the scene automatically if missing) so you can look at it.
// Also builds a random practice map from a seed, and clears the obstacles again. Works in Edit mode and Play mode.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class MapWindow : EditorWindow
{
    int practiceSeed = 1;

    [MenuItem("Rover/Load Map")]
    static void Open() { GetWindow<MapWindow>("Load Map"); }

    void OnGUI()
    {
        GUILayout.Label("Test maps (Assets/Maps)", EditorStyles.boldLabel);
        string[] guids = AssetDatabase.FindAssets("t:MapDefinition", new[] { "Assets/Maps" });
        if (guids.Length == 0) EditorGUILayout.HelpBox("No MapDefinition assets found in Assets/Maps.", MessageType.Info);
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var def = AssetDatabase.LoadAssetAtPath<MapDefinition>(path);
            if (def != null && GUILayout.Button(def.name + "   (" + def.obstacles.Count + " boxes)")) LoadTestMap(def);
        }

        GUILayout.Space(10);
        GUILayout.Label("Practice map (random, for chatbot feedback only)", EditorStyles.boldLabel);
        practiceSeed = EditorGUILayout.IntField("Seed", practiceSeed);
        if (GUILayout.Button("Generate practice map from seed")) LoadPractice();

        GUILayout.Space(10);
        if (GUILayout.Button("Clear obstacles"))
        {
            var loader = EnsureLoader();
            if (loader != null) { loader.Clear(); MarkDirty(loader); }
        }

        EditorGUILayout.HelpBox(
            "Obstacles are ordinary objects under 'MapLoader/Obstacles'. Press Clear before saving the scene if you do not " +
            "want them stored in it. When Play starts, the MapLoader rebuilds its current map by itself.", MessageType.None);
    }

    static void LoadTestMap(MapDefinition def)
    {
        var loader = EnsureLoader();
        if (loader == null) return;
        loader.Load(def);          // also remembers it as the loader's map, so Play shows the same map
        MarkDirty(loader);
    }

    void LoadPractice()
    {
        var loader = EnsureLoader();
        if (loader == null) return;
        var def = PracticeMapGenerator.Generate(practiceSeed, loader.config);
        loader.Load(def);
        loader.map = null;         // a practice map is not an asset, so the scene must not keep a reference to it
        DestroyImmediate(def);
        MarkDirty(loader);
        Debug.Log("Practice map from seed " + practiceSeed + " loaded.");
    }

    // Writes results/start_poses.csv: the start pose of every trial seed (firstTrialSeed .. firstTrialSeed+trialCount-1).
    // heading_deg: rover heading in the map frame, counter-clockwise from east (+x), 0..360 (Unity yaw = 90 - heading).
    // goal_angle_deg: angle to the goal as the rover sees it, positive = goal to the LEFT.
    [MenuItem("Rover/Export Start Poses (CSV)")]
    static void ExportStartPoses()
    {
        var config = AssetDatabase.LoadAssetAtPath<SimConfig>("Assets/SimConfig.asset");
        var map = AssetDatabase.LoadAssetAtPath<MapDefinition>("Assets/Maps/M1_Open.asset");   // all maps share the zone centres
        if (config == null || map == null) { Debug.LogError("Export Start Poses: SimConfig or map not found"); return; }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var csv = new System.Text.StringBuilder("seed,x,z,heading_deg,goal_angle_deg\n");
        for (int seed = config.firstTrialSeed; seed < config.firstTrialSeed + config.trialCount; seed++)
        {
            StartPose p = TrialSpawner.ComputePose(seed, map.startZoneCentre, map.goalZoneCentre, config);
            csv.Append(seed).Append(',').Append(p.position.x.ToString("0.0000", inv)).Append(',').Append(p.position.y.ToString("0.0000", inv))
               .Append(',').Append(p.HeadingDeg.ToString("0.00", inv)).Append(',').Append(p.goalAngleDeg.ToString("0.00", inv)).Append('\n');
        }
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "results", "start_poses.csv"));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
        System.IO.File.WriteAllText(full, csv.ToString());
        Debug.Log("Saved " + full);
    }

    static void MarkDirty(MapLoader loader)
    {
        if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(loader.gameObject.scene);
    }

    // Find the scene's MapLoader, or add one (at the origin) and fill in its SimConfig and material.
    static MapLoader EnsureLoader()
    {
        var loader = Object.FindFirstObjectByType<MapLoader>();
        if (loader == null)
        {
            var go = new GameObject("MapLoader");
            Undo.RegisterCreatedObjectUndo(go, "Add MapLoader");
            loader = go.AddComponent<MapLoader>();
        }
        if (loader.config == null) loader.config = AssetDatabase.LoadAssetAtPath<SimConfig>("Assets/SimConfig.asset");
        if (loader.obstacleMaterial == null) loader.obstacleMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Obstacle.mat");
        if (loader.config == null) { Debug.LogError("Rover > Load Map: Assets/SimConfig.asset not found"); return null; }
        if (loader.transform.position != Vector3.zero) Debug.LogWarning("MapLoader should sit at the world origin (0,0,0).", loader);
        return loader;
    }
}
