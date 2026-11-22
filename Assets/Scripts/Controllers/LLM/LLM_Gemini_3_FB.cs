using System;
using UnityEngine;

public class LLM_Gemini_3_FB : IRoverController
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
    private int _escapeSign;     // +1 = left, -1 = right
    private int _wallFlipCount;  // tracks how often we've switched wall side on this obstacle/run
    private int _totalTicks;

    private float _bestGoalDist;
    private float _lastProgressGoalDist;
    private int _ticksWithoutProgress;
    private float _prevGoalDist;

    private float _prevTurnCmd;
    private int _prevTurnSign;
    private int _oscillationScore;

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
        _wallFlipCount = 0;
        _totalTicks = 0;

        _bestGoalDist = float.MaxValue;
        _lastProgressGoalDist = float.MaxValue;
        _ticksWithoutProgress = 0;
        _prevGoalDist = float.MaxValue;

        _prevTurnCmd = 0.0f;
        _prevTurnSign = 0;
        _oscillationScore = 0;

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

        float front = FilteredSensor(_frontBuf, rawFront);
        float left = FilteredSensor(_leftBuf, rawLeft);
        float right = FilteredSensor(_rightBuf, rawRight);
        float leftSide = FilteredSensor(_leftSideBuf, rawLeftSide);
        float rightSide = FilteredSensor(_rightSideBuf, rawRightSide);

        float goalDist = Mathf.Max(0.0f, obs.goalDistance);
        float goalAngle = NormalizeAngleDeg(obs.goalAngleDeg);

        UpdateProgressTracking(goalDist);

        if (goalDist < 0.10f)
        {
            float settleTurn = Mathf.Clamp(goalAngle / 35.0f, -0.4f, 0.4f);
            _prevTurnCmd = settleTurn;
            return (0.15f, settleTurn);
        }

        float minForwardCone = Mathf.Min(front, Mathf.Min(left, right));
        bool blockedAhead = front < 0.44f || minForwardCone < 0.31f;
        bool tightPocket = blockedAhead && left < 0.56f && right < 0.56f;

        // Detect back-and-forth steering oscillation when not making steady progress
        if (_oscillationScore >= 8 && _ticksWithoutProgress > 40 && _mode != NavMode.EscapeTurn)
        {
            _escapeSign = ChooseEscapeDirection(goalAngle, left, right, leftSide, rightSide);
            _mode = NavMode.EscapeTurn;
            _modeTicks = 0;
            _oscillationScore = 0;
        }
        else if (tightPocket && _mode != NavMode.EscapeTurn)
        {
            _escapeSign = ChooseEscapeDirection(goalAngle, left, right, leftSide, rightSide);
            _mode = NavMode.EscapeTurn;
            _modeTicks = 0;
            _oscillationScore = 0;
        }
        else if (_mode == NavMode.EscapeTurn)
        {
            // Stay in escape turn long enough to clear a U-turn / pocket cleanly (~1.6s min, up to 3.0s)
            bool clearedPocket = _modeTicks >= 32 && front > 0.72f &&
                                 ((_escapeSign > 0 && left > 0.62f) || (_escapeSign < 0 && right > 0.62f));
            if (clearedPocket || _modeTicks >= 60)
            {
                _mode = (_escapeSign > 0) ? NavMode.FollowWallRight : NavMode.FollowWallLeft;
                _modeTicks = 0;
                _ticksWithoutProgress = 0;
                _lastProgressGoalDist = goalDist;
            }
        }
        else if (_mode == NavMode.SeekGoal)
        {
            // Require a few ticks in SeekGoal before re-entering wall follow to prevent edge chatter
            if (blockedAhead && _modeTicks >= 6)
            {
                bool preferLeft = ChooseWallFollowLeft(goalAngle, left, right, leftSide, rightSide);
                _mode = preferLeft ? NavMode.FollowWallLeft : NavMode.FollowWallRight;
                _modeTicks = 0;
            }
        }
        else // FollowWallLeft or FollowWallRight
        {
            // Hold wall-following for at least 55 ticks (2.75s) so we round obstacles rather than bouncing off edges
            bool minDwellMet = _modeTicks >= 55;
            bool goalClear = IsGoalPathClear(goalAngle, front, left, right, leftSide, rightSide);
            bool realProgress = goalDist + 0.18f < _lastProgressGoalDist;

            if (minDwellMet && goalClear && (!blockedAhead || _modeTicks >= 95) && (realProgress || _modeTicks >= 80))
            {
                _mode = NavMode.SeekGoal;
                _modeTicks = 0;
                _wallFlipCount = 0;
            }
            else if (_ticksWithoutProgress >= 150)
            {
                // Stuck along a wall/loop: if we haven't flipped yet, switch sides; otherwise do an escape pivot
                if (_wallFlipCount < 2)
                {
                    _wallFlipCount++;
                    _mode = (_mode == NavMode.FollowWallLeft) ? NavMode.FollowWallRight : NavMode.FollowWallLeft;
                    _modeTicks = 0;
                    _ticksWithoutProgress = 0;
                    _lastProgressGoalDist = goalDist;
                    _oscillationScore = 0;
                }
                else
                {
                    _wallFlipCount = 0;
                    _escapeSign = ChooseEscapeDirection(goalAngle, left, right, leftSide, rightSide);
                    _mode = NavMode.EscapeTurn;
                    _modeTicks = 0;
                    _ticksWithoutProgress = 0;
                    _lastProgressGoalDist = goalDist;
                    _oscillationScore = 0;
                }
            }
        }

        float speed01;
        float rawTurn;

        switch (_mode)
        {
            case NavMode.EscapeTurn:
                (speed01, rawTurn) = ComputeEscapeCommand(front, left, right);
                break;

            case NavMode.FollowWallLeft:
                (speed01, rawTurn) = ComputeWallFollowCommand(
                    followLeftWall: true,
                    front: front,
                    diagAhead: left,
                    diagOpposite: right,
                    sideWall: leftSide,
                    oppositeSideWall: rightSide
                );
                break;

            case NavMode.FollowWallRight:
                (speed01, rawTurn) = ComputeWallFollowCommand(
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
                (speed01, rawTurn) = ComputeSeekGoalCommand(goalAngle, front, left, right, leftSide, rightSide);
                break;
        }

        // Smooth turn command to dampen sensor jitter and prevent steering oscillation
        float smoothAlpha = (_mode == NavMode.EscapeTurn) ? 0.85f : 0.55f;
        float turn = Mathf.Lerp(_prevTurnCmd, rawTurn, smoothAlpha);

        TrackSteeringOscillation(turn);
        _prevTurnCmd = turn;

        speed01 = ApplyProximitySpeedLimit(speed01, turn, front, left, right, leftSide, rightSide);
        turn = Mathf.Clamp(turn, -1.0f, 1.0f);
        speed01 = Mathf.Clamp(speed01, 0.0f, 1.0f);

        return (speed01, turn);
    }

    private bool ChooseWallFollowLeft(
        float goalAngle,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float leftScore = left + 0.5f * leftSide + (goalAngle > 15.0f ? 0.25f : 0.0f);
        float rightScore = right + 0.5f * rightSide + (goalAngle < -15.0f ? 0.25f : 0.0f);

        if (Mathf.Abs(leftScore - rightScore) > 0.12f)
            return leftScore > rightScore;

        // Tie-breaker: turn toward goal sign
        return goalAngle >= 0.0f;
    }

    private int ChooseEscapeDirection(
        float goalAngle,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float leftOpen = left + 0.6f * leftSide;
        float rightOpen = right + 0.6f * rightSide;

        if (Mathf.Abs(leftOpen - rightOpen) > 0.10f)
            return (leftOpen > rightOpen) ? 1 : -1;

        if (Mathf.Abs(goalAngle) > 20.0f)
            return (goalAngle >= 0.0f) ? 1 : -1;

        return (_totalTicks % 2 == 0) ? 1 : -1;
    }

    private (float speed01, float turn) ComputeSeekGoalCommand(
        float goalAngle,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        // Deadband around 0 deg prevents tiny left/right wiggle when aligned with the goal
        float effectiveAngle = (Mathf.Abs(goalAngle) < 4.0f) ? 0.0f : goalAngle;
        float goalTurn = effectiveAngle / 45.0f;

        float avoidanceBias = 0.0f;
        if (left < 0.50f)
            avoidanceBias -= (0.50f - left) * 1.3f;
        if (right < 0.50f)
            avoidanceBias += (0.50f - right) * 1.3f;
        if (leftSide < 0.24f)
            avoidanceBias -= (0.24f - leftSide) * 1.0f;
        if (rightSide < 0.24f)
            avoidanceBias += (0.24f - rightSide) * 1.0f;

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
        float towardWallSign = followLeftWall ? +1.0f : -1.0f;
        float awayWallSign = -towardWallSign;

        const float desiredSideDist = 0.35f;
        const float deadband = 0.04f;
        float turn = 0.0f;
        float speed01 = 0.68f;

        if (front < 0.56f || diagAhead < 0.46f)
        {
            float urgency = Mathf.Clamp01(1.0f - Mathf.Min(front / 0.56f, diagAhead / 0.46f));
            turn = awayWallSign * Mathf.Lerp(0.45f, 0.95f, urgency);
            speed01 = Mathf.Lerp(0.55f, 0.22f, urgency);
        }
        else if (sideWall < 1.30f)
        {
            float error = sideWall - desiredSideDist;
            if (Mathf.Abs(error) < deadband)
                error = 0.0f;
            turn = Mathf.Clamp(towardWallSign * (error * 1.8f), -0.55f, 0.55f);
            speed01 = 0.72f;
        }
        else
        {
            // Gentle curve back toward wall when outer corner is passed
            turn = towardWallSign * 0.32f;
            speed01 = 0.65f;
        }

        if (oppositeSideWall < 0.22f)
            turn += towardWallSign * ((0.22f - oppositeSideWall) * 1.2f);
        if (diagOpposite < 0.26f)
            turn += towardWallSign * ((0.26f - diagOpposite) * 1.1f);

        return (speed01, Mathf.Clamp(turn, -1.0f, 1.0f));
    }

    private (float speed01, float turn) ComputeEscapeCommand(float front, float left, float right)
    {
        float turn = _escapeSign * 0.95f;
        float speed01 = (front > 0.50f && left > 0.40f && right > 0.40f) ? 0.20f : 0.0f;
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
        if (Mathf.Abs(goalAngle) <= 20.0f)
            return front > 0.75f && left > 0.52f && right > 0.52f;

        if (goalAngle > 20.0f && goalAngle <= 65.0f)
            return left > 0.75f && front > 0.52f;

        if (goalAngle < -20.0f && goalAngle >= -65.0f)
            return right > 0.75f && front > 0.52f;

        if (goalAngle > 65.0f && goalAngle <= 110.0f)
            return leftSide > 0.75f && left > 0.52f;

        if (goalAngle < -65.0f && goalAngle >= -110.0f)
            return rightSide > 0.75f && right > 0.52f;

        return false;
    }

    private void TrackSteeringOscillation(float turn)
    {
        int sign = 0;
        if (turn > 0.12f) sign = 1;
        else if (turn < -0.12f) sign = -1;

        if (sign != 0 && _prevTurnSign != 0 && sign != _prevTurnSign)
        {
            _oscillationScore += 2;
        }
        else if (_oscillationScore > 0)
        {
            _oscillationScore--;
        }

        if (sign != 0)
            _prevTurnSign = sign;
    }

    private void UpdateProgressTracking(float goalDist)
    {
        if (goalDist + 0.04f < _bestGoalDist)
        {
            _bestGoalDist = goalDist;
            _ticksWithoutProgress = 0;
            _oscillationScore = Mathf.Max(0, _oscillationScore - 2);
        }
        else
        {
            _ticksWithoutProgress++;
        }

        if (goalDist + 0.25f < _lastProgressGoalDist)
        {
            _lastProgressGoalDist = goalDist;
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

        if (currentRaw >= SensorMax - 0.01f && median < 2.2f)
            return median;

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
