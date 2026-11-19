// SimConfig: one data asset holding every fixed number of the study (arena, rover, sensors,
// timing, run limits). Scripts read their numbers from here; nothing is hard-coded elsewhere.
using UnityEngine;

[CreateAssetMenu(fileName = "SimConfig", menuName = "RoverNavStudy/SimConfig")]
public class SimConfig : ScriptableObject
{
    [Header("Arena (metres)")]
    public float arenaSize = 6f;          // arena is arenaSize x arenaSize
    public float wallHeight = 0.5f;
    public float zoneSize = 1f;           // start zone and goal zone are zoneSize x zoneSize
    public float obstacleHeight = 0.5f;   // box obstacles built from a MapDefinition (same height as the walls)
    public float wallThickness = 0.1f;    // walls sit OUTSIDE the arena, so the inside stays arenaSize
    public float floorThickness = 0.1f;   // floor top surface is at y = 0
    public float cameraMargin = 0.4f;     // extra empty space (m) around the outside of the walls in the top-down view

    [Header("Rover body (metres, kg)")]
    public float roverLength = 0.20f;
    public float roverWidth = 0.15f;
    public float roverHeight = 0.08f;
    public float roverMass = 1f;

    [Header("Rover motion (velocity control, no wheel physics)")]
    public float maxSpeed = 0.3f;         // m/s
    public float maxTurnRateDeg = 90f;    // deg/s; positive turn = LEFT
    public bool allowReversing = false;   // no reversing

    [Header("Ultrasonic sensors")]
    // Mounting angles in degrees: 0 = front, +45 = left, -45 = right (all three at the middle of the front face), then
    // +90 = left side and -90 = right side (at the middle of each side of the body). Five sensors in total.
    public float[] sensorAnglesDeg = { 0f, 45f, -45f, 90f, -90f };
    public int raysPerSensor = 5;
    public float coneHalfAngleDeg = 15f;  // rays spread over +-this angle; reading = shortest hit
    public float minRange = 0.02f;        // m
    public float maxRange = 4.0f;         // m; also the value returned for "no hit"
    public float sensorUpdateHz = 20f;
    // reading = true * (1 + e), e ~ Normal(0, noise^2); one run uses one of these levels.
    public float[] noiseLevels = { 0f, 0.02f, 0.05f, 0.10f };
    public float dropoutProbability = 0.02f; // chance a reading is lost and returns maxRange

    [Header("Timing (seconds)")]
    public float controllerInterval = 0.05f; // controllers decide at 20 Hz
    public float physicsTimestep = 0.02f;    // Unity fixed timestep

    [Header("Trial start pose")]
    // TrialSpawner: random point inside the start zone, random goal angle within +-this (positive = goal to the LEFT).
    public float startHeadingRangeDeg = 45f;
    public float startWallClearance = 0.05f;     // m: the rover starts at least this far from the zone edge (= arena wall), even turned diagonally
    public int firstTrialSeed = 1;               // the study uses trial seeds 1..30
    public int trialCount = 30;

    [Header("Visuals")]
    // true = show the detailed Perseverance model (demos, screenshots); false = plain box rover.
    // Visual only: the invisible BoxCollider is always the real body. The ExperimentRunner must set this to FALSE.
    public bool showVisualModel = true;
    // true = viewing aids: follow camera (key C), path trail, sensor beams in the Game view (key G), HUD. Visual only.
    public bool showViewAids = true;
    public float followCameraHeight = 1.2f;  // FOLLOW view: metres above the rover, looking straight down
    public float chaseBackDistance = 0.6f;   // CHASE view: metres behind the rover
    public float chaseHeight = 0.35f;        // CHASE view: metres above the floor
    public float chasePitchDeg = 20f;        // CHASE view: tilt looking down ("slightly down")
    public float cameraFieldOfView = 60f;    // FOLLOW and CHASE views (ARENA is orthographic)
    public float cameraSmoothing = 6f;       // higher = camera sticks closer to the rover (per second)
    public float trailWidth = 0.015f;        // metres, path line behind the rover (cyan)
    public float beamWidth = 0.01f;          // metres, sensor beam lines in the Game view (key G shows/hides them)
    public float beamNearDistance = 0.5f;    // m, beam is RED below this distance
    public float beamFarDistance = 1.5f;     // m, beam is YELLOW up to this distance, GREEN beyond (or if nothing detected)

    /// <summary>Switch off everything that is only for looking at. Call this on a runtime COPY of the config
    /// (Object.Instantiate), never on the asset, before running experiments.</summary>
    public void SetExperimentMode()
    {
        showVisualModel = false;
        showViewAids = false;
    }

    [Header("Run limits")]
    public float maxRunTime = 60f;        // a run also ends on goal reached or any collision
}
