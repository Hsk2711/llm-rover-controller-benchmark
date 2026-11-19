// CameraManager: viewing only. Press C to cycle ARENA (whole arena, top-down) -> FOLLOW (top-down above
// the rover) -> CHASE (3D camera behind and above the rover). One camera is moved between the views;
// follow/chase move smoothly. Shows a small "View: ..." label. When SimConfig.showViewAids is false
// (experiments) it keeps the ARENA view, ignores C and hides the label.
using UnityEngine;
using UnityEngine.InputSystem;

public class CameraManager : MonoBehaviour
{
    public enum ViewMode { Arena, Follow, Chase }

    public Camera cam;
    public RoverDrive rover;                     // its config holds all view numbers and the on/off switch
    public UnityEngine.UI.Text viewLabel;        // optional on-screen label

    public ViewMode Mode { get; private set; } = ViewMode.Arena;

    Vector3 arenaPosition;
    Quaternion arenaRotation;
    float arenaOrthoSize;
    bool snap = true;                            // jump straight to the new view on the first frame after a switch
    bool cWasDown;                               // C key state in the previous frame

    SimConfig Config => rover != null ? rover.config : null;
    bool AidsOn => Config != null && Config.showViewAids;

    void Start()
    {
        if (cam == null) cam = Camera.main;
        arenaPosition = cam.transform.position;  // the scene's camera starts as the full-arena view
        arenaRotation = cam.transform.rotation;
        arenaOrthoSize = cam.orthographicSize;
        cam.nearClipPlane = 0.05f;               // close enough for the low chase camera
        ApplyLabel();
    }

    void Update()
    {
        if (!AidsOn)
        {
            if (Mode != ViewMode.Arena) SetMode(ViewMode.Arena);
            return;
        }
        // Edge detection on the key state (pressed now, not pressed last frame): independent of which
        // Input System update step delivered the key event.
        var kb = Keyboard.current;
        bool down = kb != null && kb.cKey.isPressed;
        if (down && !cWasDown) Next();
        cWasDown = down;
    }

    public void Next()
    {
        if (!AidsOn) return;
        SetMode((ViewMode)(((int)Mode + 1) % 3));
    }

    void SetMode(ViewMode mode)
    {
        Mode = mode;
        snap = true;
        ApplyLabel();
    }

    void LateUpdate()
    {
        if (cam == null || rover == null || Config == null) return;

        if (Mode == ViewMode.Arena || !AidsOn)
        {
            cam.orthographic = true;
            cam.orthographicSize = arenaOrthoSize;
            cam.transform.SetPositionAndRotation(arenaPosition, arenaRotation);
            return;
        }

        cam.orthographic = false;
        cam.fieldOfView = Config.cameraFieldOfView;

        Transform r = rover.transform;
        Vector3 wantedPosition;
        Quaternion wantedRotation;
        if (Mode == ViewMode.Follow)
        {
            wantedPosition = new Vector3(r.position.x, Config.followCameraHeight, r.position.z);
            wantedRotation = Quaternion.Euler(90f, 0f, 0f);                       // straight down, north up
        }
        else
        {
            Quaternion yaw = Quaternion.Euler(0f, r.eulerAngles.y, 0f);           // only the heading, not any tilt
            wantedPosition = r.position - yaw * Vector3.forward * Config.chaseBackDistance;
            wantedPosition.y = Config.chaseHeight;
            wantedRotation = yaw * Quaternion.Euler(Config.chasePitchDeg, 0f, 0f);
        }

        if (snap)
        {
            cam.transform.SetPositionAndRotation(wantedPosition, wantedRotation);
            snap = false;
            return;
        }
        float k = 1f - Mathf.Exp(-Config.cameraSmoothing * Time.deltaTime);       // frame-rate independent smoothing
        cam.transform.SetPositionAndRotation(
            Vector3.Lerp(cam.transform.position, wantedPosition, k),
            Quaternion.Slerp(cam.transform.rotation, wantedRotation, k));
    }

    void ApplyLabel()
    {
        if (viewLabel != null) viewLabel.text = "View: " + Mode.ToString().ToUpper() + "   [C]";
    }
}
