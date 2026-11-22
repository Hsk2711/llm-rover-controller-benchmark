using System;
using UnityEngine;

/// <summary>
/// Reactive navigation controller using a DistBug-style strategy:
///  - SEEK: drive toward the goal with local obstacle repulsion.
///  - FOLLOW: when the goal direction is blocked, follow the obstacle boundary
///    on a fixed side. Leave when the free space toward the goal guarantees
///    progress beyond the best goal distance seen so far. This escapes
///    U-shaped dead ends.
/// Sensor readings are de-glitched (false 4.0 dropouts), median-filtered and
/// scaled down slightly to stay conservative against noise.
/// </summary>
public class LLM_Claude_2_OS : IRoverController
{
    public string Name => "LLM_Controller_DistBug";

    // ---------------- constants ----------------
    const float DT = 0.05f;

    // Sensor indices
    const int S_FRONT = 0, S_LEFT = 1, S_RIGHT = 2, S_LSIDE = 3, S_RSIDE = 4;
    static readonly float[] SensorAngles = { 0f, 45f, -45f, 90f, -90f };

    // Safety distances (metres, measured from the sensor face)
    const float FRONT_STOP = 0.28f;       // turn in place below this
    const float FRONT_BLOCK_SEEK = 0.35f; // seek considers path blocked
    const float FRONT_SLOW = 0.75f;
    const float WALL_DIST = 0.20f;        // desired side clearance while following
    const float SENSOR_SAFETY = 0.92f;    // shrink readings to counter noise

    enum Mode { Seek, Follow }

    // ---------------- memory ----------------
    float[] lastValid = new float[5];
    int[] dropCount = new int[5];
    float[,] hist = new float[5, 3];
    bool histInit;

    Mode mode;
    int side;              // +1 = wall on left, -1 = wall on right
    int lastSide;
    float dMin;            // best (smallest) goal distance ever observed
    float time;
    float followTime;
    float lostTime;
    float lastLeaveTime;

    public LLM_Claude_2_OS()
    {
        ResetController();
    }

    public void ResetController()
    {
        for (int i = 0; i < 5; i++)
        {
            lastValid[i] = 4f;
            dropCount[i] = 0;
            for (int k = 0; k < 3; k++) hist[i, k] = 4f;
        }
        histInit = false;
        mode = Mode.Seek;
        side = 0;
        lastSide = 0;
        dMin = float.MaxValue;
        time = 0f;
        followTime = 0f;
        lostTime = 0f;
        lastLeaveTime = -100f;
    }

    // ---------------- sensor filtering ----------------
    float Filter(int i, float raw)
    {
        if (float.IsNaN(raw) || float.IsInfinity(raw)) raw = 4f;
        raw = Mathf.Clamp(raw, 0.02f, 4f);

        float v = raw;
        if (raw >= 3.95f)
        {
            // Possibly a false "nothing detected": hold the last real value briefly.
            if (lastValid[i] < 3.5f && dropCount[i] < 4)
            {
                dropCount[i]++;
                v = lastValid[i];
            }
            else
            {
                lastValid[i] = 4f;
            }
        }
        else
        {
            dropCount[i] = 0;
            lastValid[i] = raw;
        }

        if (!histInit)
        {
            hist[i, 0] = v; hist[i, 1] = v; hist[i, 2] = v;
        }
        else
        {
            hist[i, 0] = hist[i, 1];
            hist[i, 1] = hist[i, 2];
            hist[i, 2] = v;
        }

        float a = hist[i, 0], b = hist[i, 1], c = hist[i, 2];
        float med = Mathf.Max(Mathf.Min(a, b), Mathf.Min(Mathf.Max(a, b), c));
        // Never let filtering hide a sudden close obstacle: also respect the newest value
        // if it is much closer than the median (real approach, not noise).
        if (v < med * 0.7f) med = v;

        return med >= 3.95f ? 4f : med * SENSOR_SAFETY;
    }

