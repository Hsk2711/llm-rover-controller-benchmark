// RoverTrail: sits on the rover's "Trail" child (a TrailRenderer that draws the path driven).
// Visual only. It clears the line when a new run starts. RoverDrive.ApplyConfig switches the whole
// Trail child off when SimConfig.showViewAids is false (experiments).
using UnityEngine;

public class RoverTrail : MonoBehaviour
{
    TrailRenderer trail;
    RoverStatus status;

    void Awake()
    {
        trail = GetComponent<TrailRenderer>();
        status = GetComponentInParent<RoverStatus>();
    }

    void OnEnable()
    {
        if (status != null) status.RunReset += Clear;
        Clear();
    }

    void OnDisable()
    {
        if (status != null) status.RunReset -= Clear;
    }

    public void Clear()
    {
        if (trail != null) trail.Clear();
    }
}
