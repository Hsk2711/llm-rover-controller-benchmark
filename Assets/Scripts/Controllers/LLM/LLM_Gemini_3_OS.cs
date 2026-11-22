using System;
using UnityEngine;

public class LLM_Gemini_3_OS : IRoverController
{
    public string Name => "LLM_Controller";

    private const float Dt = 0.05f;
    private const float MaxSpeed = 0.30f;       // m/s
    private const float MaxTurnRateDeg = 90.0f; // deg/s
    private const float SensorMax = 4.0f;
    private const int WindowSize = 5;

    private enum NavMode
    {
        SeekGoal,
        FollowWallLeft,
        FollowWallRight,
        EscapeTurn
    }

    private NavMode _mode;
    private int _modeTicks;
    private int _escapeSign; // +1 = left, -1 = right
    private int _totalTicks;

    private float _bestGoalDist;
    private int _ticksWithoutProgress;
    private float _prevGoalDist;

    private readonly float[] _frontBuf = new float[WindowSize];
    private readonly float[] _leftBuf = new float[WindowSize];
    private readonly float[] _rightBuf = new float[WindowSize];
    private readonly float[] _leftSideBuf = new float[WindowSize];
    private readonly float[] _rightSideBuf = new float[WindowSize];
    private readonly float[] _sortScratch = new float[WindowSize];
    private int _bufIndex;
    private int _bufCount;

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _modeTicks = 0;
        _escapeSign = 1;
        _totalTicks = 0;

        _bestGoalDist = float.MaxValue;
        _ticksWithoutProgress = 0;
        _prevGoalDist = float.MaxValue;

        _bufIndex = 0;
        _bufCount = 0;

        for (int i = 0; i < WindowSize; i++)
        {
            _frontBuf[i] = SensorMax;
            _leftBuf[i] = SensorMax;
            _rightBuf[i] = SensorMax;
            _leftSideBuf[i] = SensorMax;
            _rightSideBuf[i] = SensorMax;
        }
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _totalTicks++;
        _modeTicks++;

        float rawFront = SanitizeSensor(obs.front);
        float rawLeft = SanitizeSensor(obs.left);
        float rawRight = SanitizeSensor(obs.right);
        float rawLeftSide = SanitizeSensor(obs.leftSide);
        float rawRightSide = SanitizeSensor(obs.rightSide);

        PushSample(rawFront, rawLeft, rawRight, rawLeftSide, rawRightSide);

        // Median filter suppresses single-sample noise and occasional false 4.0 readings
        float front = FilteredSensor(_frontBuf, rawFront);
        float left = FilteredSensor(_leftBuf, rawLeft);
        float right = FilteredSensor(_rightBuf, rawRight);
        float leftSide = FilteredSensor(_leftSideBuf, rawLeftSide);
        float rightSide = FilteredSensor(_rightSideBuf, rawRightSide);

        float goalDist = Mathf.Max(0.0f, obs.goalDistance);
        float goalAngle = NormalizeAngleDeg(obs.goalAngleDeg);

        UpdateProgressTracking(goalDist);

        // If already right on top of the goal centre, creep/stop safely
        if (goalDist < 0.10f)
        {
            float settleTurn = Mathf.Clamp(goalAngle / 35.0f, -0.5f, 0.5f);
            return (0.15f, settleTurn);
        }

        // Immediate hazard / dead-end detection
        float minForwardCone = Mathf.Min(front, Mathf.Min(left, right));
        bool blockedAhead = front < 0.42f || minForwardCone < 0.30f;
        bool tightPocket = blockedAhead && left < 0.55f && right < 0.55f;

