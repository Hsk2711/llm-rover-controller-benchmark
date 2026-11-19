// ManualController: manual testing only (replaces the old KeyboardController component). W / Up Arrow = drive forward,
// A / Left Arrow = turn left, D / Right Arrow = turn right. Releasing W stops the rover (no reversing). It ignores the
// Observation: it is a human at the keyboard, not a study subject.
using UnityEngine.InputSystem;

public class ManualController : IRoverController
{
    public string Name => "Manual (keyboard)";

    public void ResetController() { }

    public (float speed01, float turn) Decide(Observation obs)
    {
        var kb = Keyboard.current;
        if (kb == null) return (0f, 0f);

        float speed01 = (kb.wKey.isPressed || kb.upArrowKey.isPressed) ? 1f : 0f;
        float turn = 0f;                                              // positive = LEFT
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) turn += 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) turn -= 1f;
        return (speed01, turn);
    }
}
