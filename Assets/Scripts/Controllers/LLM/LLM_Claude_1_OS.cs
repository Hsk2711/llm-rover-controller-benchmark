using System;
using UnityEngine;

public class LLM_Claude_1_OS : IRoverController
{
    // ---------------- constants ----------------
    private const float DT = 0.05f;
    private const float VMAX = 0.3f;     // m/s
    private const float WMAX = 90f;      // deg/s
    private const int HIST = 3;

    // follow parameters (distances from the sensor face)
    private const float DESIRED_WALL = 0.20f;
    private const float FRONT_STOP = 0.30f;
    private const float FRONT_SLOW = 0.50f;
    private const float LOST_WALL = 0.60f;

    private enum Mode { Seek, Follow }

    public string Name => "LLM_Controller";

    // ---------------- state ----------------
    private Mode mode;
    private int dir;                 // +1: turning left around obstacle (wall on right), -1: wall on left
    private float x, y, th;          // dead-reckoned pose (m, m, rad)
    private float lastV, lastWdeg;   // last commanded velocities
    private float hitX, hitY, pathSinceHit, dMin, followTime;
    private bool hasLastHit;
    private float lastHitGd;
    private int lastDir;

    private float[,] hist = new float[5, HIST];
    private int histCount, histIdx;

    // filtered sensor values
    private float F, L, R, LS, RS;

    public LLM_Claude_1_OS()
    {
        ResetController();
    }

    public void ResetController()
    {
        mode = Mode.Seek;
        dir = 1;
        x = 0f; y = 0f; th = 0f;
        lastV = 0f; lastWdeg = 0f;
        hitX = 0f; hitY = 0f; pathSinceHit = 0f; dMin = float.MaxValue; followTime = 0f;
        hasLastHit = false;
        lastHitGd = float.MaxValue;
        lastDir = 1;
        hist = new float[5, HIST];
        histCount = 0; histIdx = 0;
        F = L = R = LS = RS = 4f;
    }

    // ---------------- helpers ----------------
    private static float San(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return 4f;
        return Mathf.Clamp(v, 0.02f, 4f);
    }

    private void UpdateFilter(Observation obs)
    {
        hist[0, histIdx] = San(obs.front);
        hist[1, histIdx] = San(obs.left);
        hist[2, histIdx] = San(obs.right);
        hist[3, histIdx] = San(obs.leftSide);
        hist[4, histIdx] = San(obs.rightSide);
        histIdx = (histIdx + 1) % HIST;
        if (histCount < HIST) histCount++;

        float[] m = new float[5];
        for (int s = 0; s < 5; s++)
        {
            float mn = 4f;
            for (int k = 0; k < histCount; k++) mn = Mathf.Min(mn, hist[s, k]);
            m[s] = mn;
        }
        F = m[0]; L = m[1]; R = m[2]; LS = m[3]; RS = m[4];
    }

    private void UpdateOdometry()
    {
        th += lastWdeg * Mathf.Deg2Rad * DT;
        x += lastV * Mathf.Cos(th) * DT;
        y += lastV * Mathf.Sin(th) * DT;
        if (mode == Mode.Follow)
        {
            pathSinceHit += lastV * DT;
            followTime += DT;
        }
    }

    // Free space measured in the goal direction (0 if no sensor covers it)
    private float FreeTowardGoal(float ga)
    {
        float a = Mathf.Abs(ga);
        if (a <= 18f) return F;
        if (a <= 65f) return ga > 0 ? L : R;
        if (a <= 105f) return ga > 0 ? LS : RS;
        return 0f;
    }

    // ---------------- main ----------------
    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateOdometry();
        UpdateFilter(obs);

        float gd = float.IsNaN(obs.goalDistance) ? 10f : obs.goalDistance;
        float ga = float.IsNaN(obs.goalAngleDeg) ? 0f : obs.goalAngleDeg;

        float v, t;
        if (mode == Mode.Seek)
        {
            SeekStep(gd, ga, out v, out t);
        }
        else
        {
            if (gd < dMin) dMin = gd;

            // ----- leave condition (DistBug) -----
            bool left = false;
            if (followTime > 0.6f && pathSinceHit > 0.2f)
            {
                float free = FreeTowardGoal(ga);
                if (free > 0.45f && (free >= gd - 0.05f || gd - free <= dMin - 0.10f))
                {
                    mode = Mode.Seek;
                    left = true;
                }
            }

            if (left)
            {
                SeekStep(gd, ga, out v, out t);
            }
            else
            {
                // ----- loop / timeout detection -> flip direction -----
                float dx = x - hitX, dy = y - hitY;
                bool loop = pathSinceHit > 1.2f && (dx * dx + dy * dy) < 0.30f * 0.30f;
                if (loop || followTime > 28f)
                {
                    dir = -dir;
                    lastDir = dir;
                    hitX = x; hitY = y;
                    pathSinceHit = 0f;
                    followTime = 0f;
                    dMin = gd;
                }
                FollowStep(out v, out t);
            }
        }

