// KeyboardController: manual testing only. W / Up Arrow = drive forward, A / Left Arrow = turn left,
// D / Right Arrow = turn right. Releasing W stops the rover (S / Down do nothing: no reversing).
using UnityEngine;
using UnityEngine.InputSystem;

public class KeyboardController : MonoBehaviour
{
    public RoverDrive drive;

    void Awake()
    {
        if (drive == null) drive = GetComponent<RoverDrive>();
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null || drive == null) return;

        float speed01 = (kb.wKey.isPressed || kb.upArrowKey.isPressed) ? 1f : 0f;

        float turn = 0f;                                              // positive = LEFT
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) turn += 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) turn -= 1f;

        drive.SetCommand(speed01, turn);
    }
}
