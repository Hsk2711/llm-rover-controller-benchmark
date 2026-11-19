// SensorRig: the rover's five ultrasonic sensors. Three at the middle of the FRONT face: front 0 deg, left +45, right -45.
// Two at the middle of each SIDE of the body: left side +90, right side -90 (added in the pilot revision). It owns the ONE
// seeded System.Random shared by all sensors, sets the noise level, and ticks the sensors in a fixed order every physics
// step so runs are reproducible. Controllers get the readings through GetReadings().
using UnityEngine;

[RequireComponent(typeof(RoverDrive))]
public class SensorRig : MonoBehaviour
{
    public UltrasonicSensor front;       // mounting angle sensorAnglesDeg[0] = 0
    public UltrasonicSensor left;        // sensorAnglesDeg[1] = +45 (positive = LEFT)
    public UltrasonicSensor right;       // sensorAnglesDeg[2] = -45
    public UltrasonicSensor leftSide;    // sensorAnglesDeg[3] = +90, on the left side of the body
    public UltrasonicSensor rightSide;   // sensorAnglesDeg[4] = -90, on the right side of the body

    [SerializeField] float noiseLevel = 0.05f;   // one of SimConfig.noiseLevels (0, 0.02, 0.05, 0.10)

    System.Random rng;
    int seed;
    RoverDrive drive;

    /// <summary>Noise standard deviation as a fraction of the true distance. Applies to all five sensors.</summary>
    public float NoiseLevel
    {
        get => noiseLevel;
        set
        {
            noiseLevel = value;
            foreach (var s in Sensors()) if (s != null) s.NoiseLevel = value;
        }
    }

    /// <summary>Restart the random generator from this seed (same seed = same noise and dropouts). Also resets each
    /// sensor's timer and readings, so the sequence of measurements restarts from the beginning.</summary>
    public void SetSeed(int newSeed)
    {
        seed = newSeed;
        rng = new System.Random(newSeed);
        foreach (var s in Sensors()) if (s != null) s.SetRandom(rng);
        Setup();
    }

    /// <summary>Latest readings in metres: front, left (+45), right (-45), left side (+90), right side (-90).</summary>
    public (float front, float left, float right, float leftSide, float rightSide) GetReadings()
    {
        return (front.LastReading, left.LastReading, right.LastReading, leftSide.LastReading, rightSide.LastReading);
    }

    void Awake()
    {
        Setup();
        // Viewing aid only: draws the beams in the Game view. Added here so no prefab edit is needed.
        if (GetComponent<SensorBeamView>() == null) gameObject.AddComponent<SensorBeamView>();
    }

    /// <summary>Place and configure the sensors from SimConfig (also called by RoverDrive.ApplyConfig).</summary>
    public void Setup()
    {
        if (drive == null) drive = GetComponent<RoverDrive>();
        var cfg = drive.config;
        if (cfg == null || front == null || left == null || right == null) return;
        if (leftSide == null || rightSide == null) { Debug.LogError("SensorRig: leftSide / rightSide sensors are not assigned", this); return; }
        if (cfg.sensorAnglesDeg.Length < 5) { Debug.LogError("SimConfig.sensorAnglesDeg needs 5 angles", this); return; }

        if (rng == null) rng = new System.Random(seed);       // default seed 0 until SetSeed is called

        MountFront(front, cfg, cfg.sensorAnglesDeg[0]);
        MountFront(left, cfg, cfg.sensorAnglesDeg[1]);
        MountFront(right, cfg, cfg.sensorAnglesDeg[2]);
        MountSide(leftSide, cfg, cfg.sensorAnglesDeg[3]);
        MountSide(rightSide, cfg, cfg.sensorAnglesDeg[4]);
    }

    void MountFront(UltrasonicSensor s, SimConfig cfg, float angleDeg)
    {
        // middle of the front face, at body mid-height; positive angle = LEFT, and Unity's +Y rotation turns right
        s.transform.localPosition = new Vector3(0f, 0f, cfg.roverLength * 0.5f);
        s.transform.localRotation = Quaternion.Euler(0f, -angleDeg, 0f);
        s.Initialize(cfg, rng, noiseLevel);
    }

    void MountSide(UltrasonicSensor s, SimConfig cfg, float angleDeg)
    {
        // middle of the side of the body (halfway along its length), at body mid-height, looking sideways.
        // In the rover's own axes +x is to the RIGHT, so a LEFT sensor (positive angle) sits at -x.
        float x = angleDeg > 0f ? -cfg.roverWidth * 0.5f : cfg.roverWidth * 0.5f;
        s.transform.localPosition = new Vector3(x, 0f, 0f);
        s.transform.localRotation = Quaternion.Euler(0f, -angleDeg, 0f);
        s.Initialize(cfg, rng, noiseLevel);
    }

    void FixedUpdate()
    {
        if (drive == null || drive.config == null || front == null || leftSide == null || rightSide == null) return;
        double dt = Time.fixedDeltaTime;
        // fixed order => the shared random stream is always consumed in the same order
        front.Tick(dt);
        left.Tick(dt);
        right.Tick(dt);
        leftSide.Tick(dt);
        rightSide.Tick(dt);
    }

    System.Collections.Generic.IEnumerable<UltrasonicSensor> Sensors()
    {
        yield return front;
        yield return left;
        yield return right;
        yield return leftSide;
        yield return rightSide;
    }
}