        // ----- hard safety override -----
        if (v > 0f && (F < 0.12f || L < 0.10f || R < 0.10f))
        {
            v = 0f;
            if (Mathf.Abs(t) < 0.3f) t = (L + LS >= R + RS) ? 1f : -1f;
        }

        v = Mathf.Clamp01(v);
        t = Mathf.Clamp(t, -1f, 1f);

        lastV = v * VMAX;
        lastWdeg = t * WMAX;
        return (v, t);
    }

    // ---------------- seek mode ----------------
    private void SeekStep(float gd, float ga, out float v, out float t)
    {
        float aga = Mathf.Abs(ga);

        // Large heading error: rotate in place (safe for a small rover)
        if (aga > 50f)
        {
            v = 0f;
            t = ga > 0 ? 1f : -1f;
            return;
        }

        // Is the way to the goal blocked?
        bool blocked =
            (aga < 30f && F < 0.35f && gd > F + 0.15f) ||
            (ga >= 15f && ga < 70f && L < 0.25f && gd > L + 0.15f) ||
            (ga <= -15f && ga > -70f && R < 0.25f && gd > R + 0.15f) ||
            (F < 0.18f && gd > F + 0.1f);

        if (blocked)
        {
            StartFollow(gd, ga);
            FollowStep(out v, out t);
            return;
        }

        t = Mathf.Clamp(ga / 35f, -1f, 1f);

        // repulsion from nearby obstacles
        if (L < 0.40f) t -= (0.40f - L) * 2.5f;
        if (R < 0.40f) t += (0.40f - R) * 2.5f;
        if (LS < 0.12f) t -= 0.3f;
        if (RS < 0.12f) t += 0.3f;
        t = Mathf.Clamp(t, -1f, 1f);

        float clear = Mathf.Min(F, Mathf.Min(L, R) * 1.2f);
        v = Mathf.Clamp01((clear - 0.12f) / 0.6f);
        v *= (1f - 0.5f * Mathf.Abs(t));
        if (clear > 0.30f) v = Mathf.Max(v, 0.2f);
        if (gd < 0.3f) v = Mathf.Min(v, 0.6f);
    }

    private void StartFollow(float gd, float ga)
    {
        mode = Mode.Follow;
        if (hasLastHit && gd > lastHitGd - 0.3f)
        {
            dir = lastDir;   // same obstacle: keep direction
        }
        else
        {
            float score = (L + 0.5f * LS) - (R + 0.5f * RS) + ga / 60f;
            dir = score >= 0f ? 1 : -1;
        }
        hitX = x; hitY = y;
        pathSinceHit = 0f;
        followTime = 0f;
        dMin = gd;
        hasLastHit = true;
        lastHitGd = gd;
        lastDir = dir;
    }

    // ---------------- wall following ----------------
    private void FollowStep(out float v, out float t)
    {
        float a = dir; // "away from wall" turn sign

        float sd  = dir > 0 ? RS : LS;   // wall-side side sensor
        float dg  = dir > 0 ? R  : L;    // wall-side diagonal sensor
        float sd2 = dir > 0 ? LS : RS;   // opposite side
        float dg2 = dir > 0 ? L  : R;    // opposite diagonal

        // perpendicular distance from side face estimated from diagonal sensor
        float diagEq = dg * 0.7071f - 0.075f;
        float wallDist = Mathf.Min(sd, diagEq);

        // 1) Inner corner / obstacle ahead: rotate away in place
        if (F < FRONT_STOP)
        {
            v = 0f;
            t = a;
            return;
        }

        // 2) Something ahead but not critical: slow and turn away
        if (F < FRONT_SLOW)
        {
            v = 0.25f;
            t = a * 0.8f;
            if (dg2 < 0.20f) { v = 0f; t = a; }
            return;
        }

        // 3) Lost the wall: arc around the convex corner toward the wall side
        if (wallDist > LOST_WALL)
        {
            v = 0.45f;
            t = -a * 0.7f;
            return;
        }

        // 4) Regular wall following
        float away = 4f * (DESIRED_WALL - wallDist)      // distance error
                   + 2f * (sd - diagEq);                 // wall converging ahead -> turn away

        // narrow corridor: centre between the two side walls
        float opp = Mathf.Min(sd2, dg2 * 0.7071f - 0.075f);
        if (opp < 0.35f && wallDist < 0.35f)
            away = 3f * (opp - wallDist);

        // keep clear of the opposite diagonal
        if (dg2 < 0.25f) away -= (0.25f - dg2) * 3f;

        t = Mathf.Clamp(a * away, -1f, 1f);

        v = 0.4f + 0.6f * Mathf.Clamp01((F - FRONT_SLOW) / 0.6f);
        v *= (1f - 0.5f * Mathf.Abs(t));
        if (wallDist < 0.08f) v = Mathf.Min(v, 0.2f);
    }
}
