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
        CommittedTurn
    }

    private NavMode _mode;
    private int _modeTicks;
    private int _clearGoalTicks;
    private int _commitSign;          // +1 = left, -1 = right
    private int _commitTicksLeft;
    private int _wallFlipCount;
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
        _clearGoalTicks = 0;
        _commitSign = 1;
        _commitTicksLeft = 0;
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
            float settleTurn = Mathf.Clamp(goalAngle / 40.0f, -0.35f, 0.35f);
            float smoothedSettle = Mathf.Lerp(_prevTurnCmd, settleTurn, 0.5f);
            _prevTurnCmd = smoothedSettle;
            return (0.15f, smoothedSettle);
        }

        float minForwardCone = Mathf.Min(front, Mathf.Min(left, right));
        bool blockedAhead = front < 0.46f || minForwardCone < 0.32f;
        bool tightPocket = blockedAhead && left < 0.58f && right < 0.58f;

        // Track consecutive ticks where the direct line toward the goal looks open
        if (IsGoalPathClear(goalAngle, front, left, right, leftSide, rightSide))
            _clearGoalTicks++;
        else
            _clearGoalTicks = 0;

        // State transitions with hysteresis to eliminate chatter and oscillation
        if (_mode == NavMode.CommittedTurn)
        {
            _commitTicksLeft--;

            bool pathOpenedEarly = _modeTicks >= 20 &&
                                   front > 0.85f &&
                                   left > 0.62f &&
                                   right > 0.62f;

            if (pathOpenedEarly || _commitTicksLeft <= 0)
            {
                // Transition smoothly into wall-following on the opposite side of the turn
                // so we hug the obstacle boundary rather than swinging back into it
                _mode = (_commitSign > 0) ? NavMode.FollowWallRight : NavMode.FollowWallLeft;
                _modeTicks = 0;
                _clearGoalTicks = 0;
                _ticksWithoutProgress = 0;
                _lastProgressGoalDist = goalDist;
                _oscillationScore = 0;
            }
        }
        else if (tightPocket || (_oscillationScore >= 7 && _ticksWithoutProgress >= 30))
        {
            StartCommittedTurn(
                sign: ChooseCommittedDirection(goalAngle, left, right, leftSide, rightSide),
                ticks: tightPocket ? 46 : 36
            );
        }
        else if (_mode == NavMode.SeekGoal)
        {
            if (blockedAhead && _modeTicks >= 8)
            {
                bool followLeft = ChooseWallFollowLeft(goalAngle, left, right, leftSide, rightSide);
                _mode = followLeft ? NavMode.FollowWallLeft : NavMode.FollowWallRight;
                _modeTicks = 0;
                _clearGoalTicks = 0;
            }
        }
        else // FollowWallLeft or FollowWallRight
        {
            // Require both a minimum dwell time and sustained clear path before resuming SeekGoal
            bool minDwellMet = _modeTicks >= 60;
            bool sustainedClear = _clearGoalTicks >= 8;
            bool progressed = goalDist + 0.16f < _lastProgressGoalDist;

            if (minDwellMet && sustainedClear && !blockedAhead && (progressed || _modeTicks >= 95))
            {
                _mode = NavMode.SeekGoal;
                _modeTicks = 0;
                _clearGoalTicks = 0;
                _wallFlipCount = 0;
            }
            else if (_ticksWithoutProgress >= 140)
            {
                if (_wallFlipCount < 1)
                {
                    _wallFlipCount++;
                    _mode = (_mode == NavMode.FollowWallLeft) ? NavMode.FollowWallRight : NavMode.FollowWallLeft;
                    _modeTicks = 0;
                    _clearGoalTicks = 0;
                    _ticksWithoutProgress = 0;
                    _lastProgressGoalDist = goalDist;
                    _oscillationScore = 0;
                }
                else
                {
                    _wallFlipCount = 0;
                    StartCommittedTurn(
                        sign: ChooseCommittedDirection(goalAngle, left, right, leftSide, rightSide),
                        ticks: 48
                    );
                }
            }
        }

        float speed01;
        float rawTurn;

        switch (_mode)
        {
            case NavMode.CommittedTurn:
                (speed01, rawTurn) = ComputeCommittedTurnCommand(front, left, right);
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

        // Low-pass filter on turn command dampens high-frequency sensor noise
        float alpha = (_mode == NavMode.CommittedTurn) ? 0.75f : 0.45f;
        float turn = Mathf.Lerp(_prevTurnCmd, rawTurn, alpha);

        TrackSteeringOscillation(turn);
        _prevTurnCmd = turn;

        speed01 = ApplyProximitySpeedLimit(speed01, turn, front, left, right, leftSide, rightSide);
        turn = Mathf.Clamp(turn, -1.0f, 1.0f);
        speed01 = Mathf.Clamp(speed01, 0.0f, 1.0f);

        return (speed01, turn);
    }

    private void StartCommittedTurn(int sign, int ticks)
    {
        _mode = NavMode.CommittedTurn;
        _modeTicks = 0;
        _commitSign = (sign >= 0) ? 1 : -1;
        _commitTicksLeft = ticks;
        _ticksWithoutProgress = 0;
        _lastProgressGoalDist = _prevGoalDist;
        _oscillationScore = 0;
        _clearGoalTicks = 0;
    }

    private bool ChooseWallFollowLeft(
        float goalAngle,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float leftSpace = left + 0.5f * leftSide;
        float rightSpace = right + 0.5f * rightSide;

        // Strong preference toward whichever side has noticeably more clearance
        if (Mathf.Abs(leftSpace - rightSpace) > 0.16f)
            return leftSpace > rightSpace;

        if (Mathf.Abs(goalAngle) > 25.0f)
            return goalAngle >= 0.0f;

        return (_wallFlipCount % 2 == 0);
    }

    private int ChooseCommittedDirection(
        float goalAngle,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float leftSpace = left + 0.65f * leftSide;
        float rightSpace = right + 0.65f * rightSide;

        if (Mathf.Abs(leftSpace - rightSpace) > 0.12f)
            return (leftSpace > rightSpace) ? 1 : -1;

        if (Mathf.Abs(goalAngle) > 30.0f)
            return (goalAngle >= 0.0f) ? 1 : -1;

        return (_wallFlipCount % 2 == 0) ? 1 : -1;
    }

    private (float speed01, float turn) ComputeSeekGoalCommand(
        float goalAngle,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        // Deadband prevents tiny left/right hunting when roughly pointed at the goal
        float effectiveAngle = (Mathf.Abs(goalAngle) < 5.0f) ? 0.0f : goalAngle;
        float goalTurn = effectiveAngle / 48.0f;

        // Only apply lateral bias when an obstacle is genuinely close so we don't fight goalAngle early
        float avoidanceBias = 0.0f;
        if (left < 0.42f)
            avoidanceBias -= (0.42f - left) * 1.1f;
        if (right < 0.42f)
            avoidanceBias += (0.42f - right) * 1.1f;
        if (leftSide < 0.22f)
            avoidanceBias -= (0.22f - leftSide) * 0.9f;
        if (rightSide < 0.22f)
            avoidanceBias += (0.22f - rightSide) * 0.9f;

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
        const float deadband = 0.05f;
        float turn = 0.0f;
        float speed01 = 0.65f;

        if (front < 0.56f || diagAhead < 0.46f)
        {
            float urgency = Mathf.Clamp01(1.0f - Mathf.Min(front / 0.56f, diagAhead / 0.46f));
            turn = awayWallSign * Mathf.Lerp(0.42f, 0.90f, urgency);
            speed01 = Mathf.Lerp(0.52f, 0.22f, urgency);
        }
        else if (sideWall < 1.45f)
        {
            float error = sideWall - desiredSideDist;
            if (Mathf.Abs(error) < deadband)
                error = 0.0f;
            turn = Mathf.Clamp(towardWallSign * (error * 1.5f), -0.45f, 0.45f);
            speed01 = 0.70f;
        }
        else
        {
            // Gentle steady curve toward wall when rounding an outer corner so sideWall doesn't cycle
            turn = towardWallSign * 0.22f;
            speed01 = 0.62f;
        }

        if (oppositeSideWall < 0.22f)
            turn += towardWallSign * ((0.22f - oppositeSideWall) * 1.0f);
        if (diagOpposite < 0.26f)
            turn += towardWallSign * ((0.26f - diagOpposite) * 1.0f);

        return (speed01, Mathf.Clamp(turn, -1.0f, 1.0f));
    }

    private (float speed01, float turn) ComputeCommittedTurnCommand(float front, float left, float right)
    {
        float turn = _commitSign * 0.90f;
        bool safeToCreep = front > 0.55f && left > 0.42f && right > 0.42f;
        float speed01 = safeToCreep ? 0.22f : 0.0f;
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
        if (Mathf.Abs(goalAngle) <= 18.0f)
            return front > 0.80f && left > 0.58f && right > 0.58f;

        if (goalAngle > 18.0f && goalAngle <= 60.0f)
            return left > 0.80f && front > 0.58f;

        if (goalAngle < -18.0f && goalAngle >= -60.0f)
            return right > 0.80f && front > 0.58f;

        if (goalAngle > 60.0f && goalAngle <= 105.0f)
            return leftSide > 0.80f && left > 0.58f;

        if (goalAngle < -60.0f && goalAngle >= -105.0f)
            return rightSide > 0.80f && right > 0.58f;

        return false;
    }

    private void TrackSteeringOscillation(float turn)
    {
        int sign = 0;
        if (turn > 0.15f) sign = 1;
        else if (turn < -0.15f) sign = -1;

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
