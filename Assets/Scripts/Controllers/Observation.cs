// Observation: everything a controller is allowed to know at one decision (every 0.05 s). Every controller gets exactly
// these inputs. front/left/right are the noisy ultrasonic readings (m). goalAngleDeg is the signed angle from the rover's
// forward direction to the goal, -180..180, positive = the goal is on the LEFT. roverPos, startPos and goalPos (x, z in
// metres) are used ONLY by Bug2, which is the one controller that also knows where it is (state this in the paper).
using UnityEngine;

public struct Observation
{
    public float front, left, right, goalDistance, goalAngleDeg;
    public Vector2 roverPos, startPos, goalPos;
}
