// Bug2Controller: Lumelsky & Stepanov's Bug2 for our three noisy ultrasonic sensors. It also knows where it is: it uses
// roverPos, startPos and goalPos from the Observation (state this in the paper). The M-line is the straight line from the
// start point to the goal. Numbers are in Bug2Params. Every sensor is smoothed with a median filter (sonar is noisy).
//   GO_TO_GOAL : steer towards the goal along the M-line. Front < 0.30 m -> remember the hit point (and its distance to
//                the goal) and switch to FOLLOW_WALL.
//   FOLLOW_WALL: keep the obstacle on the RIGHT at 0.25 m (proportional steering on the right sensor); turn left on the spot
//                if front < 0.30 m. Leave when back on the M-line (within 0.05 m) AND at least 0.10 m closer to the goal than
//                the hit point AND the front is clear. If it gets back to the hit point after travelling more than 1 m,
//                the goal is unreachable.
//   UNREACHABLE: stop (the run continues until the time limit). Positive turn = LEFT.
using UnityEngine;

public class Bug2Controller : IRoverController, IControllerGizmos
{
    public enum State { GoToGoal, FollowWall, Unreachable }

    Bug2Params p;
    State state;
    Median front = new Median(), left = new Median(), right = new Median();

    Vector2 hitPoint;           // where the current/last wall-following started
    float hitGoalDistance;      // distance from the hit point to the goal
    bool hasHit;
    float pathSinceHit;         // distance driven since the hit point
    Vector2 lastPos;
    Observation lastObs;
    bool hasObs;

    public string Name => "Bug2";
    public State CurrentState => state;
    public string StateName => state == State.GoToGoal ? "GO_TO_GOAL" : state == State.FollowWall ? "FOLLOW_WALL" : "UNREACHABLE";

    public void ResetController()
    {
        p = Bug2Params.Load();
        state = State.GoToGoal;
        front.Reset(p.medianWindow); left.Reset(p.medianWindow); right.Reset(p.medianWindow);
        hasHit = false;
        pathSinceHit = 0f;
        hasObs = false;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        if (p == null) ResetController();
        lastObs = obs;
        hasObs = true;

        // smooth every sensor (the left one is smoothed too, but Bug2 as specified does not use it)
        float f = front.Push(obs.front);
        left.Push(obs.left);
        float r = right.Push(obs.right);

        switch (state)
        {
            case State.GoToGoal:
                if (f < p.hitDistance)
                {
                    state = State.FollowWall;
                    hitPoint = obs.roverPos;
                    hitGoalDistance = obs.goalDistance;
                    hasHit = true;
                    pathSinceHit = 0f;
                    lastPos = obs.roverPos;
                    return FollowWall(f, r);
                }
                return GoToGoal(obs);

            case State.FollowWall:
                pathSinceHit += Vector2.Distance(obs.roverPos, lastPos);
                lastPos = obs.roverPos;

                // back at the hit point after a long trip around the obstacle: no way through, give up
                if (pathSinceHit > p.loopPathLength && Vector2.Distance(obs.roverPos, hitPoint) < p.hitReturnRadius)
                {
                    state = State.Unreachable;
                    return (0f, 0f);
                }

                // leave the wall: on the M-line, clearly closer to the goal than the hit point, and the front is clear
                if (DistanceToMLine(obs) <= p.mLineTolerance
                    && obs.goalDistance <= hitGoalDistance - p.progressMargin
                    && f > p.frontClearDistance)
                {
                    state = State.GoToGoal;
                    return GoToGoal(obs);
                }
                return FollowWall(f, r);

            default:
                return (0f, 0f);
        }
    }

    (float, float) GoToGoal(Observation obs)
    {
        // steering towards the goal from a point on the M-line keeps the rover on the M-line
        return (p.goSpeed, Mathf.Clamp(obs.goalAngleDeg / p.goalAngleForFullTurnDeg, -1f, 1f));
    }

    (float, float) FollowWall(float frontReading, float rightReading)
    {
        if (frontReading < p.hitDistance) return (0f, 1f);          // wall ahead: stop and turn left (wall stays on the right)
        // wall too far on the right (reading above 0.25) -> steer right (negative); too close -> steer left
        float turn = Mathf.Clamp(p.wallGain * (p.wallDistance - rightReading), -1f, 1f);
        return (p.wallSpeed, turn);
    }

    // distance from the rover to the M-line segment start -> goal
    static float DistanceToMLine(Observation obs)
    {
        Vector2 a = obs.startPos, b = obs.goalPos, c = obs.roverPos;
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(c - a, ab) / len2);
        return Vector2.Distance(c, a + ab * t);
    }

    // Scene view: M-line (magenta), hit point (red while following, grey after), state label and coloured marker on the rover.
    public void DrawGizmos()
    {
        if (!hasObs) return;
        const float y = 0.02f;
        Vector3 start = new Vector3(lastObs.startPos.x, y, lastObs.startPos.y);
        Vector3 goal = new Vector3(lastObs.goalPos.x, y, lastObs.goalPos.y);
        Gizmos.color = Color.magenta;
        Gizmos.DrawLine(start, goal);

        if (hasHit)
        {
            Gizmos.color = state == State.FollowWall ? Color.red : new Color(0.6f, 0.6f, 0.6f);
            Gizmos.DrawSphere(new Vector3(hitPoint.x, y, hitPoint.y), 0.05f);
        }

        Color c = state == State.GoToGoal ? Color.green : state == State.FollowWall ? Color.yellow : Color.red;
        Vector3 rover = new Vector3(lastObs.roverPos.x, 0.25f, lastObs.roverPos.y);
        Gizmos.color = c;
        Gizmos.DrawSphere(rover, 0.04f);
#if UNITY_EDITOR
        string text = "Bug2: " + StateName;
        if (hasHit) text += "\nhit point " + hitPoint.ToString("0.00") + ", " + hitGoalDistance.ToString("0.00") + " m from goal";
        UnityEditor.Handles.Label(rover + Vector3.up * 0.1f, text);
#endif
    }

    // Median of the last `window` readings. The very first reading fills the whole window (so the start is not biased).
    class Median
    {
        float[] buf = new float[3];
        float[] tmp = new float[3];
        int next;
        bool filled;

        public void Reset(int window)
        {
            int w = Mathf.Max(1, window | 1);                  // odd size
            if (buf.Length != w) { buf = new float[w]; tmp = new float[w]; }
            filled = false;
            next = 0;
        }

        public float Push(float x)
        {
            if (!filled) { for (int i = 0; i < buf.Length; i++) buf[i] = x; filled = true; }
            else { buf[next] = x; next = (next + 1) % buf.Length; }
            System.Array.Copy(buf, tmp, buf.Length);
            System.Array.Sort(tmp);
            return tmp[tmp.Length / 2];
        }
    }
}
