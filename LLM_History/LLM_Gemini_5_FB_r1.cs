using System;
using UnityEngine;

public sealed class LLM_Gemini_5_FB : IRoverController
{
    public string Name => "LLM_AntiOscillationController";

    private enum NavMode
    {
        SeekGoal,
        FollowLeftWall,
        FollowRightWall,
        PivotClear
    }

    private const float MaxSensorRange = 4.0f;
    private const int HistorySize = 6;

    private NavMode _mode;
    private NavMode _pendingWallMode;
    private int _tick;
    private int _modeTicks;
    private int _preferredTurnSign; // +1 left, -1 right
    private float _bestGoalDist;
    private int _ticksSinceProgress;
    private float _prevTurn;
    private float _prevSpeed;

    private float[,] _hist;
    private int[] _histCount;
    private int[] _histIdx;

    public LLM_Gemini_5_FB()
    {
        _hist = new float[5, HistorySize];
        _histCount = new int[5];
        _histIdx = new int[5];
        ResetController();
    }

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _pendingWallMode = NavMode.FollowLeftWall;
        _tick = 0;
        _modeTicks = 0;
        _preferredTurnSign = 1;
        _bestGoalDist = float.MaxValue;
        _ticksSinceProgress = 0;
        _prevTurn = 0f;
        _prevSpeed = 0f;

        for (int s = 0; s < 5; s++)
        {
            _histCount[s] = 0;
            _histIdx[s] = 0;
            for (int k = 0; k < HistorySize; k++)
            {
                _hist[s, k] = MaxSensorRange;
            }
        }
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _tick++;
        _modeTicks++;

        if (obs.goalDistance < _bestGoalDist - 0.04f)
        {
            _bestGoalDist = obs.goalDistance;
            _ticksSinceProgress = 0;
        }
        else
        {
            _ticksSinceProgress++;
        }

        float f = FilterSensor(0, obs.front);
        float l45 = FilterSensor(1, obs.left);
        float r45 = FilterSensor(2, obs.right);
        float l90 = FilterSensor(3, obs.leftSide);
        float r90 = FilterSensor(4, obs.rightSide);

        float minFrontCone = Mathf.Min(f, Mathf.Min(l45 * 1.08f, r45 * 1.08f));
        float minAnyClose = Mathf.Min(minFrontCone, Mathf.Min(l90 * 1.15f, r90 * 1.15f));

        // Final approach to goal
        if (obs.goalDistance < 0.14f && minFrontCone > 0.24f)
        {
            float tGoal = Mathf.Clamp(obs.goalAngleDeg / 35.0f, -1f, 1f);
            float sGoal = Mathf.Clamp01(0.42f * (1f - 0.45f * Mathf.Abs(tGoal)));
            return ApplySmoothing(sGoal, tGoal, maxTurnDelta: 0.35f, maxSpeedDelta: 0.25f);
        }

        bool emergencyClose = f < 0.21f || l45 < 0.16f || r45 < 0.16f || minAnyClose < 0.13f;
        bool frontBlocked = f < 0.40f || l45 < 0.29f || r45 < 0.29f;

        if (_mode == NavMode.SeekGoal)
        {
            if (_modeTicks >= 4 && (emergencyClose || frontBlocked))
            {
                NavMode chosen = ChooseWallSide(obs.goalAngleDeg, l45, r45, l90, r90);
                EnterWallFollow(chosen, pivotFirst: emergencyClose);
            }
        }
        else if (_mode == NavMode.PivotClear)
        {
            bool frontClearEnough = f > 0.52f && l45 > 0.38f && r45 > 0.38f;
            if ((frontClearEnough && _modeTicks >= 6) || _modeTicks >= 36)
            {
                _mode = _pendingWallMode;
                _modeTicks = 0;
            }
        }
        else if (_mode == NavMode.FollowLeftWall || _mode == NavMode.FollowRightWall)
        {
            bool leftWall = (_mode == NavMode.FollowLeftWall);
            float sideDist = leftWall ? l90 : r90;
            float diagWallDist = leftWall ? l45 : r45;
            float oppDiagDist = leftWall ? r45 : l45;

            if (emergencyClose && _modeTicks >= 5)
            {
                EnterWallFollow(_mode, pivotFirst: true);
            }
            else if (_modeTicks >= 16)
            {
                float goalErr = Mathf.Abs(obs.goalAngleDeg);
                bool goalAhead = goalErr < 45.0f;
                bool goalDirClear = obs.goalAngleDeg >= 0f
                    ? (f > 0.80f && l45 > 0.72f)
                    : (f > 0.80f && r45 > 0.72f);

                bool lostWall = sideDist > 1.35f && diagWallDist > 1.25f && f > 0.85f;
                bool canRejoinGoal = goalAhead && goalDirClear && oppDiagDist > 0.40f;

                if (_ticksSinceProgress > 260 && _modeTicks > 55)
                {
                    // Switch wall side if stuck in a dead end / long loop without progress
                    _ticksSinceProgress = 120;
                    NavMode opposite = leftWall ? NavMode.FollowRightWall : NavMode.FollowLeftWall;
                    EnterWallFollow(opposite, pivotFirst: f < 0.35f);
                }
                else if (lostWall || canRejoinGoal)
                {
                    _mode = NavMode.SeekGoal;
                    _modeTicks = 0;
                }
            }
        }

