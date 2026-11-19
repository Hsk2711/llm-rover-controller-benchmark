// UltrasonicSensor: one simulated ultrasonic range sensor. Every 1/sensorUpdateHz seconds it casts several rays
// in a horizontal fan (+-coneHalfAngleDeg), takes the SHORTEST hit as the true distance (no hit = maxRange),
// then makes the reading: true x (1 + e), e ~ Normal(0, noise^2) (Box-Muller), clipped to [minRange, maxRange];
// with probability dropoutProbability the echo is lost and the reading is maxRange. If NO ray hits anything within
// range the reading is exactly maxRange: noise and dropout apply only to real echoes. All randomness comes from the
// System.Random it is given (never UnityEngine.Random). It does not update itself: SensorRig calls Tick() in a fixed
// order so the shared random stream is consumed in the same order every run. Rays are drawn as Scene-view gizmos.
using System;
using UnityEngine;

public class UltrasonicSensor : MonoBehaviour
{
    public SimConfig config;

    /// <summary>What a controller sees: noisy, clamped distance in metres.</summary>
    public float LastReading { get; private set; }
    /// <summary>Ground truth: shortest ray distance in metres (maxRange when nothing was hit). Not noisy.</summary>
    public float LastTrueDistance { get; private set; }
    /// <summary>True if a real echo was lost in the last measurement (reading forced to maxRange). Always false in open space.</summary>
    public bool LastWasDropout { get; private set; }
    /// <summary>Noise level (standard deviation as a fraction of the distance), e.g. 0.05 = 5 %.</summary>
    public float NoiseLevel { get; set; }

    System.Random rng;
    Rigidbody ownBody;                                  // hits on the rover's own collider are ignored
    double timer;                                        // seconds since the last measurement
    readonly RaycastHit[] hitBuffer = new RaycastHit[16];

    // last measurement, for drawing (world space)
    Vector3 drawOrigin;
    Vector3[] drawEnds = new Vector3[0];
    bool[] drawHit = new bool[0];
    bool hasMeasured;

    /// <summary>Give the sensor its settings and the shared random generator.</summary>
    public void Initialize(SimConfig cfg, System.Random sharedRandom, float noiseLevel)
    {
        config = cfg;
        rng = sharedRandom;
        NoiseLevel = noiseLevel;
        ownBody = GetComponentInParent<Rigidbody>();
        timer = 1.0 / config.sensorUpdateHz;             // measure on the very first Tick
        LastReading = LastTrueDistance = config.maxRange;
        LastWasDropout = false;
        hasMeasured = false;
        int n = Mathf.Max(1, config.raysPerSensor);
        if (drawEnds.Length != n) { drawEnds = new Vector3[n]; drawHit = new bool[n]; }
    }

    /// <summary>Replace the random generator (used when SensorRig.SetSeed is called).</summary>
    public void SetRandom(System.Random sharedRandom) { rng = sharedRandom; }

    /// <summary>Advance sensor time by dt seconds; measures once whenever 1/sensorUpdateHz has elapsed.</summary>
    public void Tick(double dt)
    {
        double interval = 1.0 / config.sensorUpdateHz;
        timer += dt;
        if (timer >= interval - 1e-9)
        {
            timer -= interval;
            MeasureNow();
        }
    }

    /// <summary>Take one measurement immediately (also used by tests / the accuracy test).</summary>
    public void MeasureNow()
    {
        int n = Mathf.Max(1, config.raysPerSensor);
        if (drawEnds.Length != n) { drawEnds = new Vector3[n]; drawHit = new bool[n]; }

        Vector3 origin = transform.position;
        float shortest = config.maxRange;                // no hit = maxRange
        bool anyHit = false;                             // did any ray hit something within range?
        drawOrigin = origin;

        for (int i = 0; i < n; i++)
        {
            float offsetDeg = n == 1 ? 0f : -config.coneHalfAngleDeg + i * (2f * config.coneHalfAngleDeg / (n - 1));
            Vector3 dir = Quaternion.AngleAxis(offsetDeg, transform.up) * transform.forward;

            float rayDist = config.maxRange;
            bool hit = false;
            int count = Physics.RaycastNonAlloc(origin, dir, hitBuffer, config.maxRange, ~0, QueryTriggerInteraction.Ignore);
            for (int j = 0; j < count; j++)
            {
                if (ownBody != null && hitBuffer[j].collider.attachedRigidbody == ownBody) continue;   // skip the rover itself
                if (hitBuffer[j].distance < rayDist) { rayDist = hitBuffer[j].distance; hit = true; }
            }
            drawEnds[i] = origin + dir * rayDist;
            drawHit[i] = hit;
            if (hit) anyHit = true;
            if (rayDist < shortest) shortest = rayDist;
        }
        LastTrueDistance = shortest;

        // Always draw the same 3 random numbers (dropout, and two for Box-Muller), whatever the noise level or
        // outcome, so one seed gives one fixed random stream.
        double uDrop = rng.NextDouble();
        double u1 = 1.0 - rng.NextDouble();              // (0, 1], so Log(u1) is finite
        double u2 = rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);   // standard normal (Box-Muller)

        if (!anyHit)
        {
            // Open space: no echo at all. The sensor reports exactly maxRange, with no noise. (There is no echo
            // to lose, so this is not counted as a dropout either.)
            LastWasDropout = false;
            LastReading = config.maxRange;
        }
        else
        {
            // A real echo: it may be lost (dropout -> maxRange), otherwise true x (1 + e), clipped to [minRange, maxRange].
            LastWasDropout = uDrop < config.dropoutProbability;
            if (LastWasDropout)
            {
                LastReading = config.maxRange;
            }
            else
            {
                double noisy = LastTrueDistance * (1.0 + NoiseLevel * z);
                LastReading = (float)Math.Min(config.maxRange, Math.Max(config.minRange, noisy));
            }
        }
        hasMeasured = true;
    }

    // Scene view: green ray = nothing hit, red ray = hit. Only drawn while the viewing aids are on.
    void OnDrawGizmos()
    {
        if (config == null || !config.showViewAids) return;

        if (hasMeasured)
        {
            for (int i = 0; i < drawEnds.Length; i++)
            {
                Gizmos.color = drawHit[i] ? Color.red : Color.green;
                Gizmos.DrawLine(drawOrigin, drawEnds[i]);
            }
        }
        else
        {
            // not measured yet (edit mode): show the fan at full range
            int n = Mathf.Max(1, config.raysPerSensor);
            Gizmos.color = Color.green;
            for (int i = 0; i < n; i++)
            {
                float offsetDeg = n == 1 ? 0f : -config.coneHalfAngleDeg + i * (2f * config.coneHalfAngleDeg / (n - 1));
                Vector3 dir = Quaternion.AngleAxis(offsetDeg, transform.up) * transform.forward;
                Gizmos.DrawLine(transform.position, transform.position + dir * config.maxRange);
            }
        }
    }
}
