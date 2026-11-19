// RoverDrive: moves the rover by velocity control. A controller calls SetCommand(speed01, turn);
// every physics step (FixedUpdate) we set the Rigidbody's forward speed and turn rate directly,
// clamped to the SimConfig limits. No wheel physics. Positive turn = LEFT.
using UnityEngine;

[RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
public class RoverDrive : MonoBehaviour
{
    public SimConfig config;

    Rigidbody rb;
    float speedCommand;   // 0..1 of max speed (or -1..1 if SimConfig allows reversing)
    float turnCommand;    // -1..1 of max turn rate, positive = left

    /// <summary>Measured speed over the ground in m/s (ignores falling/rising).</summary>
    public float CurrentSpeed
    {
        get { var v = rb.linearVelocity; return new Vector2(v.x, v.z).magnitude; }
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (config == null) { Debug.LogError("RoverDrive: SimConfig is not assigned", this); enabled = false; return; }
        ApplyConfig();
    }

    /// <summary>Copies the rover's size, mass and visual choice (model or box) from SimConfig onto this object.</summary>
    public void ApplyConfig()
    {
        float w = config.roverWidth, h = config.roverHeight, l = config.roverLength;
        GetComponent<Rigidbody>().mass = config.roverMass;
        GetComponent<BoxCollider>().size = new Vector3(w, h, l);   // x = width, y = height, z = length (forward)

        var body = transform.Find("Body");
        if (body != null) body.localScale = new Vector3(w, h, l);

        // Visual-only choice: detailed model ("VisualModel" child) OR the plain box + nose. Physics is identical.
        var model = transform.Find("VisualModel");
        bool showModel = config.showVisualModel && model != null;
        if (model != null) model.gameObject.SetActive(showModel);
        SetRendererEnabled(body, !showModel);

        // The nose is a small bright marker on the front of the roof so the heading is visible from above on the plain
        // box. It is hidden whenever the Perseverance model is shown (the model shows the heading itself).
        var nose = transform.Find("Nose");
        if (nose != null)
        {
            float n = w * 0.4f;
            nose.localScale = new Vector3(n, h * 0.5f, n);
            nose.localPosition = new Vector3(0f, h * 0.5f, l * 0.5f - n * 0.5f);   // centred on the box roof
            SetRendererEnabled(nose, !showModel);
        }

        // Viewing aid: the path trail (a child object). Off when SimConfig.showViewAids is false.
        var trail = transform.Find("Trail");
        if (trail != null)
        {
            var line = trail.GetComponent<TrailRenderer>();
            if (line != null) line.widthMultiplier = config.trailWidth;
            trail.gameObject.SetActive(config.showViewAids);
        }

        // Sensors: re-place and re-configure them from SimConfig.
        var rig = GetComponent<SensorRig>();
        if (rig != null) rig.Setup();
    }

    static void SetRendererEnabled(Transform t, bool on)
    {
        if (t == null) return;
        var r = t.GetComponent<Renderer>();
        if (r != null) r.enabled = on;
    }

    /// <summary>speed01: fraction of max speed. turn: fraction of max turn rate, positive = left.</summary>
    public void SetCommand(float speed01, float turn)
    {
        speedCommand = config.allowReversing ? Mathf.Clamp(speed01, -1f, 1f) : Mathf.Clamp01(speed01);
        turnCommand = Mathf.Clamp(turn, -1f, 1f);
    }

    void FixedUpdate()
    {
        // Forward speed along the rover's own nose direction; keep the vertical part so gravity still works.
        Vector3 v = transform.forward * (speedCommand * config.maxSpeed);
        v.y = rb.linearVelocity.y;
        rb.linearVelocity = v;

        // Unity's +Y rotation turns RIGHT (clockwise from above), so a LEFT turn is a negative Y rate.
        float yawRate = -turnCommand * config.maxTurnRateDeg * Mathf.Deg2Rad;
        rb.angularVelocity = new Vector3(0f, yawRate, 0f);
    }
}