    // Free distance in the goal direction, or -1 if no sensor looks that way.
    float FreeToward(float goalAngle, float[] s)
    {
        int best = -1;
        float bestDiff = 999f;
        for (int i = 0; i < 5; i++)
        {
            float d = Mathf.Abs(Mathf.DeltaAngle(SensorAngles[i], goalAngle));
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        if (bestDiff > 20f) return -1f;
        return s[best];
    }

    // ---------------- main decision ----------------
    public (float speed01, float turn) Decide(Observation obs)
    {
        time += DT;

        float[] s = new float[5];
        s[S_FRONT] = Filter(S_FRONT, obs.front);
        s[S_LEFT] = Filter(S_LEFT, obs.left);
        s[S_RIGHT] = Filter(S_RIGHT, obs.right);
        s[S_LSIDE] = Filter(S_LSIDE, obs.leftSide);
        s[S_RSIDE] = Filter(S_RSIDE, obs.rightSide);
        histInit = true;

        float F = s[S_FRONT], L = s[S_LEFT], R = s[S_RIGHT], LS = s[S_LSIDE], RS = s[S_RSIDE];
        float gd = obs.goalDistance;
        float ga = obs.goalAngleDeg;
        if (float.IsNaN(ga)) ga = 0f;
        if (float.IsNaN(gd)) gd = 10f;

        if (gd < dMin) dMin = gd;

        if (mode == Mode.Follow)
        {
            followTime += DT;

            // ---- DistBug leave condition ----
            if (followTime > 1.0f)
            {
                float free = FreeToward(ga, s);
                if (free >= 0f)
                {
                    bool goalVisible = free >= 3.9f ? true : (free >= gd - 0.1f);
                    bool progress = (gd - free) <= (dMin - 0.15f);
                    if (free > 0.55f && (goalVisible || progress))
                    {
                        mode = Mode.Seek;
                        lastSide = side;
                        lastLeaveTime = time;
                    }
                }
            }

            // ---- safety valve against endless loops: switch side ----
            if (mode == Mode.Follow && followTime > 25f)
            {
                side = -side;
                followTime = 0f;
                lostTime = 0f;
                dMin = gd;
            }

            if (mode == Mode.Follow)
                return FollowWall(F, L, R, LS, RS);
        }

        // ---------------- SEEK ----------------
        if (F < FRONT_BLOCK_SEEK)
        {
            if (Mathf.Abs(ga) <= 60f)
            {
                StartFollow(L, R, LS, RS, ga);
                return FollowWall(F, L, R, LS, RS);
            }
            // Goal is well off to the side: rotate in place toward it.
            return (0f, ga > 0f ? 0.8f : -0.8f);
        }

        float turn = Mathf.Clamp(ga / 40f, -1f, 1f);

        // Local repulsion
        if (L < 0.45f) turn -= (0.45f - L) * 3f;
        if (R < 0.45f) turn += (0.45f - R) * 3f;
        if (LS < 0.15f) turn -= (0.15f - LS) * 5f;
        if (RS < 0.15f) turn += (0.15f - RS) * 5f;
        if (F < FRONT_SLOW)
        {
            // Prefer the more open side when something is ahead.
            float bias = (L + 0.5f * LS) >= (R + 0.5f * RS) ? 1f : -1f;
            turn += bias * (FRONT_SLOW - F) * 0.8f;
        }
        turn = Mathf.Clamp(turn, -1f, 1f);

        float speed = Mathf.Clamp01((F - FRONT_STOP) / 0.6f);
        float absGa = Mathf.Abs(ga);
        if (absGa > 60f) speed = 0f;                 // turn in place first
        else speed *= Mathf.Clamp(1f - absGa / 90f, 0.3f, 1f);
        if (Mathf.Min(L, R) < 0.2f) speed *= 0.4f;
        speed *= Mathf.Clamp(gd / 0.5f, 0.4f, 1f);   // ease in near the goal

        return (Mathf.Clamp01(speed), turn);
    }

    void StartFollow(float L, float R, float LS, float RS, float ga)
    {
        mode = Mode.Follow;
        followTime = 0f;
        lostTime = 0f;

        // Re-hit shortly after leaving: keep the same side for consistency.
        if (lastSide != 0 && time - lastLeaveTime < 4f)
        {
            side = lastSide;
            return;
        }

        float scoreLeft = L + 0.5f * LS + (ga > 0f ? 0.5f : 0f);
        float scoreRight = R + 0.5f * RS + (ga < 0f ? 0.5f : 0f);
        // Turning left (more open on the left) puts the wall on the right.
        side = scoreLeft >= scoreRight ? -1 : 1;
    }

    (float, float) FollowWall(float F, float L, float R, float LS, float RS)
    {
        float sideR = side > 0 ? LS : RS;   // wall-side side sensor
        float diagR = side > 0 ? L : R;     // wall-side diagonal sensor
        float oSide = side > 0 ? RS : LS;   // opposite side
        float oDiag = side > 0 ? R : L;     // opposite diagonal

        // Blocked ahead or about to clip the wall: rotate away in place.
        if (F < FRONT_STOP || diagR < 0.17f)
        {
            lostTime = 0f;
            return (0f, -side * 0.85f);
        }

        // Wall lost (outer corner): go straight briefly to clear the corner, then arc around it.
        bool lost = sideR > 0.6f && diagR > 0.9f;
        if (lost)
        {
            lostTime += DT;
            float spd = Mathf.Clamp((F - FRONT_STOP) / 0.5f, 0f, 0.5f);
            if (lostTime < 0.5f) return (spd, 0f);
            float arcTurn = side * 0.75f;
            if (oSide < 0.15f || oDiag < 0.25f) arcTurn = side * 1f;
            return (Mathf.Min(spd, 0.45f), arcTurn);
        }
        lostTime = 0f;

        float dWall = Mathf.Min(sideR, diagR * 0.707f);
        float err = dWall - WALL_DIST;
        // Heading estimate: >0 means drifting away from the wall.
        float ang = sideR < 0.6f ? Mathf.Min(diagR, 1.2f) * 0.707f - sideR - 0.075f : 0f;

        float turn = side * (Mathf.Clamp(err * 2.5f, -0.7f, 0.7f) + Mathf.Clamp(ang * 1.5f, -0.4f, 0.4f));

        // Anticipate an inner corner ahead.
        if (F < FRONT_SLOW) turn += -side * (FRONT_SLOW - F) * 1.5f;

        // Narrow corridor: keep off the opposite wall.
        if (oSide < 0.18f) turn += side * (0.18f - oSide) * 4f;
        if (oDiag < 0.30f) turn += side * (0.30f - oDiag) * 3f;

        turn = Mathf.Clamp(turn, -1f, 1f);

        float speed = Mathf.Clamp((F - FRONT_STOP) / 0.5f, 0.15f, 0.8f);
        if (Mathf.Abs(turn) > 0.6f) speed *= 0.6f;
        if (Mathf.Min(diagR, oDiag) < 0.22f) speed *= 0.5f;

        return (speed, turn);
    }
}
