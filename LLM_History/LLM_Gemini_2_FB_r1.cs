using System;
using UnityEngine;

public class LLM_Gemini_2_FB : IRoverController
{
    private enum NavMode
    {
        SeekGoal,
        FollowLeftWall,
        FollowRightWall,
        EscapeTurn
    }

    private const int HistorySize = 3;
    private const float MaxTurnDeltaPerTick = 0.28f;
    private const float MaxSpeedDeltaPerTick = 0.22f;

    private NavMode _mode;
    private int _modeSteps;
    private int _escapeDir; // +1 = left, -1 = right
    private int _committedWallDir; // +1 = left wall, -1 = right wall

    private float[] _frontHist;
    private float[] _leftHist;
    private float[] _rightHist;
    private float[] _leftSideHist;
    private float[] _rightSideHist;
    private int _histIndex;
    private bool _histWarm;

    private float _bestGoalDist;
    private int _stepsSinceProgress;
    private int _oscillationCount;
    private int _lastTurnSign;
    private int _escapeCooldown;

    private float _smoothedTurn;
    private float _smoothedSpeed;

    public string Name => "LLM_Controller";

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _modeSteps = 0;
        _escapeDir = 1;
        _committedWallDir = 1;

        _frontHist = new float[HistorySize];
        _leftHist = new float[HistorySize];
        _rightHist = new float[HistorySize];
        _leftSideHist = new float[HistorySize];
        _rightSideHist = new float[HistorySize];
        _histIndex = 0;
        _histWarm = false;

        _bestGoalDist = float.MaxValue;
        _stepsSinceProgress = 0;
        _oscillationCount = 0;
        _lastTurnSign = 0;
        _escapeCooldown = 0;

        _smoothedTurn = 0f;
        _smoothedSpeed = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        if (_frontHist == null)
        {
            ResetController();
        }

        _modeSteps++;
        if (_escapeCooldown > 0)
        {
            _escapeCooldown--;
        }

        UpdateHistory(obs);

        float f = FilteredSensor(_frontHist);
        float l = FilteredSensor(_leftHist);
        float r = FilteredSensor(_rightHist);
        float ls = FilteredSensor(_leftSideHist);
        float rs = FilteredSensor(_rightSideHist);

        // Guard against sudden close readings while resisting single-reading dropouts
        float frontClear = Mathf.Min(f, Mathf.Min(obs.front * 1.05f, 4.0f));
        float diagMin = Mathf.Min(l, r);
        float frontConeClear = Mathf.Min(frontClear, Mathf.Min(l, r));

        // Track goal progress
        if (obs.goalDistance < _bestGoalDist - 0.06f)
        {
            _bestGoalDist = obs.goalDistance;
            _stepsSinceProgress = 0;
            _oscillationCount = Mathf.Max(0, _oscillationCount - 1);
        }
        else
        {
            _stepsSinceProgress++;
        }

        // Detect back-and-forth steering oscillation
        int turnSign = Math.Abs(_smoothedTurn) < 0.12f ? 0 : (_smoothedTurn > 0f ? 1 : -1);
        if (turnSign != 0 && _lastTurnSign != 0 && turnSign != _lastTurnSign)
        {
            _oscillationCount++;
        }
        if (turnSign != 0)
        {
            _lastTurnSign = turnSign;
        }

        // If stuck without progress or oscillating heavily, trigger a committed escape turn
        bool stuckTooLong = _stepsSinceProgress >= 110 && _escapeCooldown == 0;
        bool oscillatingBadly = _oscillationCount >= 14 && _escapeCooldown == 0 && _modeSteps >= 12;

        if ((stuckTooLong || oscillatingBadly) && _mode != NavMode.EscapeTurn)
        {
            _escapeDir = PickEscapeDirection(obs.goalAngleDeg, l, ls, r, rs);
            _committedWallDir = _escapeDir;
            _stepsSinceProgress = 0;
            _oscillationCount = 0;
            _escapeCooldown = 50;
            SwitchMode(NavMode.EscapeTurn);
        }

