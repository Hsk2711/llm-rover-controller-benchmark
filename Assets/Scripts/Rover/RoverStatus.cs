// RoverStatus: records what happens to the rover in one run: whether it touched a Wall/Obstacle,
// whether its centre is inside the Goal zone, distance travelled, elapsed simulated time and the
// smallest gap to any Wall/Obstacle. Recording stops the moment the run is finished (collision or goal).
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
public class RoverStatus : MonoBehaviour
{
    public bool Collided { get; private set; }
    public bool ReachedGoal { get; private set; }
    public float DistanceTravelled { get; private set; }          // metres, along the ground
    public float ElapsedTime { get; private set; }                // simulated seconds (physics time)
    public float MinObstacleDistance { get; private set; } = float.PositiveInfinity; // metres, surface to surface
    public bool Finished => Collided || ReachedGoal;

    /// <summary>Raised whenever a new run starts (ResetStatus). Viewing aids use it to clear themselves.</summary>
    public event System.Action RunReset;

    Rigidbody rb;                // read positions from the physics body, not the (interpolated) Transform
    Collider body;
    Collider goal;
    Vector3 lastPosition;
    readonly List<Collider> obstacles = new List<Collider>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        body = GetComponent<Collider>();
    }

    void Start()
    {
        RefreshTargets();
        ResetStatus();
    }

    /// <summary>Re-find the Goal and all Wall/Obstacle colliders. Call again after a map is loaded.</summary>
    public void RefreshTargets()
    {
        obstacles.Clear();
        foreach (string tag in new[] { "Wall", "Obstacle" })
            foreach (var go in GameObject.FindGameObjectsWithTag(tag))
                obstacles.AddRange(go.GetComponentsInChildren<Collider>());

        var goalObject = GameObject.FindGameObjectWithTag("Goal");
        goal = goalObject != null ? goalObject.GetComponent<Collider>() : null;
        if (goal == null) Debug.LogWarning("RoverStatus: no object tagged Goal with a collider found", this);
    }

    /// <summary>Clear all records and start a fresh run from the current position.</summary>
    public void ResetStatus()
    {
        Collided = false;
        ReachedGoal = false;
        DistanceTravelled = 0f;
        ElapsedTime = 0f;
        lastPosition = rb.position;
        MinObstacleDistance = ComputeObstacleDistance();
        RunReset?.Invoke();
    }

    void FixedUpdate()
    {
        if (Finished) return;

        ElapsedTime += Time.fixedDeltaTime;

        Vector3 p = rb.position;
        DistanceTravelled += new Vector2(p.x - lastPosition.x, p.z - lastPosition.z).magnitude;
        lastPosition = p;

        MinObstacleDistance = Mathf.Min(MinObstacleDistance, ComputeObstacleDistance());

        // Goal reached = rover CENTRE inside the goal zone's footprint (top-down x/z test).
        if (goal != null)
        {
            Bounds b = goal.bounds;
            if (p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z)
                ReachedGoal = true;
        }
    }

    void OnCollisionEnter(Collision collision)
    {
        if (Finished) return;
        if (collision.gameObject.CompareTag("Wall") || collision.gameObject.CompareTag("Obstacle"))
        {
            Collided = true;
            MinObstacleDistance = 0f;   // touching = zero gap
        }
    }

    // Gap between the rover's surface and the nearest Wall/Obstacle surface (0 when touching).
    float ComputeObstacleDistance()
    {
        float best = float.PositiveInfinity;
        foreach (var c in obstacles)
        {
            if (c == null) continue;
            Vector3 onObstacle = c.ClosestPoint(rb.position);
            Vector3 onRover = body.ClosestPoint(onObstacle);
            best = Mathf.Min(best, Vector3.Distance(onObstacle, onRover));
        }
        return best;
    }
}