        float speed01;
        float turn;

        switch (_mode)
        {
            case NavMode.PivotClear:
                {
                    float dir = (_pendingWallMode == NavMode.FollowLeftWall) ? 1.0f : -1.0f;
                    turn = dir * 0.70f;
                    speed01 = (f > 0.25f && l45 > 0.19f && r45 > 0.19f) ? 0.08f : 0.0f;
                    return ApplySmoothing(speed01, turn, maxTurnDelta: 0.30f, maxSpeedDelta: 0.20f);
                }

            case NavMode.FollowLeftWall:
                {
                    ComputeWallFollow(
                        followLeft: true,
                        f: f,
                        diagSame: l45,
                        diagOpp: r45,
                        sideSame: l90,
                        sideOpp: r90,
                        goalAngleDeg: obs.goalAngleDeg,
                        out speed01,
                        out turn
                    );
                    return ApplySmoothing(speed01, turn, maxTurnDelta: 0.28f, maxSpeedDelta: 0.20f);
                }

            case NavMode.FollowRightWall:
                {
                    ComputeWallFollow(
                        followLeft: false,
                        f: f,
                        diagSame: r45,
                        diagOpp: l45,
                        sideSame: r90,
                        sideOpp: l90,
                        goalAngleDeg: obs.goalAngleDeg,
                        out speed01,
                        out turn
                    );
                    return ApplySmoothing(speed01, turn, maxTurnDelta: 0.28f, maxSpeedDelta: 0.20f);
                }

            case NavMode.SeekGoal:
            default:
                {
                    float desiredTurn = Mathf.Clamp(obs.goalAngleDeg / 42.0f, -1.0f, 1.0f);

                    float leftThreat = ThreatWeight(l45, 0.52f) * 1.05f + ThreatWeight(l90, 0.32f) * 0.65f;
                    float rightThreat = ThreatWeight(r45, 0.52f) * 1.05f + ThreatWeight(r90, 0.32f) * 0.65f;
                    float frontThreat = ThreatWeight(f, 0.62f);

                    // Update sticky turn preference only when there is a clear asymmetry
                    float threatDiff = rightThreat - leftThreat;
                    if (Mathf.Abs(threatDiff) > 0.14f)
                    {
                        _preferredTurnSign = threatDiff > 0f ? 1 : -1;
                    }
                    else if (Mathf.Abs(obs.goalAngleDeg) > 20f)
                    {
                        _preferredTurnSign = obs.goalAngleDeg >= 0f ? 1 : -1;
                    }

                    float avoidBias = Mathf.Clamp(threatDiff * 0.85f, -0.65f, 0.65f);
                    if (frontThreat > 0.08f)
                    {
                        avoidBias += _preferredTurnSign * frontThreat * 0.85f;
                    }

                    float rawTurn = desiredTurn * (1.0f - 0.50f * Mathf.Clamp01(frontThreat)) + avoidBias;
                    // Dampen rapid sign reversals around zero
                    if (Mathf.Sign(rawTurn) != Mathf.Sign(_prevTurn) && Mathf.Abs(_prevTurn) > 0.15f)
                    {
                        rawTurn *= 0.65f;
                    }

                    turn = Mathf.Clamp(rawTurn, -1.0f, 1.0f);

                    float turnFactor = 1.0f - 0.50f * Mathf.Clamp01(Mathf.Abs(turn));
                    float distFactor = Mathf.Clamp01((minFrontCone - 0.18f) / 0.65f);
                    speed01 = Mathf.Clamp(0.88f * turnFactor * (0.25f + 0.75f * distFactor), 0.14f, 0.95f);

                    return ApplySmoothing(speed01, turn, maxTurnDelta: 0.32f, maxSpeedDelta: 0.22f);
                }
        }
    }

    private void EnterWallFollow(NavMode targetFollowMode, bool pivotFirst)
    {
        _modeTicks = 0;
        _pendingWallMode = targetFollowMode;
        _preferredTurnSign = (targetFollowMode == NavMode.FollowLeftWall) ? 1 : -1;
        _mode = pivotFirst ? NavMode.PivotClear : targetFollowMode;
    }

    private NavMode ChooseWallSide(float goalAngleDeg, float l45, float r45, float l90, float r90)
    {
        if (l90 < 0.45f && r90 > 0.85f) return NavMode.FollowLeftWall;
        if (r90 < 0.45f && l90 > 0.85f) return NavMode.FollowRightWall;

        float leftScore = l45 + 0.55f * l90 + (goalAngleDeg < -20f ? 0.22f : -0.08f);
        float rightScore = r45 + 0.55f * r90 + (goalAngleDeg > 20f ? 0.22f : -0.08f);

        if (Mathf.Abs(leftScore - rightScore) < 0.08f)
        {
            return _preferredTurnSign >= 0 ? NavMode.FollowLeftWall : NavMode.FollowRightWall;
        }

        return (leftScore > rightScore) ? NavMode.FollowLeftWall : NavMode.FollowRightWall;
    }

    private void ComputeWallFollow(
        bool followLeft,
        float f,
        float diagSame,
        float diagOpp,
        float sideSame,
        float sideOpp,
        float goalAngleDeg,
        out float speed01,
        out float turn)
    {
        float s = followLeft ? 1.0f : -1.0f;
        const float targetSideDist = 0.25f;

        if (f < 0.29f || diagSame < 0.23f)
        {
            turn = -s * 0.85f;
            speed01 = (f < 0.18f || diagSame < 0.15f) ? 0.05f : 0.20f;
            return;
        }

        // Narrow corridor centering with deadband to avoid left-right chatter
        if (sideSame < 0.48f && sideOpp < 0.48f)
        {
            float centerErr = sideOpp - sideSame;
            float corrTurn = Mathf.Abs(centerErr) < 0.03f ? 0f : Mathf.Clamp(centerErr * 1.6f, -0.45f, 0.45f);
            float fSlow = Mathf.Clamp01((f - 0.24f) / 0.60f);
            turn = corrTurn;
            speed01 = Mathf.Clamp(0.50f * fSlow, 0.15f, 0.60f);
            return;
        }

        float distError = sideSame - targetSideDist;
        float deadbandError = Mathf.Abs(distError) < 0.025f ? 0f : distError;
        float wallTurn = s * Mathf.Clamp(deadbandError * 1.75f, -0.42f, 0.42f);

        if (diagSame < 0.40f)
        {
            float diagPush = (0.40f - diagSame) * 1.9f;
            wallTurn -= s * Mathf.Clamp(diagPush, 0f, 0.60f);
        }

        float goalNudge = Mathf.Clamp(goalAngleDeg / 140.0f, -0.15f, 0.15f);
        if (s * goalNudge < 0f && sideSame < 0.36f)
        {
            goalNudge = 0f;
        }

        turn = Mathf.Clamp(wallTurn + goalNudge, -1.0f, 1.0f);

        float minClear = Mathf.Min(f, Mathf.Min(diagSame, Mathf.Min(diagOpp, sideSame)));
        float clearFactor = Mathf.Clamp01((minClear - 0.16f) / 0.55f);
        float turnPenalty = 1.0f - 0.40f * Mathf.Abs(turn);
        speed01 = Mathf.Clamp(0.75f * clearFactor * turnPenalty, 0.15f, 0.80f);
    }

    private static float ThreatWeight(float dist, float influenceRadius)
    {
        if (dist >= influenceRadius) return 0f;
        float clamped = Mathf.Max(dist, 0.02f);
        float t = (influenceRadius - clamped) / influenceRadius;
        return t * t;
    }

    private float FilterSensor(int sensorIndex, float raw)
    {
        float clamped = Mathf.Clamp(raw, 0.02f, MaxSensorRange);

        if (clamped >= MaxSensorRange - 1e-4f && _histCount[sensorIndex] > 0)
        {
            float recentAvg = ComputeAverage(sensorIndex);
            if (recentAvg < 1.35f)
            {
                clamped = Mathf.Clamp(recentAvg + 0.15f, 0.02f, 1.8f);
            }
        }

        int idx = _histIdx[sensorIndex];
        _hist[sensorIndex, idx] = clamped;
        _histIdx[sensorIndex] = (idx + 1) % HistorySize;
        if (_histCount[sensorIndex] < HistorySize)
        {
            _histCount[sensorIndex]++;
        }

        int count = _histCount[sensorIndex];
        float[] temp = new float[count];
        for (int i = 0; i < count; i++)
        {
            temp[i] = _hist[sensorIndex, i];
        }
        Array.Sort(temp);
        float median = temp[count / 2];

        float mean = ComputeAverage(sensorIndex);
        return Mathf.Clamp(0.70f * median + 0.30f * mean, 0.02f, MaxSensorRange);
    }

    private float ComputeAverage(int sensorIndex)
    {
        int count = _histCount[sensorIndex];
        if (count == 0) return MaxSensorRange;
        float sum = 0f;
        for (int i = 0; i < count; i++)
        {
            sum += _hist[sensorIndex, i];
        }
        return sum / count;
    }

    private (float speed01, float turn) ApplySmoothing(
        float targetSpeed,
        float targetTurn,
        float maxTurnDelta,
        float maxSpeedDelta)
    {
        float turn = Mathf.Clamp(targetTurn, _prevTurn - maxTurnDelta, _prevTurn + maxTurnDelta);
        turn = Mathf.Clamp(turn, -1.0f, 1.0f);

        float speed = Mathf.Clamp(targetSpeed, _prevSpeed - maxSpeedDelta, _prevSpeed + maxSpeedDelta);
        speed = Mathf.Clamp01(speed);

        _prevTurn = turn;
        _prevSpeed = speed;
        return (speed, turn);
    }
}
