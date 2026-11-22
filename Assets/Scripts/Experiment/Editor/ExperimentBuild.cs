// ExperimentBuild (editor only): (1) creates the scene Assets/Scenes/Experiment.unity that holds the ExperimentRunner, and
// (2) builds that scene as a Windows standalone player into Build/Experiment/ (RoverNavStudy.exe), so the long experiment does not
// run inside the Editor's Play mode (which can stall). The build step also writes Build/Experiment/controllers_manifest.txt: the list
// of every controller of the study with 1 = compiles / 0 = did not compile (a player cannot see the LLM_NoCompile folder).
// Batch mode:  Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod ExperimentBuild.BuildFromCommandLine
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ExperimentBuild
{
    public const string ScenePath = "Assets/Scenes/Experiment.unity";
    public const string BuildDir = "Build/Experiment";
    public const string ExeName = "RoverNavStudy.exe";

    [MenuItem("Rover/Experiment/Create Experiment Scene")]
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("ExperimentRunner");
        var runner = go.AddComponent<ExperimentRunner>();
        runner.config = AssetDatabase.LoadAssetAtPath<SimConfig>("Assets/SimConfig.asset");
        runner.roverPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Rover.prefab");
        runner.maps = new[] { "M1_Open", "M2_Cluttered", "M3_Corridor", "M4_Trap" }
            .Select(n => AssetDatabase.LoadAssetAtPath<MapDefinition>("Assets/Maps/" + n + ".asset")).ToArray();
        var cam = new GameObject("Main Camera");
        cam.tag = "MainCamera";
        cam.AddComponent<Camera>().backgroundColor = Color.black;
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("Experiment scene saved: " + ScenePath);
    }

    [MenuItem("Rover/Experiment/Build Player")]
    public static void BuildPlayer()
    {
        if (!File.Exists(ScenePath)) CreateScene();
        Directory.CreateDirectory(BuildDir);
        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = Path.Combine(BuildDir, ExeName),
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new System.Exception("Experiment build failed: " + report.summary.result + ", errors " + report.summary.totalErrors);
        File.WriteAllLines(Path.Combine(BuildDir, "controllers_manifest.txt"),
            ControllerRegistry.ExperimentEntries().Select(e => e.name + "," + (e.compiles ? 1 : 0)));
        Debug.Log("Experiment player built: " + Path.Combine(BuildDir, ExeName) + " (" + report.summary.totalSize / (1024 * 1024) + " MB, " + report.summary.totalTime + ")");
    }

    public static void BuildFromCommandLine()
    {
        try { BuildPlayer(); EditorApplication.Exit(0); }
        catch (System.Exception e) { Debug.LogError(e); EditorApplication.Exit(1); }
    }
}
