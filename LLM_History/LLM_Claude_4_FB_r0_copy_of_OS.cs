using System;
using UnityEngine;

public class LLM_Claude_4_FB : IRoverController
{
    public string Name => "LLM_Controller (DistBug wall follower)";

    // ---------------- Tunables ----------------
    const int   FilterWindow     = 3;      // samples for sliding-min filter
    const float HalfWidth        = 0.075f; // rover half width (m)
    const float Sin45            = 0.7071f;
    const float FollowClearance  = 0.20f;  // desired side clearance while wall-following
    const float FrontStopFollow  = 0.32f;  // turn in place when front closer than this (following)
    const float FrontClearFollow = 0.50f;  // stop turning in place when front exceeds this
    const float FrontTrigSeek    = 0.38f;  // switch to wall-follow when front closer than this
    const float DiagTrigSeek     = 0.06f;  // switch to wall-follow when diagonal clearance below this
    const float LeaveMargin      = 0.12f;  // must be this much closer to goal than at hit point
    const int   NoProgressLimit  = 300;    // ticks (15 s) without progress -> reverse follow direction
    const int   LostLimit        = 80;     // ticks (4 s) with wall lost -> go back to goal seeking
    const int   UTurnTicks       = 40;     // 2 s at 90 deg/s = 180 deg

    // States
    const int SEEK = 0, FOLLOW = 1, UTURN = 2;

    // ---------------- Memory ----------------
    readonly float[,] hist = new float[5, FilterWindow];
    readonly float[] raw = new float[5];
    bool histInit;
    int histIdx;

    float F, L, R, LS, RS; // filtered: front, left45, right45, leftSide, rightSide

    int state;
    int side;              // +1 = wall on left, -1 = wall on right
    float dHit;            // goal distance when obstacle was hit
    float bestDist;        // best goal distance during current follow
    int followTicks;
    int noProgTicks;
    int lostTicks;
    int uturnCount;
    bool turningAway;

    public LLM_Claude_4_FB()
    {
        ResetController();
    }

    public void ResetController()
    {
        Array.Clear(hist, 0, hist.Length);
        histInit = false;
        histIdx = 0;
        F = L = R = LS = RS = 4f;
        state = SEEK;
        side = -1;
        dHit = float.MaxValue;
        bestDist = float.MaxValue;
        followTicks = 0;
        noProgTicks = 0;
        lostTicks = 0;
        uturnCount = 0;
        turningAway = false;
    }

    // ---------------- Main decision ----------------
    public (float speed01, float turn) Decide(Observation obs)
    {
        FilterSensors(obs);

        float gd = Sanitize(obs.goalDistance, 100f);
        float ga = Sanitize(obs.goalAngleDeg, 0f);

        if (gd < 0.03f) return (0f, 0f); // at goal centre

        float speed, turn;

        switch (state)
        {
            case UTURN:
                uturnCount++;
                // Rotate in place away from the old wall (== toward new side sign).
                if (uturnCount >= UTurnTicks || F < 0.05f && false)
                {
                    state = FOLLOW;
                    followTicks = 0;
                    noProgTicks = 0;
                    lostTicks = 0;
                    turningAway = false;
                    bestDist = gd;
                }
                return (0f, side * 1f);

            case FOLLOW:
                followTicks++;

                // Progress bookkeeping
                if (gd < bestDist - 0.05f) { bestDist = gd; noProgTicks = 0; }
                else noProgTicks++;

                // Leave condition (DistBug): closer than hit point and goal direction free
                if (followTicks > 10 && !turningAway &&
                    gd < dHit - LeaveMargin && GoalDirectionFree(gd, ga))
                {
                    state = SEEK;
                    return Seek(gd, ga);
                }

                // Lost the wall for too long -> resume seeking
                if (lostTicks > LostLimit)
                {
                    state = SEEK;
                    lostTicks = 0;
                    return Seek(gd, ga);
                }

                // No progress for a long time -> reverse direction along the boundary
                if (noProgTicks > NoProgressLimit)
                {
                    side = -side;
                    state = UTURN;
                    uturnCount = 0;
                    noProgTicks = 0;
                    return (0f, side * 1f);
                }

                (speed, turn) = Follow();
                return (Clamp01(speed), Mathf.Clamp(turn, -1f, 1f));

            default: // SEEK
                // Trigger wall following when blocked
                float cL = L * Sin45 - HalfWidth;
                float cR = R * Sin45 - HalfWidth;
                bool goalBeforeObstacle = gd < F - 0.05f && Mathf.Abs(ga) < 15f;
                bool frontBlocked = F < FrontTrigSeek && !goalBeforeObstacle;
                bool diagBlocked = (cL < DiagTrigSeek || cR < DiagTrigSeek) && !(gd < 0.15f);

                if ((frontBlocked || diagBlocked) && Mathf.Abs(ga) < 70f)
                {
                    StartFollow(gd, ga);
                    (speed, turn) = Follow();
                    return (Clamp01(speed), Mathf.Clamp(turn, -1f, 1f));
                }

                return Seek(gd, ga);
        }
    }

