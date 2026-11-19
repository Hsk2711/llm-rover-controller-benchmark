// SensorBeamView: viewing aid only. Draws each sensor's rays as thin lines in the GAME view coloured by
// distance (red < 0.5 m, yellow 0.5-1.5 m, green > 1.5 m or nothing detected; limits are in SimConfig). Press G to show or hide them. It only reads the sensors' last measurement; it never changes
// a reading, a random number or the physics. When SimConfig.showViewAids is false (experiments) it draws nothing,
// ignores G and creates no objects. SensorRig adds this component automatically.
using UnityEngine;
using UnityEngine.InputSystem;

public class SensorBeamView : MonoBehaviour
{
    static readonly Color nearColor = Color.red;
    static readonly Color midColor = Color.yellow;
    static readonly Color farColor = Color.green;

    SensorRig rig;
    RoverDrive drive;
    LineRenderer[][] lines;          // [sensor][ray]
    Material material;
    bool visible = true;             // toggled with G
    bool gWasDown;

    void Awake()
    {
        rig = GetComponent<SensorRig>();
        drive = GetComponent<RoverDrive>();
    }

    void LateUpdate()
    {
        var cfg = drive != null ? drive.config : null;
        bool aidsOn = cfg != null && cfg.showViewAids;

        if (aidsOn)
        {
            // Edge detection (pressed now, not pressed last frame), same approach as the C key in CameraManager.
            var kb = Keyboard.current;
            bool down = kb != null && kb.gKey.isPressed;
            if (down && !gWasDown) visible = !visible;
            gWasDown = down;
        }

        if (!aidsOn || !visible || rig == null || rig.front == null || rig.left == null || rig.right == null)
        {
            SetAllEnabled(false);
            return;
        }

        var sensors = new[] { rig.front, rig.left, rig.right };
        EnsureLines(sensors, cfg);
        for (int s = 0; s < sensors.Length; s++)
        {
            var sensor = sensors[s];
            for (int i = 0; i < lines[s].Length; i++)
            {
                var line = lines[s][i];
                bool draw = sensor.HasMeasured && i < sensor.RayCount;
                line.enabled = draw;
                if (!draw) continue;
                line.widthMultiplier = cfg.beamWidth;
                line.SetPosition(0, sensor.RayOrigin);
                line.SetPosition(1, sensor.RayEnd(i));
                Color c = BeamColor(sensor, i, cfg);
                line.startColor = c;
                line.endColor = c;
            }
        }
    }

    // Colour by how far the ray travelled: red = close, yellow = medium, green = far or nothing detected (a ray that
    // hits nothing travels the full range, so it is automatically green).
    static Color BeamColor(UltrasonicSensor sensor, int ray, SimConfig cfg)
    {
        float d = sensor.RayHit(ray) ? Vector3.Distance(sensor.RayOrigin, sensor.RayEnd(ray)) : float.PositiveInfinity;
        if (d < cfg.beamNearDistance) return nearColor;
        if (d <= cfg.beamFarDistance) return midColor;
        return farColor;
    }

    // Create the line objects the first time they are needed (or if the number of rays changed).
    void EnsureLines(UltrasonicSensor[] sensors, SimConfig cfg)
    {
        int n = Mathf.Max(1, cfg.raysPerSensor);
        if (lines != null && lines[0].Length == n) return;

        if (lines != null) foreach (var set in lines) foreach (var l in set) if (l != null) Destroy(l.gameObject);
        if (material == null) material = new Material(Shader.Find("Sprites/Default"));   // unlit, takes the line colour

        lines = new LineRenderer[sensors.Length][];
        for (int s = 0; s < sensors.Length; s++)
        {
            lines[s] = new LineRenderer[n];
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject("Beam_" + s + "_" + i);
                go.transform.SetParent(transform, false);
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.sharedMaterial = material;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.numCapVertices = 0;
                lines[s][i] = line;
            }
        }
    }

    void SetAllEnabled(bool on)
    {
        if (lines == null) return;
        foreach (var set in lines) foreach (var l in set) if (l != null) l.enabled = on;
    }
}
