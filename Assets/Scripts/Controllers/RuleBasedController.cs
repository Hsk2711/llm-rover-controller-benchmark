// RuleBasedController: the simple baseline, as simple as a student Arduino program with standard values (no tuning).
// Three rules, checked in this order (numbers are in RuleParams; positive turn = LEFT):
//   1. Front obstacle: front < 0.30 m -> stop and turn on the spot, at full turn rate, towards the side (left or right
//      sensor) with more space. Keep turning that way until front > 0.45 m (so it does not wobble back and forth).
//   2. Side obstacle: left or right < 0.20 m -> 50% speed and steer away from that side.
//   3. Otherwise: full speed, turn = clamp(goalAngle / 45 deg, -1, 1) to steer towards the goal.
// It uses only front, left, right and the goal angle. It remembers one thing: whether it is in the middle of a turn (rule 1).
using UnityEngine;

public class RuleBasedController : IRoverController
{
    RuleParams p;
    bool escaping;      // true while turning away from a front obstacle (rule 1)
    float escapeDir;    // +1 = turning left, -1 = turning right

    public string Name => "Rule-based";

    public void ResetController()
    {
        p = RuleParams.Load();      // re-read the asset at the start of every run
        escaping = false;
        escapeDir = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        if (p == null) p = RuleParams.Load();

        // Rule 1: front obstacle. Once started, the turn goes on until the front is clear again (hysteresis).
        if (escaping && obs.front > p.frontClearDistance) escaping = false;
        if (!escaping && obs.front < p.frontStopDistance)
        {
            escaping = true;
            escapeDir = obs.left >= obs.right ? 1f : -1f;      // more space on the left -> turn left (a tie turns left)
        }
        if (escaping) return (0f, escapeDir * p.escapeTurn);

        // Rule 2: side obstacle. A close LEFT obstacle -> steer right (negative); a close RIGHT obstacle -> steer left.
        bool leftClose = obs.left < p.sideDistance;
        bool rightClose = obs.right < p.sideDistance;
        if (leftClose || rightClose)
        {
            bool steerRight = leftClose && (!rightClose || obs.left <= obs.right);   // both close: avoid the nearer one
            return (p.sideSpeed, steerRight ? -p.sideTurn : p.sideTurn);
        }

        // Rule 3: nothing close, head for the goal.
        float turn = Mathf.Clamp(obs.goalAngleDeg / p.goalAngleForFullTurnDeg, -1f, 1f);
        return (p.cruiseSpeed, turn);
    }
}