        // Mode state machine with hysteresis and minimum dwell times
        bool boxedInFront = frontClear < 0.28f && l < 0.34f && r < 0.34f;
        bool needObstacleAvoid = frontClear < 0.36f || diagMin < 0.25f;

        if (_mode == NavMode.EscapeTurn)
        {
            // Commit to escape turn long enough to clear a corner or U-turn (~1.4s to 2.6s)
            bool clearedPocket = frontConeClear > 0.56f && _modeSteps >= 24;
            if (clearedPocket || _modeSteps >= 52)
            {
                _oscillationCount = 0;
                _stepsSinceProgress = Mathf.Min(_stepsSinceProgress, 40);
                SwitchMode(_committedWallDir > 0 ? NavMode.FollowLeftWall : NavMode.FollowRightWall);
            }
        }
        else if (boxedInFront && _escapeCooldown == 0)
        {
            _escapeDir = PickEscapeDirection(obs.goalAngleDeg, l, ls, r, rs);
            _committedWallDir = _escapeDir;
            _escapeCooldown = 40;
            SwitchMode(NavMode.EscapeTurn);
        }
        else if (_mode == NavMode.SeekGoal)
        {
            if (needObstacleAvoid)
            {
                _committedWallDir = PickWallSide(obs.goalAngleDeg, l, ls, r, rs);
                SwitchMode(_committedWallDir > 0 ? NavMode.FollowLeftWall : NavMode.FollowRightWall);
            }
        }
        else if (_mode == NavMode.FollowLeftWall || _mode == NavMode.FollowRightWall)
        {
            bool isLeft = _mode == NavMode.FollowLeftWall;
            bool goalOnSameSide = isLeft ? (obs.goalAngleDeg > 5f) : (obs.goalAngleDeg < -5f);
            bool pathTowardGoalClear = frontConeClear > 0.62f &&
                                      Mathf.Abs(obs.goalAngleDeg) < 42f &&
                                      (!goalOnSameSide || (isLeft ? l > 0.65f && ls > 0.50f : r > 0.65f && rs > 0.50f));

            // Require at least 22 ticks (~1.1s) of wall following before exiting to SeekGoal
            if (_modeSteps >= 22 && pathTowardGoalClear)
            {
                SwitchMode(NavMode.SeekGoal);
            }
        }

        float targetTurn;
        float targetSpeed01;

        switch (_mode)
        {
            case NavMode.EscapeTurn:
                targetTurn = _escapeDir * 0.88f;
                targetSpeed01 = frontClear < 0.16f ? 0.05f : 0.20f;
                break;

            case NavMode.FollowLeftWall:
                ComputeWallFollow(
                    followLeft: true,
                    front: frontClear,
                    diagSame: l,
                    sideSame: ls,
                    diagOpp: r,
                    sideOpp: rs,
                    goalAngleDeg: obs.goalAngleDeg,
                    oscillationCount: _oscillationCount,
                    out targetTurn,
                    out targetSpeed01);
                break;

            case NavMode.FollowRightWall:
                ComputeWallFollow(
                    followLeft: false,
                    front: frontClear,
                    diagSame: r,
                    sideSame: rs,
                    diagOpp: l,
                    sideOpp: ls,
                    goalAngleDeg: obs.goalAngleDeg,
                    oscillationCount: _oscillationCount,
                    out targetTurn,
                    out targetSpeed01);
                break;

            case NavMode.SeekGoal:
            default:
                ComputeSeekGoal(
                    obs: obs,
                    front: frontClear,
                    l: l,
                    r: r,
                    ls: ls,
                    rs: rs,
                    oscillationCount: _oscillationCount,
                    out targetTurn,
                    out targetSpeed01);
                break;
        }

        // Rate-limit changes to prevent jitter and oscillation
        _smoothedTurn = Mathf.Clamp(
            targetTurn,
            _smoothedTurn - MaxTurnDeltaPerTick,
            _smoothedTurn + MaxTurnDeltaPerTick);

        _smoothedSpeed = Mathf.Clamp(
            targetSpeed01,
            _smoothedSpeed - MaxSpeedDeltaPerTick,
            _smoothedSpeed + MaxSpeedDeltaPerTick);

