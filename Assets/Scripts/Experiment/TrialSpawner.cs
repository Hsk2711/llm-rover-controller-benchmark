// TrialSpawner: puts the rover on its start pose for one trial. Given a trial seed it picks a random point inside the start
// zone (the whole rover stays inside it, at least startWallClearance from its edge) and a random heading between -45 and +45 degrees from the direction of the
// goal, using a System.Random seeded from the trial seed (never UnityEngine.Random). The same seed always gives the same pose, whatever the
// controller, noise level or map. Sign convention: positive goal angle = the goal is to the LEFT of the rover's heading.
using UnityEngine;

public struct StartPose
{
    public Vector2 position;       // rover centre on the floor (x, z), metres
    public float yawDeg;           // Unity rotation about the vertical axis (0 = facing +z / north, positive turns RIGHT)
    public float goalAngleDeg;     // angle to the goal as the rover sees it: positive = goal to the LEFT
    /// <summary>Heading in the map frame (x east, z north): degrees counter-clockwise from east, 0..360. = 90 - yaw.</summary>
    public float HeadingDeg => Mathf.Repeat(90f - yawDeg, 360f);
}

public class TrialSpawner : MonoBehaviour
{
    public SimConfig config;
    public RoverDrive rover;
    public Transform startZone;      // found by the name "StartZone" if left empty (MapLoader moves it to the map's start)
    public Transform goalZone;       // found by the name "GoalZone" if left empty

    [Header("Demo (pressing Play)")]
    public bool spawnOnStart;        // place the rover for demoSeed when Play starts (the experiment runner does not use this)
    public int demoSeed = 1;

    void Start()
    {
        if (spawnOnStart) Spawn(demoSeed);
    }

    /// <summary>Place the rover for this trial seed, stop it and restart its run records. Returns the pose used.</summary>
    public StartPose Spawn(int trialSeed)
    {
        Resolve(ref startZone, "StartZone");
        Resolve(ref goalZone, "GoalZone");
        var pose = ComputePose(trialSeed,
            new Vector2(startZone.position.x, startZone.position.z), new Vector2(goalZone.position.x, goalZone.position.z), config);

        var rb = rover.GetComponent<Rigidbody>();
        var position = new Vector3(pose.position.x, rover.transform.position.y, pose.position.y);   // keep the rover's height
        var rotation = Quaternion.Euler(0f, pose.yawDeg, 0f);
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.position = position;
        rb.rotation = rotation;
        rover.transform.SetPositionAndRotation(position, rotation);
        Physics.SyncTransforms();

        var status = rover.GetComponent<RoverStatus>();
        if (status != null) status.ResetStatus();
        return pose;
    }

    /// <summary>The pose for a trial seed (no objects are moved). Same seed = same pose.</summary>
    public static StartPose ComputePose(int trialSeed, Vector2 startCentre, Vector2 goalCentre, SimConfig config)
    {
        var rng = new System.Random(MixSeed(trialSeed));
        double ux = rng.NextDouble(), uz = rng.NextDouble(), ua = rng.NextDouble();   // always three draws, in this order

        // Any point of the zone where the whole rover (even turned diagonally) stays at least startWallClearance away from the
        // zone edge. The clearance matters: with no gap the rover can touch the arena wall at the start, and the physics engine
        // reports that as a collision (it counts contacts inside its 1 cm contact margin), which would end the run at once.
        float halfDiagonal = 0.5f * Mathf.Sqrt(config.roverLength * config.roverLength + config.roverWidth * config.roverWidth);
        float range = config.zoneSize * 0.5f - halfDiagonal - config.startWallClearance;
        var position = new Vector2(startCentre.x + (float)(2 * ux - 1) * range, startCentre.y + (float)(2 * uz - 1) * range);
        float goalAngle = (float)(2 * ua - 1) * config.startHeadingRangeDeg;

        // yaw that faces the goal, then turned RIGHT by the goal angle (turning right makes the goal appear on the left)
        Vector2 toGoal = goalCentre - position;
        float yawToGoal = Mathf.Atan2(toGoal.x, toGoal.y) * Mathf.Rad2Deg;
        float yaw = Mathf.Repeat(yawToGoal + goalAngle, 360f);

        return new StartPose { position = position, yawDeg = yaw, goalAngleDeg = goalAngle };
    }

    // System.Random seeded with 1, 2, 3, ... gives a perfectly regular lattice (each draw grows by a constant step from one
    // seed to the next, so the poses would be evenly spaced, not random). Passing the trial seed through this nonlinear
    // integer hash (murmur3 finalizer) first removes that pattern. It is a fixed function: same trial seed = same pose.
    static int MixSeed(int trialSeed)
    {
        unchecked
        {
            uint h = (uint)trialSeed;
            h ^= h >> 16; h *= 0x85ebca6bu;
            h ^= h >> 13; h *= 0xc2b2ae35u;
            h ^= h >> 16;
            return (int)h;
        }
    }

    static void Resolve(ref Transform zone, string zoneName)
    {
        if (zone != null) return;
        var found = GameObject.Find(zoneName);
        if (found != null) zone = found.transform;
    }
}
