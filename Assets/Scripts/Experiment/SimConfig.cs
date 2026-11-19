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
    // Mounting angles in degrees: 0 = front, +45 = left, -45 = right.
    public float[] sensorAnglesDeg = { 0f, 45f, -45f };
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

    [Header("Run limits")]
    public float maxRunTime = 60f;        // a run also ends on goal reached or any collision
}