    // ---------------- Behaviours ----------------
    (float, float) Seek(float gd, float ga)
    {
        float absGa = Mathf.Abs(ga);

        // Large heading error: rotate in place toward the goal
        if (absGa > 60f)
        {
            float t = Mathf.Sign(ga) * Mathf.Clamp(absGa / 60f, 0.5f, 1f);
            return (0f, t);
        }

        float turn = Mathf.Clamp(ga / 35f, -1f, 1f);

        // Obstacle repulsion (keeps rover centred in corridors / off edges)
        float cL = L * Sin45 - HalfWidth;
        float cR = R * Sin45 - HalfWidth;
        float rep = 0f;
        if (cL < 0.20f) rep -= (0.20f - cL) * 4f;
        if (cR < 0.20f) rep += (0.20f - cR) * 4f;
        if (LS < 0.12f) rep -= (0.12f - LS) * 5f;
        if (RS < 0.12f) rep += (0.12f - RS) * 5f;
        if (gd > 0.2f) turn = Mathf.Clamp(turn + rep, -1f, 1f);

        float frontLimit = (gd < F - 0.05f) ? 1f : Mathf.Clamp((F - 0.25f) / 0.7f, 0.25f, 1f);
        float speed = frontLimit * (1f - (absGa / 60f) * 0.6f);
        if (gd < 0.3f) speed = Mathf.Min(speed, 0.3f + gd);
        if (F < 0.18f && gd > F) speed = 0f;

        return (Clamp01(speed), turn);
    }

    void StartFollow(float gd, float ga)
    {
        // Pick the turning direction: more open side, biased toward goal side
        float openL = Mathf.Min(L, 2f) + 0.5f * Mathf.Min(LS, 2f);
        float openR = Mathf.Min(R, 2f) + 0.5f * Mathf.Min(RS, 2f);
        float score = (openL - openR) + 0.5f * Mathf.Clamp(ga / 45f, -1f, 1f);

        // Turning left means the wall ends up on the right (side = -1)
        side = score >= 0f ? -1 : +1;

        state = FOLLOW;
        dHit = gd;
        bestDist = gd;
        followTicks = 0;
        noProgTicks = 0;
        lostTicks = 0;
        turningAway = true;
    }