        float outTurn = Mathf.Clamp(_smoothedTurn, -1f, 1f);
        float outSpeed = Mathf.Clamp(_smoothedSpeed, 0f, 1f);

        if (frontClear < 0.13f)
        {
            outSpeed = Mathf.Min(outSpeed, 0.10f);
        }

        return (outSpeed, outTurn);
    }

    private void SwitchMode(NavMode next)
    {
        if (_mode != next)
        {
            _mode = next;
            _modeSteps = 0;
        }
    }

    private int PickWallSide(float goalAngleDeg, float l, float ls, float r, float rs)
    {
        float leftOpen = 0.55f * l + 0.45f * ls + (goalAngleDeg > 12f ? 0.12f : 0f);
        float rightOpen = 0.55f * r + 0.45f * rs + (goalAngleDeg < -12f ? 0.12f : 0f);
        if (Mathf.Abs(leftOpen - rightOpen) < 0.06f)
        {
            return _committedWallDir;
        }
        return leftOpen > rightOpen ? 1 : -1;
    }

    private int PickEscapeDirection(float goalAngleDeg, float l, float ls, float r, float rs)
    {
        float leftSpace = l + 0.8f * ls + (goalAngleDeg > 20f ? 0.15f : 0f);
        float rightSpace = r + 0.8f * rs + (goalAngleDeg < -20f ? 0.15f : 0f);
        if (Mathf.Abs(leftSpace - rightSpace) < 0.06f)
        {
            return _escapeDir != 0 ? _escapeDir : (goalAngleDeg >= 0f ? 1 : -1);
        }
        return leftSpace > rightSpace ? 1 : -1;
    }

    private void ComputeSeekGoal(
        Observation obs,
        float front,
        float l,
        float r,
        float ls,
        float rs,
        int oscillationCount,
        out float turn,
        out float speed01)
    {
        // Deadband around 0 deg so small angle noise doesn't wiggle the rover
        float angle = Mathf.Abs(obs.goalAngleDeg) < 3.5f ? 0f : obs.goalAngleDeg;
        float goalTurn = Mathf.Clamp(angle / 40f, -1f, 1f);

        // Smooth repulsion from side/diagonal obstacles
        float leftPush = Threat(l, 0.52f) * 0.95f + Threat(ls, 0.30f) * 0.60f;
        float rightPush = Threat(r, 0.52f) * 0.95f + Threat(rs, 0.30f) * 0.60f;
        float obstacleTurn = (rightPush - leftPush) * 1.05f;

        // If front clearance drops, smoothly blend toward the more open diagonal
        float frontBlend = Mathf.Clamp01((0.65f - front) / 0.45f);
        float openSideTurn = 0f;
        if (frontBlend > 0f)
        {
            float sideDiff = (l + 0.45f * ls) - (r + 0.45f * rs);
            float dir = Mathf.Abs(sideDiff) > 0.05f ? Mathf.Sign(sideDiff) : (angle >= 0f ? 1f : -1f);
            openSideTurn = dir * 0.80f;
        }

        turn = Mathf.Clamp(
            goalTurn * (1f - 0.55f * frontBlend) + obstacleTurn + openSideTurn * frontBlend,
            -1f,
            1f);

        float oscSlowdown = oscillationCount >= 6 ? 0.65f : 1f;
        float clearFactor = Mathf.Clamp01((front - 0.16f) / 0.60f);
        float turnFactor = 1f - 0.50f * Mathf.Abs(turn);
        float nearGoalFactor = obs.goalDistance < 0.35f ? Mathf.Clamp(obs.goalDistance / 0.35f, 0.35f, 1f) : 1f;

        speed01 = Mathf.Clamp(clearFactor * turnFactor * nearGoalFactor * oscSlowdown, 0.12f, 1.0f);
    }

    private void ComputeWallFollow(
        bool followLeft,
        float front,
        float diagSame,
        float sideSame,
        float diagOpp,
        float sideOpp,
        float goalAngleDeg,
        int oscillationCount,
        out float turn,
        out float speed01)
    {
        // +1 = left, -1 = right
        float towardWall = followLeft ? 1f : -1f;
        float awayWall = -towardWall;

        const float targetSideDist = 0.24f;
        float effectiveSide = Mathf.Min(sideSame, diagSame + 0.04f);

        // Smooth urgency when obstacle ahead or on the followed diagonal gets close
        float frontDanger = Mathf.Clamp01((0.40f - front) / 0.25f);
        float diagDanger = Mathf.Clamp01((0.28f - diagSame) / 0.18f);
        float avoidBlend = Mathf.Max(frontDanger, diagDanger);

        float avoidTurn = awayWall * (0.45f + 0.50f * avoidBlend);

        // Gentle wall distance controller with deadband to avoid chasing noisy side readings
        float err = effectiveSide - targetSideDist;
        if (Mathf.Abs(err) < 0.03f)
        {
            err = 0f;
        }
        float wallTrackTurn = Mathf.Clamp(err * 1.8f, -0.45f, 0.45f) * towardWall;

        // Subtle nudge toward goal only when well clear of the wall
        float goalNudge = 0f;
        if (avoidBlend < 0.15f && diagSame > 0.48f)
        {
            goalNudge = Mathf.Clamp(goalAngleDeg / 110f, -0.18f, 0.18f);
        }

        // Mild opposite-side avoidance in narrow corridors
        float oppThreat = Threat(diagOpp, 0.30f) * 0.55f + Threat(sideOpp, 0.22f) * 0.45f;
        float oppTurn = -awayWall * oppThreat * 0.40f;

        float followTurn = Mathf.Clamp(wallTrackTurn + goalNudge + oppTurn, -0.85f, 0.85f);
        turn = Mathf.Clamp(Mathf.Lerp(followTurn, avoidTurn, avoidBlend), -1f, 1f);

        float oscSlowdown = oscillationCount >= 6 ? 0.70f : 1f;
        float speedBase = Mathf.Lerp(0.20f, 0.72f, Mathf.Clamp01((Mathf.Min(front, diagSame) - 0.18f) / 0.50f));
        speed01 = Mathf.Clamp(speedBase * (1f - 0.35f * Mathf.Abs(turn)) * oscSlowdown, 0.12f, 0.85f);
    }

    private static float Threat(float dist, float maxRange)
    {
        if (dist >= maxRange) return 0f;
        float d = Mathf.Max(dist, 0.02f);
        return Mathf.Clamp01((maxRange - d) / (maxRange - 0.02f));
    }

    private void UpdateHistory(Observation obs)
    {
        _frontHist[_histIndex] = Mathf.Clamp(obs.front, 0.02f, 4.0f);
        _leftHist[_histIndex] = Mathf.Clamp(obs.left, 0.02f, 4.0f);
        _rightHist[_histIndex] = Mathf.Clamp(obs.right, 0.02f, 4.0f);
        _leftSideHist[_histIndex] = Mathf.Clamp(obs.leftSide, 0.02f, 4.0f);
        _rightSideHist[_histIndex] = Mathf.Clamp(obs.rightSide, 0.02f, 4.0f);

        _histIndex = (_histIndex + 1) % HistorySize;
        if (_histIndex == 0)
        {
            _histWarm = true;
        }
    }

    private float FilteredSensor(float[] buf)
    {
        if (!_histWarm && _histIndex == 1)
        {
            return buf[0];
        }

        int count = _histWarm ? HistorySize : _histIndex;
        float a = buf[0];
        float b = count > 1 ? buf[1] : a;
        float c = count > 2 ? buf[2] : b;

        float median = Median3(a, b, c);
        float minVal = Mathf.Min(a, Mathf.Min(b, c));
        return Mathf.Min(median, minVal + 0.05f);
    }

    private static float Median3(float a, float b, float c)
    {
        if (a > b) { float t = a; a = b; b = t; }
        if (b > c) { float t = b; b = c; c = t; }
        if (a > b) { float t = a; a = b; b = t; }
        return b;
    }
}
