using System;
using UnityEngine;

public sealed class LLM_Gemini_5_OS : IRoverController
{
    public string Name => "LLM_ReactiveWallGoalController";

    private enum NavMode
    {
        SeekGoal,
        FollowLeftWall,
        FollowRightWall,
        PivotClear
    }

    private const float Dt = 0.05f;
    private const float MaxSpeedMps = 0.30f;
    private const float MaxTurnDegPerSec = 90.0f;
    private const float SensorMaxRange = 4.0f;
    private const int HistorySize = 5;

    private NavMode _mode;
    private int _tick;
    private int _modeTicks;
    private float _bestGoalDist;
    private int _ticksSinceProgress;
    private float _prevTurn;
    private float _prevSpeed;

    private float[,] _hist;
    private int[] _histCount;
    private int[] _histIdx;

    public LLM_Gemini_5_OS()
    {
        _hist = new float[5, HistorySize];
        _histCount = new int[5];
        _histIdx = new int[5];
        ResetController();
    }

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _tick = 0;
        _modeTicks = 0;
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
                _hist[s, k] = SensorMaxRange;
            }
        }
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _tick++;
        _modeTicks++;

        // Track goal progress for escape / re-evaluation heuristics
        if (obs.goalDistance < _bestGoalDist - 0.05f)
        {
            _bestGoalDist = obs.goalDistance;
            _ticksSinceProgress = 0;
        }
        else
        {
            _ticksSinceProgress++;
        }

        // Filter raw ultrasonic sensors against Gaussian noise and occasional false 4.0m readings
        float f = FilterSensor(0, obs.front);
        float l45 = FilterSensor(1, obs.left);
        float r45 = FilterSensor(2, obs.right);
        float l90 = FilterSensor(3, obs.leftSide);
        float r90 = FilterSensor(4, obs.rightSide);

        // Effective front & diagonal hazard distances (rover is 0.20m long x 0.15m wide)
        float minFrontCone = Mathf.Min(f, Mathf.Min(l45 * 1.08f, r45 * 1.08f));
        float minAnyClose = Mathf.Min(minFrontCone, Mathf.Min(l90 * 1.15f, r90 * 1.15f));

        // Goal reached or very close: approach smoothly
        if (obs.goalDistance < 0.12f && minFrontCone > 0.22f)
        {
            float tGoal = Mathf.Clamp(obs.goalAngleDeg / 35.0f, -1f, 1f);
            float sGoal = Mathf.Clamp01(0.45f * (1f - Mathf.Abs(tGoal) * 0.5f));
            return ApplySmoothing(sGoal, tGoal);
        }

        // Mode transitions
        bool frontBlocked = f < 0.42f || l45 < 0.30f || r45 < 0.30f;
        bool emergencyClose = f < 0.22f || l45 < 0.16f || r45 < 0.16f || minAnyClose < 0.13f;

        if (_mode == NavMode.SeekGoal)
        {
            if (emergencyClose)
            {
                EnterWallFollow(ChooseWallSide(obs.goalAngleDeg, l45, r45, l90, r90), true);
            }
            else if (frontBlocked)
            {
                EnterWallFollow(ChooseWallSide(obs.goalAngleDeg, l45, r45, l90, r90), false);
            }
        }
        else if (_mode == NavMode.PivotClear)
        {
            // Pivot until front/diagonal cone has enough room to move or wall-follow
            if (f > 0.48f && l45 > 0.34f && r45 > 0.34f && _modeTicks >= 4)
            {
                _mode = NavMode.SeekGoal;
                _modeTicks = 0;
            }
            else if (_modeTicks > 45) // ~2.25s max pivot before switching to wall follow
            {
                EnterWallFollow(ChooseWallSide(obs.goalAngleDeg, l45, r45, l90, r90), false);
            }
        }
        else if (_mode == NavMode.FollowLeftWall || _mode == NavMode.FollowRightWall)
        {
            bool leftWall = (_mode == NavMode.FollowLeftWall);
            float sideDist = leftWall ? l90 : r90;
            float diagWallDist = leftWall ? l45 : r45;
            float oppDiagDist = leftWall ? r45 : l45;

            // Check if we can leave the wall toward a clear goal direction
            float goalErr = Mathf.Abs(obs.goalAngleDeg);
            bool goalRoughlyAhead = goalErr < 55.0f;
            bool pathTowardGoalClear =
                (obs.goalAngleDeg >= 0f ? l45 > 0.65f && f > 0.70f : r45 > 0.65f && f > 0.70f);

            // Leave wall if path toward goal is clear after following obstacle a bit, or if we lost the wall
            bool lostWall = sideDist > 1.25f && diagWallDist > 1.15f && _modeTicks > 8;
            bool canRejoinGoal = goalRoughlyAhead && pathTowardGoalClear && _modeTicks > 12 && oppDiagDist > 0.35f;

            // If stuck in a tight U-turn / dead-end for too long without goal progress, switch wall side
            if (_ticksSinceProgress > 220 && _modeTicks > 40) // ~11s without progress
            {
                _ticksSinceProgress = 100;
                EnterWallFollow(leftWall ? NavMode.FollowRightWall : NavMode.FollowLeftWall, frontBlocked);
            }
            else if (lostWall || canRejoinGoal)
            {
                _mode = NavMode.SeekGoal;
                _modeTicks = 0;
            }
        }

        float speed01;
        float turn;

        switch (_mode)
        {
            case NavMode.PivotClear:
                {
                    // Turn toward the more open side (or away from closer obstacle) with minimal forward creep
                    float leftOpen = l45 + 0.7f * l90;
                    float rightOpen = r45 + 0.7f * r90;
                    float dir = (leftOpen >= rightOpen) ? 1.0f : -1.0f;
                    if (Mathf.Abs(leftOpen - rightOpen) < 0.08f)
                    {
                        dir = (obs.goalAngleDeg >= 0f) ? 1.0f : -1.0f;
                    }
                    turn = dir * 0.85f;
                    speed01 = (f > 0.24f && l45 > 0.18f && r45 > 0.18f) ? 0.12f : 0.0f;
                    break;
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
                    break;
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
                    break;
                }

            case NavMode.SeekGoal:
            default:
                {
                    // Base turn toward goal
                    float desiredTurn = Mathf.Clamp(obs.goalAngleDeg / 38.0f, -1.0f, 1.0f);

                    // Repulsive steering bias from nearby obstacles (positive turn = steer left, negative = right)
                    float leftThreat = ThreatWeight(l45, 0.55f) * 1.15f + ThreatWeight(l90, 0.34f) * 0.75f;
                    float rightThreat = ThreatWeight(r45, 0.55f) * 1.15f + ThreatWeight(r90, 0.34f) * 0.75f;
                    float frontThreat = ThreatWeight(f, 0.65f);

                    float avoidBias = (rightThreat - leftThreat);
                    if (frontThreat > 0.05f)
                    {
                        float fSign = (obs.goalAngleDeg >= 0f) ? 1.0f : -1.0f;
                        if (Mathf.Abs(leftThreat - rightThreat) > 0.15f)
                        {
                            fSign = (rightThreat > leftThreat) ? 1.0f : -1.0f;
                        }
                        avoidBias += fSign * frontThreat * 1.25f;
                    }

                    turn = Mathf.Clamp(desiredTurn * (1.0f - 0.55f * Mathf.Clamp01(frontThreat)) + avoidBias, -1.0f, 1.0f);

                    // Speed modulation: slow down for sharp turns or close obstacles
                    float turnFactor = 1.0f - 0.55f * Mathf.Clamp01(Mathf.Abs(obs.goalAngleDeg) / 90.0f);
                    float distFactor = Mathf.Clamp01((minFrontCone - 0.18f) / 0.65f);
                    speed01 = Mathf.Clamp(0.95f * turnFactor * (0.25f + 0.75f * distFactor), 0.12f, 1.0f);
                    break;
                }
        }

        return ApplySmoothing(speed01, turn);
    }

    private void EnterWallFollow(NavMode targetFollowMode, bool forcePivotFirst)
    {
        _modeTicks = 0;
        if (forcePivotFirst)
        {
            _mode = NavMode.PivotClear;
            // Store intended follow mode in _prevSpeed sign bit via helper? Better keep explicit:
            // If we pivot first, next tick or PivotClear completion will transition cleanly.
        }
        else
        {
            _mode = targetFollowMode;
        }
    }

    private NavMode ChooseWallSide(float goalAngleDeg, float l45, float r45, float l90, float r90)
    {
        // Prefer following the wall on the side opposite the goal so turning toward the goal opens up naturally,
        // unless one side is much closer/clearer.
        float leftScore = l45 + 0.6f * l90 + (goalAngleDeg < -15f ? 0.25f : -0.10f);
        float rightScore = r45 + 0.6f * r90 + (goalAngleDeg > 15f ? 0.25f : -0.10f);

        // In tight spaces, hug whichever side has a clearer wall within reach
        if (l90 < 0.45f && r90 > 0.85f) return NavMode.FollowLeftWall;
        if (r90 < 0.45f && l90 > 0.85f) return NavMode.FollowRightWall;

        return (leftScore >= rightScore) ? NavMode.FollowLeftWall : NavMode.FollowRightWall;
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
        // Sign helper: +1 means turn left, -1 means turn right
        float s = followLeft ? 1.0f : -1.0f;

        // Target distance to keep from the followed side wall (rover half-width is 0.075m)
        const float targetSideDist = 0.24f;

        // Inside corner / obstacle directly ahead: turn sharply away from followed wall
        if (f < 0.30f || diagSame < 0.24f)
        {
            turn = -s * 0.95f;
            speed01 = (f < 0.18f || diagSame < 0.15f) ? 0.05f : 0.22f;
            return;
        }

        // Corridor centering if both sides are narrow
        if (sideSame < 0.50f && sideOpp < 0.50f)
        {
            float centerErr = (sideOpp - sideSame); // positive => more room on opposite side
            float corrTurn = Mathf.Clamp(centerErr * 2.2f, -0.65f, 0.65f);
            float fSlow = Mathf.Clamp01((f - 0.25f) / 0.60f);
            turn = corrTurn;
            speed01 = Mathf.Clamp(0.55f * fSlow, 0.15f, 0.65f);
            return;
        }

        // Standard wall following PD-like control:
        // If sideSame < targetSideDist -> too close to wall -> steer away (-s)
        // If sideSame > targetSideDist -> drifting away from wall -> steer slightly toward wall (+s)
        float distError = sideSame - targetSideDist;
        float wallTurn = s * Mathf.Clamp(distError * 2.4f, -0.55f, 0.55f);

        // Also react to diagonal sensor on the wall side so we don't clip protruding corners
        if (diagSame < 0.42f)
        {
            float diagPush = (0.42f - diagSame) * 2.5f;
            wallTurn -= s * Mathf.Clamp(diagPush, 0f, 0.75f);
        }

        // Gentle nudging toward goal if turning toward goal moves away from the wall
        float goalNudge = Mathf.Clamp(goalAngleDeg / 120.0f, -0.2f, 0.2f);
        if (s * goalNudge < 0f && sideSame < 0.35f)
        {
            goalNudge = 0f;
        }

        turn = Mathf.Clamp(wallTurn + goalNudge, -1.0f, 1.0f);

        float minClear = Mathf.Min(f, Mathf.Min(diagSame, Mathf.Min(diagOpp, sideSame)));
        float clearFactor = Mathf.Clamp01((minClear - 0.16f) / 0.55f);
        float turnPenalty = 1.0f - 0.45f * Mathf.Abs(turn);
        speed01 = Mathf.Clamp(0.80f * clearFactor * turnPenalty, 0.15f, 0.85f);
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
        float clamped = Mathf.Clamp(raw, 0.02f, SensorMaxRange);

        // Detect likely false 4.0m reading when recent readings were significantly closer
        if (clamped >= SensorMaxRange - 1e-4f && _histCount[sensorIndex] > 0)
        {
            float recentAvg = ComputeAverage(sensorIndex);
            if (recentAvg < 1.35f)
            {
                // Blend a conservative reading so an occasional false 4.0 doesn't cause a sudden lunge
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

        // Use median of recent window to reject single-sample noise spikes
        int count = _histCount[sensorIndex];
        float[] temp = new float[count];
        for (int i = 0; i < count; i++)
        {
            temp[i] = _hist[sensorIndex, i];
        }
        Array.Sort(temp);
        float median = temp[count / 2];

        // Combine median and mean slightly for smooth, responsive distance estimate
        float mean = ComputeAverage(sensorIndex);
        return Mathf.Clamp(0.65f * median + 0.35f * mean, 0.02f, SensorMaxRange);
    }

    private float ComputeAverage(int sensorIndex)
    {
        int count = _histCount[sensorIndex];
        if (count == 0) return SensorMaxRange;
        float sum = 0f;
        for (int i = 0; i < count; i++)
        {
            sum += _hist[sensorIndex, i];
        }
        return sum / count;
    }

    private (float speed01, float turn) ApplySmoothing(float targetSpeed, float targetTurn)
    {
        // Limit per-tick changes slightly for smooth actuation while staying responsive
        float maxTurnDelta = 0.65f;
        float turn = Mathf.Clamp(targetTurn, _prevTurn - maxTurnDelta, _prevTurn + maxTurnDelta);
        turn = Mathf.Clamp(turn, -1.0f, 1.0f);

        float maxSpeedDelta = 0.35f;
        float speed = Mathf.Clamp(targetSpeed, _prevSpeed - maxSpeedDelta, _prevSpeed + maxSpeedDelta);
        speed = Mathf.Clamp01(speed);

        _prevTurn = turn;
        _prevSpeed = speed;
        return (speed, turn);
    }
}