    (float, float) Follow()
    {
        int s = side;
        float sd  = s > 0 ? LS : RS;   // wall-side side sensor
        float dg  = s > 0 ? L  : R;    // wall-side diagonal sensor
        float osd = s > 0 ? RS : LS;   // opposite side sensor
        float odg = s > 0 ? R  : L;    // opposite diagonal

        float cSide  = sd;
        float cDiag  = dg  * Sin45 - HalfWidth;
        float ocDiag = odg * Sin45 - HalfWidth;

        // Adapt clearance target in narrow corridors
        float D = FollowClearance;
        if (sd < 0.45f && osd < 0.45f) D = Mathf.Min(D, 0.5f * (sd + osd));

        // Concave corner / blocked front: rotate in place away from wall (with hysteresis)
        if (F < FrontStopFollow || cDiag < 0.05f) turningAway = true;
        if (turningAway && F > FrontClearFollow && cDiag > D * 0.8f) turningAway = false;
        if (turningAway)
        {
            lostTicks = 0;
            return (0f, -s * 1f);
        }

        // Wall lost: convex corner -> arc around it toward the wall side
        bool lost = sd > 1.0f && dg > 1.5f;
        if (lost)
        {
            lostTicks++;
            float sp = F < 0.6f ? 0.25f : 0.45f;
            return (sp, s * 0.75f);
        }
        lostTicks = 0;

        // Normal wall following: distance error + heading term
        float wallC = Mathf.Min(cSide, cDiag);
        float err = D - wallC;                                   // + : too close
        float angleTerm = Mathf.Clamp(cSide - cDiag, -0.3f, 0.3f); // + : heading into wall
        float u = 4f * err + 3f * angleTerm;

        // Opposite-side safety
        if (ocDiag < 0.08f) u -= (0.08f - ocDiag) * 6f;
        if (osd < 0.08f)    u -= (0.08f - osd) * 6f;

        float turn = -s * Mathf.Clamp(u, -1f, 1f);

        float speed = Mathf.Clamp((F - 0.3f) / 0.6f, 0.2f, 1f) * (1f - 0.5f * Mathf.Abs(turn));
        if (wallC < 0.10f) speed *= 0.5f;
        if (F < 0.2f) speed = 0f;

        return (speed, turn);
    }

    // Is the direction toward the goal free according to the closest sensor?
    bool GoalDirectionFree(float gd, float ga)
    {
        float[] angles = { 0f, 45f, -45f, 90f, -90f };
        float[] vals = { F, L, R, LS, RS };
        int best = -1;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            float d = Mathf.Abs(Mathf.DeltaAngle(ga, angles[i]));
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        if (best < 0 || bestDiff > 30f) return false;

        float need = Mathf.Min(gd + 0.1f, 0.9f);
        if (vals[best] < need) return false;

        // When the goal is well to one side we will rotate toward it: make sure that
        // side's diagonal is also reasonably clear.
        if (ga > 60f && L < 0.5f) return false;
        if (ga < -60f && R < 0.5f) return false;
        return true;
    }

    // ---------------- Helpers ----------------
    void FilterSensors(Observation obs)
    {
        raw[0] = Sanitize(obs.front, 4f);
        raw[1] = Sanitize(obs.left, 4f);
        raw[2] = Sanitize(obs.right, 4f);
        raw[3] = Sanitize(obs.leftSide, 4f);
        raw[4] = Sanitize(obs.rightSide, 4f);

        for (int i = 0; i < 5; i++)
        {
            float v = Mathf.Clamp(raw[i], 0.02f, 4f);
            if (!histInit)
            {
                for (int k = 0; k < FilterWindow; k++) hist[i, k] = v;
            }
            else
            {
                hist[i, histIdx] = v;
            }
        }
        histInit = true;
        histIdx = (histIdx + 1) % FilterWindow;

        F  = MinOf(0);
        L  = MinOf(1);
        R  = MinOf(2);
        LS = MinOf(3);
        RS = MinOf(4);
    }

    float MinOf(int i)
    {
        float m = hist[i, 0];
        for (int k = 1; k < FilterWindow; k++)
            if (hist[i, k] < m) m = hist[i, k];
        return m;
    }

    static float Sanitize(float v, float fallback)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
        return v;
    }

    static float Clamp01(float v) => Mathf.Clamp(v, 0f, 1f);
}
