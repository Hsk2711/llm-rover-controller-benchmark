using System;
using UnityEngine;

public class LLM_Claude_3_OS : IRoverController
{
    public string Name => "LLM_Controller";

    // ---------------- constants ----------------
    const float DT = 0.05f;
    const int FILT = 5;                 // median filter length

    const float EMERG_FRONT = 0.12f;    // stop & rotate thresholds
    const float EMERG_DIAG = 0.08f;
    const float EMERG_SIDE = 0.035f;
    const float ESCAPE_CLEAR_FRONT = 0.30f;
    const float ESCAPE_CLEAR_DIAG = 0.18f;

    const float BLOCK_FRONT = 0.45f;    // obstacle ahead -> wall-follow
    const float WALL_TARGET = 0.22f;    // desired lateral distance to wall
    const float WALL_LOST = 0.70f;      // wall considered lost beyond this
    const float LEAVE_STEP = 0.50f;     // DistBug progress margin
    const float FOLLOW_TIMEOUT = 22f;   // switch follow side after this
    const float GOAL_STOP = 0.06f;

    // sensor indices
    const int S_F = 0, S_L = 1, S_R = 2, S_LS = 3, S_RS = 4;

    // ---------------- state ----------------
    float[,] buf = new float[5, FILT];
    int bufIdx;
    int samples;
    float[] tmp = new float[FILT];

    enum Mode { Seek, Follow }
    Mode mode;
    int followSide;          // +1 = wall on left, -1 = wall on right
    float hitDist;
    float followTime;
    int escapeDir;           // 0 = not escaping, +1 left, -1 right
    float prevWall;
    bool havePrevWall;
    int sideSwitches;

    public LLM_Claude_3_OS()
    {
        ResetController();
    }

    public void ResetController()
    {
        buf = new float[5, FILT];
        tmp = new float[FILT];
        bufIdx = 0;
        samples = 0;
        mode = Mode.Seek;
        followSide = -1;
        hitDist = float.MaxValue;
        followTime = 0f;
        escapeDir = 0;
        prevWall = 0f;
        havePrevWall = false;
        sideSwitches = 0;
    }

    // ---------------- filtering ----------------
    void Push(Observation o)
    {
        float[] v = { o.front, o.left, o.right, o.leftSide, o.rightSide };
        if (samples == 0)
        {
            for (int s = 0; s < 5; s++)
                for (int k = 0; k < FILT; k++) buf[s, k] = Sanitize(v[s]);
        }
        for (int s = 0; s < 5; s++) buf[s, bufIdx] = Sanitize(v[s]);
        bufIdx = (bufIdx + 1) % FILT;
        if (samples < FILT) samples++;
    }

    static float Sanitize(float x)
    {
        if (float.IsNaN(x) || float.IsInfinity(x)) return 4f;
        return Mathf.Clamp(x, 0.02f, 4f);
    }

    float Med(int s)
    {
        for (int k = 0; k < FILT; k++) tmp[k] = buf[s, k];
        Array.Sort(tmp);
        return tmp[FILT / 2];
    }

    // ---------------- main ----------------
    public (float speed01, float turn) Decide(Observation obs)
    {
        Push(obs);
        float f = Med(S_F), l = Med(S_L), r = Med(S_R), ls = Med(S_LS), rs = Med(S_RS);
        float gd = obs.goalDistance;
        float ga = obs.goalAngleDeg;

        if (gd < GOAL_STOP) return (0f, 0f);

        // ---------- emergency: stop and rotate in place ----------
        bool emergency = f < EMERG_FRONT || l < EMERG_DIAG || r < EMERG_DIAG;
        if (emergency && escapeDir == 0)
        {
            if (mode == Mode.Follow)
                escapeDir = -followSide;                 // rotate away from followed wall
            else
                escapeDir = (l + 0.5f * ls >= r + 0.5f * rs) ? 1 : -1;

            if (mode == Mode.Seek) EnterFollow(-escapeDir, gd);
        }
        if (escapeDir != 0)
        {
            if (f > ESCAPE_CLEAR_FRONT && l > ESCAPE_CLEAR_DIAG && r > ESCAPE_CLEAR_DIAG)
                escapeDir = 0;
            else
            {
                if (mode == Mode.Follow) followTime += DT;
                return (0f, 0.8f * escapeDir);
            }
        }

        if (mode == Mode.Follow)
        {
            followTime += DT;

            if (CanLeave(f, l, r, ls, rs, gd, ga))
            {
                mode = Mode.Seek;
                havePrevWall = false;
            }
            else
            {
                if (followTime > FOLLOW_TIMEOUT)
                {
                    // probably circling; try the other way round
                    followSide = -followSide;
                    followTime = 0f;
                    hitDist = gd;
                    havePrevWall = false;
                    sideSwitches++;
                }
                return FollowWall(f, l, r, ls, rs, gd);
            }
        }

        return Seek(f, l, r, ls, rs, gd, ga);
    }

