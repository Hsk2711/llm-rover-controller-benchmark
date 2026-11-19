// SensorRig: the rover's three ultrasonic sensors (front 0 deg, left +45, right -45, mounted at the middle of the
// front face). It owns the ONE seeded System.Random shared by all sensors, sets the noise level, and ticks the
// sensors in a fixed order every physics step so runs are reproducible. Controllers call GetReadings().
using UnityEngine;

[RequireComponent(typeof(RoverDrive))]
public class SensorRig : MonoBehaviour
{
    public UltrasonicSensor front;   // mounting angle sensorAnglesDeg[0] = 0
    public UltrasonicSensor left;    // sensorAnglesDeg[1] = +45 (positive = LEFT)
    public UltrasonicSensor right;   // sensorAnglesDeg[2] = -45

    [SerializeField] float noiseLevel = 0.05f;   // one of SimConfig.noiseLevels (0, 0.02, 0.05, 0.10)

    System.Random rng;
    int seed;
    RoverDrive drive;

    /// <summary>Noise standard deviation as a fraction of the true distance. Applies to all three sensors.</summary>
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

    /// <summary>Latest readings in metres: front, left, right.</summary>
    public (float front, float left, float right) GetReadings()
    {
        return (front.LastReading, left.LastReading, right.LastReading);
    }

    void Awake() { Setup(); }

    /// <summary>Place and configure the sensors from SimConfig (also called by RoverDrive.ApplyConfig).</summary>
    public void Setup()
    {
        if (drive == null) drive = GetComponent<RoverDrive>();
        var cfg = drive.config;
        if (cfg == null || front == null || left == null || right == null) return;
        if (cfg.sensorAnglesDeg.Length < 3) { Debug.LogError("SimConfig.sensorAnglesDeg needs 3 angles", this); return; }

        if (rng == null) rng = new System.Random(seed);       // default seed 0 until SetSeed is called

        Mount(front, cfg, cfg.sensorAnglesDeg[0]);
        Mount(left, cfg, cfg.sensorAnglesDeg[1]);
        Mount(right, cfg, cfg.sensorAnglesDeg[2]);
    }

    void Mount(UltrasonicSensor s, SimConfig cfg, float angleDeg)
    {
        // middle of the front face, at body mid-height; positive angle = LEFT, and Unity's +Y rotation turns right
        s.transform.localPosition = new Vector3(0f, 0f, cfg.roverLength * 0.5f);
        s.transform.localRotation = Quaternion.Euler(0f, -angleDeg, 0f);
        s.Initialize(cfg, rng, noiseLevel);
    }

    void FixedUpdate()
    {
        if (drive == null || drive.config == null || front == null) return;
        double dt = Time.fixedDeltaTime;
        // fixed order => the shared random stream is always consumed in the same order
        front.Tick(dt);
        left.Tick(dt);
        right.Tick(dt);
    }

    System.Collections.Generic.IEnumerable<UltrasonicSensor> Sensors()
    {
        yield return front;
        yield return left;
        yield return right;
    }
}