        // State transitions
        if (tightPocket || (_ticksWithoutProgress > 140 && blockedAhead && _mode != NavMode.EscapeTurn))
        {
            if (_mode != NavMode.EscapeTurn)
            {
                _mode = NavMode.EscapeTurn;
                _modeTicks = 0;
                // Turn toward whichever side has slightly more room, or alternate if nearly tied
                if (Mathf.Abs(left - right) > 0.08f)
                    _escapeSign = (left > right) ? 1 : -1;
                else if (Mathf.Abs(leftSide - rightSide) > 0.08f)
                    _escapeSign = (leftSide > rightSide) ? 1 : -1;
                else
                    _escapeSign = (goalAngle >= 0.0f) ? 1 : -1;
            }
        }
        else if (_mode == NavMode.EscapeTurn)
        {
            // Exit escape turn once front/diagonal path opens up or after ~2.2s of pivoting
            bool frontCleared = front > 0.65f && ((_escapeSign > 0 && left > 0.55f) || (_escapeSign < 0 && right > 0.55f));
            if (frontCleared || _modeTicks >= 44)
            {
                _mode = (_escapeSign > 0) ? NavMode.FollowWallRight : NavMode.FollowWallLeft;
                _modeTicks = 0;
                _ticksWithoutProgress = 0;
            }
        }
        else if (_mode == NavMode.SeekGoal)
        {
            if (blockedAhead)
            {
                // Pick wall-following side to steer around obstacle toward goal
                bool preferLeft = (goalAngle > 0.0f && left > right + 0.10f) || (left > right + 0.25f);
                _mode = preferLeft ? NavMode.FollowWallLeft : NavMode.FollowWallRight;
                _modeTicks = 0;
            }
        }
        else // FollowWallLeft or FollowWallRight
        {
            bool goalDirectionClear = IsGoalPathClear(goalAngle, front, left, right, leftSide, rightSide);
            bool madeFreshProgress = goalDist + 0.12f < _bestGoalDist;

            // Re-engage goal seeking when goal is ahead and not blocked, or after a long wall segment with clear goal line
            if (goalDirectionClear && (!blockedAhead || _modeTicks > 70) && (madeFreshProgress || _modeTicks > 35))
            {
                _mode = NavMode.SeekGoal;
                _modeTicks = 0;
            }
            else if (_ticksWithoutProgress > 220)
            {
                // Flip wall-follow side if stuck circling or in a long obstacle loop
                _mode = (_mode == NavMode.FollowWallLeft) ? NavMode.FollowWallRight : NavMode.FollowWallLeft;
                _modeTicks = 0;
                _ticksWithoutProgress = 0;
            }
        }

        // Compute steering & speed for active mode
        float speed01;
        float turn;

        switch (_mode)
        {
            case NavMode.EscapeTurn:
                (speed01, turn) = ComputeEscapeCommand(front, left, right);
                break;

            case NavMode.FollowWallLeft:
                (speed01, turn) = ComputeWallFollowCommand(
                    followLeftWall: true,
                    front: front,
                    diagAhead: left,
                    diagOpposite: right,
                    sideWall: leftSide,
                    oppositeSideWall: rightSide
                );
                break;

            case NavMode.FollowWallRight:
                (speed01, turn) = ComputeWallFollowCommand(
                    followLeftWall: false,
                    front: front,
                    diagAhead: right,
                    diagOpposite: left,
                    sideWall: rightSide,
                    oppositeSideWall: leftSide
                );
                break;

            case NavMode.SeekGoal:
            default:
                (speed01, turn) = ComputeSeekGoalCommand(goalAngle, front, left, right, leftSide, rightSide);
                break;
        }

