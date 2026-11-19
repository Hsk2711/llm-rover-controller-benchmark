// RoverHud: updates the on-screen text boxes (rover readings, view label). Display only; it never changes
// the simulation. When SimConfig.showViewAids is false (experiments) the whole HUD canvas is switched off.
using UnityEngine;

public class RoverHud : MonoBehaviour
{
    public RoverStatus status;
    public RoverDrive drive;
    public SensorRig sensors;                   // optional: adds the three range readings
    public UnityEngine.UI.Text text;

    Canvas canvas;

    void Awake() { canvas = GetComponent<Canvas>(); }

    void Update()
    {
        if (status == null || drive == null || text == null) return;

        bool on = drive.config != null && drive.config.showViewAids;
        if (canvas != null && canvas.enabled != on) canvas.enabled = on;
        if (!on) return;

        string closest = float.IsPositiveInfinity(status.MinObstacleDistance)
            ? "-" : status.MinObstacleDistance.ToString("0.00") + " m";

        text.text =
            "Speed: " + drive.CurrentSpeed.ToString("0.00") + " m/s\n" +
            "Distance: " + status.DistanceTravelled.ToString("0.00") + " m\n" +
            "Time: " + status.ElapsedTime.ToString("0.0") + " s\n" +
            "Collided: " + (status.Collided ? "YES" : "no") + "\n" +
            "Reached goal: " + (status.ReachedGoal ? "YES" : "no") + "\n" +
            "Closest obstacle: " + closest;

        if (sensors != null)
        {
            var r = sensors.GetReadings();
            text.text +=
                "\nFront: " + r.front.ToString("0.00") + " m" +
                "\nLeft: " + r.left.ToString("0.00") + " m" +
                "\nRight: " + r.right.ToString("0.00") + " m";
        }
    }
}
