// ControllerHost: sits on the rover and is the ONLY thing that talks to the active controller. Every 0.05 s (SimConfig
// controllerInterval, counted in physics steps like the sensors) it builds an Observation from the sensors and the goal,
// asks the active controller through SafeController, and sends the answer to RoverDrive.SetCommand. The answer is held
// until the next decision. When the run is over (collision or goal reached) it stops the rover and stops asking.
// Pick the active controller in the Inspector dropdown (or call SetController from code, as the experiment runner will).
// A new run starts when RoverStatus.RunReset fires (e.g. TrialSpawner.Spawn): the controller is reset and the start
// position is remembered (only Bug2 uses it). Before Decide the Observation goes through Restrict(): every controller
// except Bug2 gets roverPos/startPos/goalPos = (0,0), so nobody else can use the rover's position.
using UnityEngine;

[DefaultExecutionOrder(100)]   // runs after SensorRig (order 0), so it sees the sensors' newest measurement
[RequireComponent(typeof(RoverDrive), typeof(SensorRig))]
public class ControllerHost : MonoBehaviour
{
    [ControllerChoice] public string controller = "ManualController";   // class name; chosen with the dropdown

    public string ControllerName => safe != null ? safe.Name : "(none)";
    public float LastSpeed { get; private set; }          // the command currently being sent
    public float LastTurn { get; private set; }
    public int DecisionCount { get; private set; }        // decisions made in this run
    public int ExceptionCount => safe != null ? safe.ExceptionCount : 0;
    public SafeController Safe => safe;

    /// <summary>null (default) = the goal is the object tagged Goal anywhere in the scene. The experiment runner sets this to the
    /// root of ONE arena, so the controller of that arena only sees its own goal.</summary>
    public Transform arenaRoot;

    RoverDrive drive;
    SensorRig rig;
    RoverStatus status;
    Rigidbody rb;
    Transform goal;
    SafeController safe;
    string appliedName;
    double timer;
    Vector2 startPos;
    bool warnedNoGoal;

    void Awake()
    {
        drive = GetComponent<RoverDrive>();
        rig = GetComponent<SensorRig>();
        status = GetComponent<RoverStatus>();
        rb = GetComponent<Rigidbody>();
    }

    void OnEnable() { if (status != null) status.RunReset += BeginRun; }
    void OnDisable() { if (status != null) status.RunReset -= BeginRun; }

    void Start() { SetController(controller); }

    // Changing the dropdown while the game is running switches the controller at once.
    void OnValidate()
    {
        if (Application.isPlaying && drive != null && controller != appliedName) SetController(controller);
    }

    /// <summary>Make the controller with this class name active (it is wrapped in a SafeController).</summary>
    public void SetController(string className)
    {
        var c = ControllerRegistry.Create(className);
        if (c == null) { safe = null; appliedName = className; return; }
        SetController(c);
    }

    /// <summary>Make this controller object active (it is wrapped in a SafeController).</summary>
    public void SetController(IRoverController c)
    {
        if (drive == null) Awake();
        safe = new SafeController(c, drive.config != null && drive.config.allowReversing);
        controller = appliedName = c.GetType().Name;
        BeginRun();
    }

    /// <summary>Start of a run: reset the controller and its counters, remember where the run started.</summary>
    public void BeginRun()
    {
        if (drive == null || drive.config == null) return;
        timer = drive.config.controllerInterval;           // decide at the very first physics step
        LastSpeed = LastTurn = 0f;
        DecisionCount = 0;
        startPos = new Vector2(rb.position.x, rb.position.z);
        if (safe != null) safe.ResetController();
    }

    void FixedUpdate()
    {
        if (safe == null || drive.config == null) return;

        if (status != null && status.Finished)             // run over: stop and stop asking the controller
        {
            LastSpeed = LastTurn = 0f;
            drive.SetCommand(0f, 0f);
            return;
        }

        timer += Time.fixedDeltaTime;
        double interval = drive.config.controllerInterval;
        if (timer < interval - 1e-9) return;               // between decisions the last command is simply held
        timer -= interval;

        if (!TryBuildObservation(out Observation obs)) { drive.SetCommand(0f, 0f); return; }

        var answer = safe.Decide(Restrict(obs, safe.Inner));
        LastSpeed = answer.speed01;
        LastTurn = answer.turn;
        DecisionCount++;
        drive.SetCommand(answer.speed01, answer.turn);
    }

    // Scene view: let the active controller draw its debugging lines (viewing aid, off during experiments).
    void OnDrawGizmos()
    {
        if (safe == null || drive == null || drive.config == null || !drive.config.showViewAids) return;
        (safe.Inner as IControllerGizmos)?.DrawGizmos();
    }

    /// <summary>What a controller is ALLOWED to see. Only Bug2 may know where it is: for every other controller the rover, start
    /// and goal positions are set to zero, so LLM controllers get exactly the inputs of docs/llm_task_spec.txt.</summary>
    public static Observation Restrict(Observation obs, IRoverController controller)
    {
        if (controller is Bug2Controller) return obs;
        obs.roverPos = obs.startPos = obs.goalPos = Vector2.zero;
        return obs;
    }

    /// <summary>The Observation a controller would get right now (also used by tests).</summary>
    public bool TryBuildObservation(out Observation obs)
    {
        obs = default;
        if (goal == null && arenaRoot != null)
        {
            foreach (var t in arenaRoot.GetComponentsInChildren<Transform>())
                if (t.CompareTag("Goal")) { goal = t; break; }
        }
        if (goal == null)
        {
            var g = GameObject.FindGameObjectWithTag("Goal");
            if (g != null) goal = g.transform;
        }
        if (goal == null)
        {
            if (!warnedNoGoal) Debug.LogError("ControllerHost: no object tagged Goal found", this);
            warnedNoGoal = true;
            return false;
        }

        var readings = rig.GetReadings();
        var pos = new Vector2(rb.position.x, rb.position.z);          // physics position (not the smoothed Transform)
        var goalPos = new Vector2(goal.position.x, goal.position.z);
        Vector3 f = rb.rotation * Vector3.forward;
        var forward = new Vector2(f.x, f.z);
        Vector2 toGoal = goalPos - pos;

        obs.front = readings.front;
        obs.left = readings.left;
        obs.right = readings.right;
        obs.leftSide = readings.leftSide;
        obs.rightSide = readings.rightSide;
        obs.goalDistance = toGoal.magnitude;
        // Vector2.SignedAngle is counter-clockwise positive in the (x east, z north) plane = toward the LEFT of the heading
        obs.goalAngleDeg = toGoal.sqrMagnitude < 1e-12f ? 0f : Vector2.SignedAngle(forward, toGoal);
        obs.roverPos = pos;
        obs.startPos = startPos;
        obs.goalPos = goalPos;
        return true;
    }
}