    // ---------------- goal seeking ----------------
    (float, float) Seek(float f, float l, float r, float ls, float rs, float gd, float ga)
    {
        // blocked ahead -> start following the obstacle
        if (f < BLOCK_FRONT && Mathf.Abs(ga) < 80f)
        {
            int dir;   // +1 = go around by turning left
            bool goalLeft = ga >= 0f;
            float leftSpace = l + 0.5f * ls;
            float rightSpace = r + 0.5f * rs;
            if (goalLeft) dir = (leftSpace < 0.35f && rightSpace > leftSpace + 0.3f) ? -1 : 1;
            else dir = (rightSpace < 0.35f && leftSpace > rightSpace + 0.3f) ? 1 : -1;

            EnterFollow(-dir, gd);
            return FollowWall(f, l, r, ls, rs, gd);
        }

        // goal far behind: rotate (almost) in place
        if (Mathf.Abs(ga) > 70f)
        {
            float sp = (f > 0.6f && l > 0.3f && r > 0.3f) ? 0.1f : 0f;
            return (sp, Mathf.Sign(ga) * 0.9f);
        }

        float turn = Mathf.Clamp(ga / 40f, -1f, 1f);

        // repulsion from close obstacles (keeps centered in corridors)
        float rep = 0f;
        if (l < 0.5f) rep -= (0.5f - l) * 2.0f;
        if (r < 0.5f) rep += (0.5f - r) * 2.0f;
        if (ls < 0.2f) rep -= (0.2f - ls) * 2.5f;
        if (rs < 0.2f) rep += (0.2f - rs) * 2.5f;
        turn = Mathf.Clamp(turn + rep, -1f, 1f);

        float minFwd = Mathf.Min(f, Mathf.Min(l, r) * 1.2f);
        float speed = Mathf.Clamp((minFwd - EMERG_FRONT) / 0.6f, 0.15f, 1f);
        speed *= 1f - 0.6f * Mathf.Abs(turn);
        speed = Mathf.Min(speed, 0.25f + gd / 0.4f);
        return (Mathf.Clamp01(speed), turn);
    }

    void EnterFollow(int side, float gd)
    {
        if (mode != Mode.Follow)
        {
            mode = Mode.Follow;
            followSide = side;
            hitDist = gd;
            followTime = 0f;
            havePrevWall = false;
        }
    }

    // ---------------- wall following ----------------
    (float, float) FollowWall(float f, float l, float r, float ls, float rs, float gd)
    {
        int s = followSide;
        float wallSide = s > 0 ? ls : rs;
        float wallDiag = s > 0 ? l : r;
        float otherSide = s > 0 ? rs : ls;
        float otherDiag = s > 0 ? r : l;

        // perpendicular-ish distance estimate to the followed wall
        float dWall = Mathf.Min(wallSide, wallDiag * 0.72f);

        // narrow corridor: aim for the centre instead of a fixed offset
        float target = WALL_TARGET;
        if (otherSide < 0.5f) target = Mathf.Min(WALL_TARGET, 0.5f * (wallSide + otherSide));

        float turn, speed;

        if (f < BLOCK_FRONT)
        {
            // inner corner: turn away from the wall, creep forward only if room
            turn = -s * 1.0f;
            speed = Mathf.Clamp((f - EMERG_FRONT - 0.08f) / 0.6f, 0f, 0.25f);
            havePrevWall = false;
        }
        else if (dWall > WALL_LOST)
        {
            // outer corner / wall lost: arc around towards the wall side
            turn = s * 0.7f;
            speed = 0.45f;
            havePrevWall = false;
        }
        else
        {
            float err = dWall - target;
            float deriv = 0f;
            if (havePrevWall) deriv = Mathf.Clamp((dWall - prevWall) / DT, -0.5f, 0.5f);
            prevWall = dWall;
            havePrevWall = true;

            turn = s * (3.0f * err + 1.2f * deriv);

            // push away from things on the opposite side as well
            if (otherDiag < 0.25f) turn += s * (0.25f - otherDiag) * 3f;
            if (otherSide < 0.12f) turn += s * (0.12f - otherSide) * 4f;

            turn = Mathf.Clamp(turn, -0.85f, 0.85f);
            speed = Mathf.Clamp((f - EMERG_FRONT) / 0.5f, 0.2f, 0.9f);
            speed *= 1f - 0.5f * Mathf.Abs(turn);
        }

        // never drive fast with a diagonal very close
        float minDiag = Mathf.Min(l, r);
        if (minDiag < 0.2f) speed = Mathf.Min(speed, 0.25f);

        return (Mathf.Clamp01(speed), Mathf.Clamp(turn, -1f, 1f));
    }

    // ---------------- DistBug leave condition ----------------
    bool CanLeave(float f, float l, float r, float ls, float rs, float gd, float ga)
    {
        if (followTime < 1.0f) return false;
        float a = Mathf.Abs(ga);
        if (a > 100f) return false;

        float free;
        if (a <= 22.5f) free = f;
        else if (a <= 67.5f) free = ga > 0f ? Mathf.Min(l, f + 0.2f) : Mathf.Min(r, f + 0.2f);
        else free = ga > 0f ? Mathf.Min(ls, l + 0.2f) : Mathf.Min(rs, r + 0.2f);

        // goal side must not be the wall we are hugging at close range
        bool goalTowardWall = (followSide > 0 && ga > 30f) || (followSide < 0 && ga < -30f);
        if (goalTowardWall && free < 0.6f) return false;

        free *= 0.9f; // account for up to 10% noise

        if (free < BLOCK_FRONT + 0.15f && free < gd) return false;

        // goal visible with clear line, or guaranteed progress (DistBug)
        if (free >= gd) return true;
        if (gd - free <= hitDist - LEAVE_STEP) return true;
        return false;
    }
}
