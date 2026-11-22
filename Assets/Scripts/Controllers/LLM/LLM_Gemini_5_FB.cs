using System;
using UnityEngine;

public sealed class LLM_Gemini_5_FB : IRoverController
{
    public string Name => "LLM_SteadyNavController";

    private enum Mode
    {
        SeekGoal,
        FollowLeft,
        FollowRight,
        Unstick
    }

    private const float MaxRange = 4.0f;
    private const int Window = 5;

    private Mode _mode;
    private int _modeTicks;
    private int _unstickTicks;
    private int _unstickCooldown;
    private float _unstickTurn;

    private float _bestGoalDist;
    private int _ticksNoProgress;

    private float _lastTurn;
    private int _reversalCount;
    private int _reversalDecayTick;

    private float[,] _buf;
    private int[] _bufLen;
    private int[] _bufPos;

    public LLM_Gemini_5_FB()
    {
        _buf = new float[5, Window];
        _bufLen = new int[5];
        _bufPos = new int[5];
        ResetController();
    }

    public void ResetController()
    {
        _mode = Mode.SeekGoal;
        _modeTicks = 0;
        _unstickTicks = 0;
        _unstickCooldown = 0;
        _unstickTurn = 0.7f;

        _bestGoalDist = float.MaxValue;
        _ticksNoProgress = 0;

        _lastTurn = 0f;
        _reversalCount = 0;
        _reversalDecayTick = 0;

        for (int i = 0; i < 5; i++)
        {
            _bufLen[i] = 0;
            _bufPos[i] = 0;
            for (int j = 0; j < Window; j++)
            {
                _buf[i, j] = MaxRange;
            }
        }
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTicks++;
        if (_unstickCooldown > 0) _unstickCooldown--;

        if (obs.goalDistance < _bestGoalDist - 0.05f)
        {
            _bestGoalDist = obs.goalDistance;
            _ticksNoProgress = 0;
        }
        else
        {
            _ticksNoProgress++;
        }

        float f = ReadFiltered(0, obs.front);
        float l45 = ReadFiltered(1, obs.left);
        float r45 = ReadFiltered(2, obs.right);
        float l90 = ReadFiltered(3, obs.leftSide);
        float r90 = ReadFiltered(4, obs.rightSide);

        float minFront = Mathf.Min(f, Mathf.Min(l45, r45));

        // Goal close and unobstructed
        if (obs.goalDistance < 0.15f && minFront > 0.24f)
        {
            float t = Mathf.Abs(obs.goalAngleDeg) < 4f ? 0f : Mathf.Clamp(obs.goalAngleDeg / 45f, -0.8f, 0.8f);
            float s = Mathf.Clamp01(0.40f * (1f - 0.4f * Mathf.Abs(t)));
            TrackReversals(t);
            return (s, t);
        }

        // Decay reversal counter gradually
        _reversalDecayTick++;
        if (_reversalDecayTick >= 25)
        {
            _reversalDecayTick = 0;
            _reversalCount = Mathf.Max(0, _reversalCount - 2);
        }

        // Trigger Unstick only when genuinely stuck or oscillating, respecting cooldown
        bool stuckByTime = _ticksNoProgress > 220; // ~11s without new best goal distance
        bool stuckByOsc = _reversalCount >= 10;

        if (_mode != Mode.Unstick && _unstickCooldown == 0 && (stuckByTime || stuckByOsc))
        {
            _reversalCount = 0;
            _ticksNoProgress = 80;
            _unstickCooldown = 80; // ~4s cooldown
            _unstickTicks = 50;    // ~2.5s committed maneuver

            if (_mode == Mode.FollowLeft)
            {
                _unstickTurn = -0.65f;
            }
            else if (_mode == Mode.FollowRight)
            {
                _unstickTurn = 0.65f;
            }
            else
            {
                float leftOpen = l45 + 0.75f * l90;
                float rightOpen = r45 + 0.75f * r90;
                if (Mathf.Abs(leftOpen - rightOpen) < 0.12f)
                {
                    _unstickTurn = obs.goalAngleDeg >= 0f ? 0.65f : -0.65f;
                }
                else
                {
                    _unstickTurn = leftOpen > rightOpen ? 0.65f : -0.65f;
                }
            }

            _mode = Mode.Unstick;
            _modeTicks = 0;
        }

        if (_mode == Mode.Unstick)
        {
            _unstickTicks--;
            float turnEsc = _unstickTurn;
            if (f < 0.22f)
            {
                // Turn harder in place if very close to an obstacle
                turnEsc = Mathf.Sign(_unstickTurn) * 0.85f;
            }
            float speedEsc = (f > 0.30f && l45 > 0.24f && r45 > 0.24f) ? 0.25f : 0.06f;

            if (_unstickTicks <= 0)
            {
                if (l90 < 0.55f && _unstickTurn > 0f)
                {
                    _mode = Mode.FollowLeft;
                }
                else if (r90 < 0.55f && _unstickTurn < 0f)
                {
                    _mode = Mode.FollowRight;
                }
                else
                {
                    _mode = Mode.SeekGoal;
                }
                _modeTicks = 0;
            }

            TrackReversals(turnEsc);
            return (speedEsc, turnEsc);
        }

        // Mode transitions with strong hysteresis
        bool blockedAhead = f < 0.44f || l45 < 0.32f || r45 < 0.32f;

        if (_mode == Mode.SeekGoal)
        {
            if (blockedAhead && _modeTicks >= 5)
            {
                _mode = PickWallMode(obs.goalAngleDeg, l45, r45, l90, r90);
                _modeTicks = 0;
            }
        }
        else // FollowLeft or FollowRight
        {
            bool isLeft = (_mode == Mode.FollowLeft);
            float sideDist = isLeft ? l90 : r90;
            float diagWall = isLeft ? l45 : r45;

            if (_modeTicks >= 24) // Stay on wall at least ~1.2s
            {
                bool wallGone = sideDist > 1.50f && diagWall > 1.35f && f > 0.90f;

                bool goalRoughlyAhead = Mathf.Abs(obs.goalAngleDeg) < 35f;
                bool goalSideClear = obs.goalAngleDeg >= 0f ? l45 > 0.78f : r45 > 0.78f;
                bool clearToRejoin = goalRoughlyAhead && goalSideClear && f > 1.00f && l45 > 0.55f && r45 > 0.55f;

                if (wallGone || clearToRejoin)
                {
                    _mode = Mode.SeekGoal;
                    _modeTicks = 0;
                }
            }
        }

        float speed01;
        float turn;

        if (_mode == Mode.FollowLeft)
        {
            SteerWall(followLeft: true, f, l45, r45, l90, r90, out speed01, out turn);
        }
        else if (_mode == Mode.FollowRight)
        {
            SteerWall(followLeft: false, f, r45, l45, r90, l90, out speed01, out turn);
        }
        else
        {
            SteerSeek(obs.goalAngleDeg, f, l45, r45, l90, r90, out speed01, out turn);
        }

        TrackReversals(turn);
        return (speed01, turn);
    }