        // Final safety guard so rover doesn't drive fast into a close obstacle
        speed01 = ApplyProximitySpeedLimit(speed01, turn, front, left, right, leftSide, rightSide);
        turn = Mathf.Clamp(turn, -1.0f, 1.0f);
        speed01 = Mathf.Clamp(speed01, 0.0f, 1.0f);

        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeSeekGoalCommand(
        float goalAngle,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        // Repulsive push from nearby obstacles on left/right so rover stays centered/safe
        float avoidanceBias = 0.0f;
        if (left < 0.55f)
            avoidanceBias -= (0.55f - left) * 1.6f;
        if (right < 0.55f)
            avoidanceBias += (0.55f - right) * 1.6f;
        if (leftSide < 0.28f)
            avoidanceBias -= (0.28f - leftSide) * 1.2f;
        if (rightSide < 0.28f)
            avoidanceBias += (0.28f - rightSide) * 1.2f;

        float goalTurn = goalAngle / 42.0f;
        float turn = Mathf.Clamp(goalTurn + avoidanceBias, -1.0f, 1.0f);

        float angleFactor = Mathf.Clamp01(1.0f - Mathf.Abs(goalAngle) / 135.0f);
        float clearanceFactor = Mathf.Clamp01((front - 0.22f) / 1.10f);
        float speed01 = Mathf.Lerp(0.25f, 1.0f, angleFactor * clearanceFactor);

        if (Mathf.Abs(goalAngle) > 75.0f)
            speed01 = Mathf.Min(speed01, 0.35f);

        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeWallFollowCommand(
        bool followLeftWall,
        float front,
        float diagAhead,
        float diagOpposite,
        float sideWall,
        float oppositeSideWall)
    {
        // Sign convention: +1 = turn left, -1 = turn right
        float towardWallSign = followLeftWall ? +1.0f : -1.0f;
        float awayWallSign = -towardWallSign;

        const float desiredSideDist = 0.34f;
        float turn = 0.0f;
        float speed01 = 0.70f;

        // 1. Obstacle ahead or on the diagonal toward the followed wall -> turn away from wall
        if (front < 0.55f || diagAhead < 0.45f)
        {
            float urgency = Mathf.Clamp01(1.0f - Mathf.Min(front / 0.55f, diagAhead / 0.45f));
            turn = awayWallSign * Mathf.Lerp(0.45f, 1.0f, urgency);
            speed01 = Mathf.Lerp(0.55f, 0.20f, urgency);
        }
        // 2. Side sensor detects wall -> hold target standoff distance
        else if (sideWall < 1.35f)
        {
            float error = sideWall - desiredSideDist; // positive = too far from wall, negative = too close
            turn = Mathf.Clamp(towardWallSign * (error * 2.4f), -0.65f, 0.65f);
            speed01 = 0.75f;
        }
        // 3. Lost side wall (e.g., rounded an outer corner) -> curve gently back toward wall to find it
        else
        {
            turn = towardWallSign * 0.42f;
            speed01 = 0.65f;
        }

        // Corridor / opposite-side safety adjustment
        if (oppositeSideWall < 0.24f)
        {
            turn += towardWallSign * ((0.24f - oppositeSideWall) * 1.5f);
        }
        if (diagOpposite < 0.28f)
        {
            turn += towardWallSign * ((0.28f - diagOpposite) * 1.4f);
        }

        return (speed01, Mathf.Clamp(turn, -1.0f, 1.0f));
    }

    private (float speed01, float turn) ComputeEscapeCommand(float front, float left, float right)
    {
        // Pivot in place or barely creep forward while turning out of a tight spot / dead end
        float turn = _escapeSign * 1.0f;
        float speed01 = (front > 0.45f && left > 0.35f && right > 0.35f) ? 0.22f : 0.0f;
        return (speed01, turn);
    }

    private float ApplyProximitySpeedLimit(
        float speed01,
        float turn,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float minAll = Mathf.Min(front, Mathf.Min(left, right));
        float minSide = Mathf.Min(leftSide, rightSide);

        if (minAll < 0.20f || minSide < 0.14f)
            return Mathf.Min(speed01, 0.12f);

        if (minAll < 0.34f || minSide < 0.20f)
            speed01 = Mathf.Min(speed01, 0.35f);
        else if (minAll < 0.55f)
            speed01 = Mathf.Min(speed01, 0.65f);

        // Reduce forward speed during sharp turns so turning radius stays tight
        float absTurn = Mathf.Abs(turn);
        if (absTurn > 0.75f)
            speed01 = Mathf.Min(speed01, 0.30f);
        else if (absTurn > 0.45f)
            speed01 = Mathf.Min(speed01, 0.55f);

        return speed01;
    }

    private bool IsGoalPathClear(
        float goalAngle,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        if (Mathf.Abs(goalAngle) <= 22.0f)
            return front > 0.65f && left > 0.45f && right > 0.45f;

        if (goalAngle > 22.0f && goalAngle <= 68.0f)
            return left > 0.65f && front > 0.45f;

        if (goalAngle < -22.0f && goalAngle >= -68.0f)
            return right > 0.65f && front > 0.45f;

        if (goalAngle > 68.0f && goalAngle <= 115.0f)
            return leftSide > 0.65f && left > 0.45f;

        if (goalAngle < -68.0f && goalAngle >= -115.0f)
            return rightSide > 0.65f && right > 0.45f;

        return false;
    }

    private void UpdateProgressTracking(float goalDist)
    {
        if (goalDist + 0.04f < _bestGoalDist)
        {
            _bestGoalDist = goalDist;
            _ticksWithoutProgress = 0;
        }
        else if (Mathf.Abs(goalDist - _prevGoalDist) < 0.004f)
        {
            _ticksWithoutProgress++;
        }
        else
        {
            _ticksWithoutProgress++;
        }

        _prevGoalDist = goalDist;
    }

    private void PushSample(float f, float l, float r, float ls, float rs)
    {
        _frontBuf[_bufIndex] = f;
        _leftBuf[_bufIndex] = l;
        _rightBuf[_bufIndex] = r;
        _leftSideBuf[_bufIndex] = ls;
        _rightSideBuf[_bufIndex] = rs;

        _bufIndex = (_bufIndex + 1) % WindowSize;
        if (_bufCount < WindowSize)
            _bufCount++;
    }

    private float FilteredSensor(float[] buf, float currentRaw)
    {
        if (_bufCount <= 1)
            return currentRaw;

        int n = _bufCount;
        for (int i = 0; i < n; i++)
            _sortScratch[i] = buf[i];

        Array.Sort(_sortScratch, 0, n);
        float median = _sortScratch[n / 2];

        // If the latest sample is a sudden false 4.0 while the median shows a nearby obstacle,
        // clamp toward the median to avoid falsely assuming clear space.
        if (currentRaw >= SensorMax - 0.01f && median < 2.2f)
            return median;

        // Blend median and current sample for responsiveness + noise rejection
        return 0.65f * median + 0.35f * currentRaw;
    }

    private static float SanitizeSensor(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return SensorMax;
        return Mathf.Clamp(value, 0.02f, SensorMax);
    }

    private static float NormalizeAngleDeg(float angle)
    {
        if (float.IsNaN(angle) || float.IsInfinity(angle))
            return 0.0f;

        while (angle > 180.0f)
            angle -= 360.0f;
        while (angle < -180.0f)
            angle += 360.0f;
        return angle;
    }
}
