// QuickTestWindow: Editor menu "Rover > Quick Test Controller". Pick a controller class, press Run: the Arena scene goes into
// Play mode, QuickTestRunner tests the controller on 10 practice maps (seeds 2000-2009, noise 0.05), Play mode stops by itself,
// and the one-line report (format: docs/feedback_template.txt) is shown here, printed in the Console, copied to the clipboard
// and saved with per-run details to results/quicktest/<controller>.txt. Never touches the four test maps.
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class QuickTestWindow : EditorWindow
{
    const string KeyPending = "QuickTest.pending";
    const string KeyReport = "QuickTest.report";
    const string KeyChoice = "QuickTest.choice";

    int choice;
    string[] names = new string[0];
    Vector2 scroll;

    [MenuItem("Rover/Quick Test Controller")]
    public static void Open() { GetWindow<QuickTestWindow>("Quick Test").Show(); }

    void OnEnable() { RefreshNames(); }

    void RefreshNames()
    {
        // LLM controllers first (the usual use), then the baselines, then the utility controllers
        names = ControllerRegistry.Names(ControllerKind.Llm).Concat(ControllerRegistry.Names(ControllerKind.Baseline))
            .Concat(ControllerRegistry.Names(ControllerKind.Utility)).Concat(ControllerRegistry.Names(ControllerKind.Excluded)).ToArray();
        string saved = SessionState.GetString(KeyChoice, "");
        int i = System.Array.IndexOf(names, saved);
        choice = i >= 0 ? i : 0;
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox("Runs the chosen controller once on each of 10 practice maps (seeds 2000-2009), noise 0.05. "
            + "The Arena scene must be open. Never uses the 4 test maps.", MessageType.Info);
        if (names.Length == 0) { EditorGUILayout.LabelField("No controller classes found."); if (GUILayout.Button("Refresh")) RefreshNames(); return; }

        EditorGUI.BeginChangeCheck();
        choice = EditorGUILayout.Popup("Controller", Mathf.Clamp(choice, 0, names.Length - 1), names);
        if (EditorGUI.EndChangeCheck()) SessionState.SetString(KeyChoice, names[choice]);

        string[] bad = ControllerRegistry.MisnamedLlmControllers();
        if (bad.Length > 0) EditorGUILayout.HelpBox("LLM class name should be LLM_<Model>_<Attempt>_<OS|FB>: " + string.Join(", ", bad), MessageType.Warning);

        bool busy = EditorApplication.isPlaying && QuickTestRunner.IsRunning;
        using (new EditorGUI.DisabledScope(busy || SessionState.GetBool(KeyPending, false)))
        {
            if (GUILayout.Button("Run Quick Test (10 practice maps)", GUILayout.Height(28))) Start(names[choice]);
        }
        if (busy) EditorGUILayout.LabelField("Running ... see the Game view.");

        string report = SessionState.GetString(KeyReport, "");
        if (report.Length > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Last report (also copied to the clipboard)", EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(150));
            EditorGUILayout.SelectableLabel(report, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }
    }

    /// <summary>Start a quick test of this controller class (enters Play mode if needed). Also usable from scripts.</summary>
    public static void Start(string controllerName)
    {
        if (!ControllerRegistry.Exists(controllerName)) { Debug.LogError("QuickTest: no controller class named " + controllerName); return; }
        if (Object.FindFirstObjectByType<MapLoader>() == null && !EditorApplication.isPlaying)
        { Debug.LogError("QuickTest: open the Arena scene first (no MapLoader found)."); return; }
        SessionState.SetString(KeyPending, controllerName);
        SessionState.SetString(KeyReport, "");
        if (EditorApplication.isPlaying) Launch();
        else EditorApplication.EnterPlaymode();
    }

    [InitializeOnLoadMethod]
    static void Hook()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && !string.IsNullOrEmpty(SessionState.GetString(KeyPending, ""))) Launch();
        };
    }

    static void Launch()
    {
        string name = SessionState.GetString(KeyPending, "");
        SessionState.SetString(KeyPending, "");
        if (string.IsNullOrEmpty(name)) return;
        QuickTestRunner.Completed += OnCompleted;
        var go = new GameObject("QuickTestRunner");
        var runner = go.AddComponent<QuickTestRunner>();
        runner.controllerName = name;
    }

    static void OnCompleted(QuickTestResult result)
    {
        QuickTestRunner.Completed -= OnCompleted;
        if (result != null)
        {
            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "results", "quicktest");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, result.controller + ".txt"), result.Details());
            SessionState.SetString(KeyReport, result.Report);
            EditorGUIUtility.systemCopyBuffer = result.Report;
            Debug.Log(result.Report);
        }
        EditorApplication.isPlaying = false;
    }
}