    private static Mode PickWallMode(float goalAngleDeg, float l45, float r45, float l90, float r90)
    {
        if (l90 < 0.35f && r90 > 0.85f) return Mode.FollowLeft;
        if (r90 < 0.35f && l90 > 0.85f) return Mode.FollowRight;

        if (goalAngleDeg < -15f)
        {
            return r45 < l45 - 0.10f ? Mode.FollowRight : Mode.FollowLeft;
        }
        if (goalAngleDeg > 15f)
        {
            return l45 < r45 - 0.10f ? Mode.FollowLeft : Mode.FollowRight;
        }

        return (l45 + l90) < (r45 + r90) ? Mode.FollowLeft : Mode.FollowRight;
    }

    private void SteerSeek(
        float goalAngleDeg,
        float f,
        float l45,
        float r45,
        float l90,
        float r90,
        out float speed01,
        out float turn)
    {
        float angleCmd = Mathf.Abs(goalAngleDeg) < 3.5f
            ? 0f
            : Mathf.Clamp(goalAngleDeg / 48f, -0.85f, 0.85f);

        float leftPush = SoftPush(l45, 0.55f) * 0.9f + SoftPush(l90, 0.30f) * 0.5f;
        float rightPush = SoftPush(r45, 0.55f) * 0.9f + SoftPush(r90, 0.30f) * 0.5f;
        float frontPush = SoftPush(f, 0.70f);

        float avoidCmd = Mathf.Clamp(rightPush - leftPush, -0.65f, 0.65f);
        if (frontPush > 0.05f)
        {
            float dir = Mathf.Abs(rightPush - leftPush) > 0.15f
                ? (rightPush > leftPush ? 1f : -1f)
                : (goalAngleDeg >= 0f ? 1f : -1f);
            avoidCmd += dir * frontPush * 0.85f;
        }

        float mix = Mathf.Clamp01(frontPush * 0.8f + Mathf.Max(leftPush, rightPush) * 0.4f);
        turn = Mathf.Clamp(angleCmd * (1f - mix) + avoidCmd, -1f, 1f);

        float minFront = Mathf.Min(f, Mathf.Min(l45, r45));
        float distFactor = Mathf.Clamp01((minFront - 0.18f) / 0.55f);
        float turnFactor = 1f - 0.45f * Mathf.Abs(turn);
        speed01 = Mathf.Clamp(0.80f * distFactor * turnFactor, 0.16f, 0.90f);
    }

    private void SteerWall(
        bool followLeft,
        float f,
        float diagSame,
        float diagOpp,
        float sideSame,
        float sideOpp,
        out float speed01,
        out float turn)
    {
        // +1 = turn left, -1 = turn right
        float s = followLeft ? 1f : -1f;
        const float targetDist = 0.25f;

        if (f < 0.30f || diagSame < 0.23f)
        {
            turn = -s * 0.75f;
            speed01 = (f < 0.18f || diagSame < 0.15f) ? 0.08f : 0.22f;
            return;
        }

        // Corridor centering with deadband
        if (sideSame < 0.44f && sideOpp < 0.44f)
        {
            float diff = sideOpp - sideSame;
            turn = Mathf.Abs(diff) < 0.04f ? 0f : Mathf.Clamp(diff * 1.4f, -0.40f, 0.40f);
            speed01 = Mathf.Clamp(0.48f * Mathf.Clamp01((f - 0.24f) / 0.50f), 0.18f, 0.55f);
            return;
        }

        float err = sideSame - targetDist;
        if (Mathf.Abs(err) < 0.03f) err = 0f;

        float cmd = s * Mathf.Clamp(err * 1.5f, -0.35f, 0.35f);

        if (diagSame < 0.40f)
        {
            cmd -= s * Mathf.Clamp((0.40f - diagSame) * 1.6f, 0f, 0.50f);
        }

        turn = Mathf.Clamp(cmd, -1f, 1f);

        float minClear = Mathf.Min(f, Mathf.Min(diagSame, Mathf.Min(diagOpp, sideSame)));
        float distFactor = Mathf.Clamp01((minClear - 0.18f) / 0.50f);
        float turnFactor = 1f - 0.35f * Mathf.Abs(turn);
        speed01 = Mathf.Clamp(0.70f * distFactor * turnFactor, 0.18f, 0.75f);
    }

    private static float SoftPush(float dist, float radius)
    {
        if (dist >= radius) return 0f;
        float d = Mathf.Max(dist, 0.02f);
        float x = (radius - d) / radius;
        return x * x;
    }

    private void TrackReversals(float turn)
    {
        if (Mathf.Abs(turn) > 0.22f && Mathf.Abs(_lastTurn) > 0.22f)
        {
            if (Mathf.Sign(turn) != Mathf.Sign(_lastTurn))
            {
                _reversalCount++;
            }
        }
        _lastTurn = turn;
    }

    private float ReadFiltered(int sensorIdx, float raw)
    {
        float v = Mathf.Clamp(raw, 0.02f, MaxRange);

        // If a reading suddenly jumps to 4.0 while recent readings were close, treat as a likely false 4.0
        if (v >= MaxRange - 1e-4f && _bufLen[sensorIdx] > 0)
        {
            float sum = 0f;
            for (int i = 0; i < _bufLen[sensorIdx]; i++)
            {
                sum += _buf[sensorIdx, i];
            }
            float avg = sum / _bufLen[sensorIdx];
            if (avg < 1.0f)
            {
                v = avg;
            }
        }

        int pos = _bufPos[sensorIdx];
        _buf[sensorIdx, pos] = v;
        _bufPos[sensorIdx] = (pos + 1) % Window;
        if (_bufLen[sensorIdx] < Window) _bufLen[sensorIdx]++;

        int len = _bufLen[sensorIdx];
        float[] copy = new float[len];
        for (int i = 0; i < len; i++)
        {
            copy[i] = _buf[sensorIdx, i];
        }
        Array.Sort(copy);
        return copy[len / 2];
    }
}
