using System;
using UnityEngine;

public class LLM_Gemini_2_OS : IRoverController
{
    private enum NavMode
    {
        SeekGoal,
        FollowLeftWall,
        FollowRightWall,
        PivotEscape
    }

    private const int HistorySize = 3;
    private const float Dt = 0.05f;
    private const float MaxSpeed = 0.30f;
    private const float MaxTurnRateDegPerSec = 90f;

    private NavMode _mode;
    private int _stepCount;
    private int _modeSteps;
    private int _pivotDirection; // +1 = left, -1 = right

    private float[] _frontHist;
    private float[] _leftHist;
    private float[] _rightHist;
    private float[] _leftSideHist;
    private float[] _rightSideHist;
    private int _histIndex;
    private bool _histWarm;

    private float _bestGoalDist;
    private int _stepsSinceProgress;
    private float _smoothedTurn;
    private float _smoothedSpeed;

    public string Name => "LLM_Controller";

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _stepCount = 0;
        _modeSteps = 0;
        _pivotDirection = 1;

        _frontHist = new float[HistorySize];
        _leftHist = new float[HistorySize];
        _rightHist = new float[HistorySize];
        _leftSideHist = new float[HistorySize];
        _rightSideHist = new float[HistorySize];
        _histIndex = 0;
        _histWarm = false;

        _bestGoalDist = float.MaxValue;
        _stepsSinceProgress = 0;
        _smoothedTurn = 0f;
        _smoothedSpeed = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        if (_frontHist == null)
        {
            ResetController();
        }

        _stepCount++;
        _modeSteps++;

        UpdateHistory(obs);

        float f = FilteredSensor(_frontHist);
        float l = FilteredSensor(_leftHist);
        float r = FilteredSensor(_rightHist);
        float ls = FilteredSensor(_leftSideHist);
        float rs = FilteredSensor(_rightSideHist);

        // Also keep raw immediate danger awareness so sudden close readings aren't delayed
        float minFrontCone = Mathf.Min(f, Mathf.Min(l, r));
        float immediateMinFront = Mathf.Min(obs.front, Mathf.Min(obs.left, obs.right));
        float frontClearance = Mathf.Min(minFrontCone, immediateMinFront);

        // Track goal progress for loop/dead-end escape
        if (obs.goalDistance < _bestGoalDist - 0.08f)
        {
            _bestGoalDist = obs.goalDistance;
            _stepsSinceProgress = 0;
        }
        else
        {
            _stepsSinceProgress++;
        }

        // If stuck without goal progress for ~7 seconds (140 ticks), flip wall-follow side
        if (_stepsSinceProgress >= 140 && _mode != NavMode.PivotEscape)
        {
            _stepsSinceProgress = 0;
            _bestGoalDist = obs.goalDistance;
            if (_mode == NavMode.FollowLeftWall)
            {
                SwitchMode(NavMode.FollowRightWall);
            }
            else if (_mode == NavMode.FollowRightWall)
            {
                SwitchMode(NavMode.FollowLeftWall);
            }
            else
            {
                SwitchMode(ChooseWallSide(obs.goalAngleDeg, l, ls, r, rs));
            }
        }

        // State transitions
        bool frontBlocked = frontClearance < 0.34f;
        bool tightPocket = f < 0.30f && l < 0.36f && r < 0.36f;

        if (tightPocket && _mode != NavMode.PivotEscape)
        {
            _pivotDirection = ChoosePivotDirection(obs.goalAngleDeg, l, ls, r, rs);
            SwitchMode(NavMode.PivotEscape);
        }
        else if (_mode == NavMode.PivotEscape)
        {
            // Exit pivot once front-ish path is reasonably open or after ~2.2s (44 ticks)
            if ((frontClearance > 0.48f && _modeSteps >= 8) || _modeSteps >= 44)
            {
                SwitchMode(ChooseWallSide(obs.goalAngleDeg, l, ls, r, rs));
            }
        }
        else if (_mode == NavMode.SeekGoal)
        {
            if (frontBlocked)
            {
                SwitchMode(ChooseWallSide(obs.goalAngleDeg, l, ls, r, rs));
            }
        }
        else if (_mode == NavMode.FollowLeftWall || _mode == NavMode.FollowRightWall)
        {
            // Check if goal direction is unobstructed and we've followed wall long enough
            bool goalOnLeft = obs.goalAngleDeg > 0f;
            bool goalSideClear = goalOnLeft
                ? (l > 0.55f && ls > 0.45f)
                : (r > 0.55f && rs > 0.45f);

            bool alignedTowardGoal = Mathf.Abs(obs.goalAngleDeg) < 55f;

            if (_modeSteps >= 16 && frontClearance > 0.52f && alignedTowardGoal && goalSideClear)
            {
                SwitchMode(NavMode.SeekGoal);
            }
            else if (frontClearance < 0.20f)
            {
                // Very close obstacle ahead: turn more sharply away
                _pivotDirection = (_mode == NavMode.FollowLeftWall) ? -1 : 1;
                if (l > r + 0.12f) _pivotDirection = 1;
                else if (r > l + 0.12f) _pivotDirection = -1;
            }
        }

        float targetTurn;
        float targetSpeed01;

        switch (_mode)
        {
            case NavMode.PivotEscape:
                targetTurn = _pivotDirection * 0.95f;
                targetSpeed01 = frontClearance < 0.18f ? 0.05f : 0.22f;
                break;

            case NavMode.FollowLeftWall:
                ComputeWallFollow(
                    isLeftWall: true,
                    f: frontClearance,
                    diagFront: l,
                    sideDist: ls,
                    oppositeDiag: r,
                    oppositeSide: rs,
                    goalAngleDeg: obs.goalAngleDeg,
                    out targetTurn,
                    out targetSpeed01);
                break;

            case NavMode.FollowRightWall:
                ComputeWallFollow(
                    isLeftWall: false,
                    f: frontClearance,
                    diagFront: r,
                    sideDist: rs,
                    oppositeDiag: l,
                    oppositeSide: ls,
                    goalAngleDeg: obs.goalAngleDeg,
                    out targetTurn,
                    out targetSpeed01);
                break;

            case NavMode.SeekGoal:
            default:
                ComputeGoalSeek(
                    obs: obs,
                    f: frontClearance,
                    l: l,
                    r: r,
                    ls: ls,
                    rs: rs,
                    out targetTurn,
                    out targetSpeed01);
                break;
        }

        // Smooth commands slightly to reduce oscillation from noisy sensors
        // At 90 deg/s max turn rate, 1.0 turn = 4.5 deg per 0.05s tick
        float maxTurnDelta = 0.45f;
        float maxSpeedDelta = 0.35f;

        _smoothedTurn = Mathf.Clamp(
            targetTurn,
            _smoothedTurn - maxTurnDelta,
            _smoothedTurn + maxTurnDelta);
        _smoothedSpeed = Mathf.Clamp(
            targetSpeed01,
            _smoothedSpeed - maxSpeedDelta,
            _smoothedSpeed + maxSpeedDelta);

        float finalTurn = Mathf.Clamp(_smoothedTurn, -1f, 1f);
        float finalSpeed = Mathf.Clamp(_smoothedSpeed, 0f, 1f);

        // Final safety clamp near close obstacles
        if (frontClearance < 0.14f)
        {
            finalSpeed = Mathf.Min(finalSpeed, 0.12f);
        }

        return (finalSpeed, finalTurn);
    }

    private void SwitchMode(NavMode nextMode)
    {
        if (_mode != nextMode)
        {
            _mode = nextMode;
            _modeSteps = 0;
        }
    }

    private NavMode ChooseWallSide(float goalAngleDeg, float l, float ls, float r, float rs)
    {
        float leftScore = 0.6f * l + 0.4f * ls + (goalAngleDeg > 0f ? 0.15f : 0f);
        float rightScore = 0.6f * r + 0.4f * rs + (goalAngleDeg < 0f ? 0.15f : 0f);
        return leftScore >= rightScore ? NavMode.FollowLeftWall : NavMode.FollowRightWall;
    }

    private int ChoosePivotDirection(float goalAngleDeg, float l, float ls, float r, float rs)
    {
        float leftRoom = l + 0.7f * ls + (goalAngleDeg > 15f ? 0.15f : 0f);
        float rightRoom = r + 0.7f * rs + (goalAngleDeg < -15f ? 0.15f : 0f);
        if (Mathf.Abs(leftRoom - rightRoom) < 0.05f)
        {
            return goalAngleDeg >= 0f ? 1 : -1;
        }
        return leftRoom > rightRoom ? 1 : -1;
    }

    private void ComputeGoalSeek(
        Observation obs,
        float f,
        float l,
        float r,
        float ls,
        float rs,
        out float turn01,
        out float speed01)
    {
        // Proportional steering toward goalAngleDeg (full turn around 45 deg)
        float goalTurn = Mathf.Clamp(obs.goalAngleDeg / 45f, -1f, 1f);

        // Repulsive steering away from obstacles on left/right
        // Positive repulsion = turn left (away from right obstacles)
        // Negative repulsion = turn right (away from left obstacles)
        float leftThreat = ThreatWeight(l, 0.55f) * 1.0f + ThreatWeight(ls, 0.32f) * 0.65f;
        float rightThreat = ThreatWeight(r, 0.55f) * 1.0f + ThreatWeight(rs, 0.32f) * 0.65f;
        float obstacleRepulsion = (rightThreat - leftThreat) * 1.15f;

        // If front is getting tight, bias turn toward clearer side
        float frontBias = 0f;
        if (f < 0.65f)
        {
            float urgency = Mathf.Clamp01((0.65f - f) / 0.50f);
            float sideDiff = (l + 0.5f * ls) - (r + 0.5f * rs);
            float dir = Mathf.Abs(sideDiff) > 0.04f
                ? Mathf.Sign(sideDiff)
                : (obs.goalAngleDeg >= 0f ? 1f : -1f);
            frontBias = dir * urgency * 0.85f;
        }

        turn01 = Mathf.Clamp(goalTurn * (1f - 0.45f * Mathf.Clamp01((0.60f - f) / 0.45f)) + obstacleRepulsion + frontBias, -1f, 1f);

        // Speed scales with clearance and turn sharpness
        float clearanceFactor = Mathf.Clamp01((f - 0.16f) / 0.65f);
        float turnPenalty = 1f - 0.55f * Mathf.Abs(turn01);
        float goalApproachSlowdown = obs.goalDistance < 0.35f ? Mathf.Clamp(obs.goalDistance / 0.35f, 0.35f, 1f) : 1f;

        speed01 = Mathf.Clamp(clearanceFactor * turnPenalty * goalApproachSlowdown, 0.12f, 1.0f);
    }

    private void ComputeWallFollow(
        bool isLeftWall,
        float f,
        float diagFront,
        float sideDist,
        float oppositeDiag,
        float oppositeSide,
        float goalAngleDeg,
        out float turn01,
        out float speed01)
    {
        // Sign helper: +1 means turn left, -1 means turn right
        float towardWall = isLeftWall ? 1f : -1f;
        float awayFromWall = -towardWall;

        const float desiredSideDist = 0.24f;
        float effectiveSide = Mathf.Min(sideDist, diagFront + 0.05f);

        // If obstacle ahead or diagonal front is close, turn away from wall
        if (f < 0.34f || diagFront < 0.26f)
        {
            float frontUrgency = Mathf.Clamp01((0.36f - Mathf.Min(f, diagFront + 0.06f)) / 0.24f);
            turn01 = awayFromWall * (0.45f + 0.55f * frontUrgency);
            speed01 = Mathf.Lerp(0.45f, 0.16f, frontUrgency);
            return;
        }

        // Maintain desired distance from the wall being followed
        float distError = effectiveSide - desiredSideDist;
        // If too far from wall (distError > 0), steer slightly toward wall; if too close (distError < 0), steer away
        float wallTrackTurn = Mathf.Clamp(distError * 2.2f, -0.55f, 0.55f) * towardWall;

        // Gentle nudge toward goal if it doesn't push us into the wall
        float goalNudge = Mathf.Clamp(goalAngleDeg / 90f, -0.25f, 0.25f);
        if (isLeftWall && goalNudge > 0f && diagFront < 0.55f) goalNudge = 0f;
        if (!isLeftWall && goalNudge < 0f && diagFront < 0.55f) goalNudge = 0f;

        // Avoid hitting opposite side if corridor is narrow
        float oppThreat = ThreatWeight(oppositeDiag, 0.32f) * 0.6f + ThreatWeight(oppositeSide, 0.22f) * 0.5f;
        float oppAvoid = -awayFromWall * oppThreat * 0.5f;

        turn01 = Mathf.Clamp(wallTrackTurn + goalNudge + oppAvoid, -1f, 1f);

        float narrowFactor = Mathf.Clamp01((Mathf.Min(f, diagFront) - 0.18f) / 0.55f);
        speed01 = Mathf.Lerp(0.22f, 0.75f, narrowFactor) * (1f - 0.35f * Mathf.Abs(turn01));
    }

    private static float ThreatWeight(float distance, float threshold)
    {
        if (distance >= threshold) return 0f;
        float clamped = Mathf.Max(distance, 0.02f);
        return Mathf.Clamp01((threshold - clamped) / (threshold - 0.02f));
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

        // Median of up to 3 samples suppresses single-tick false 4.0 dropouts and spikes
        float median = Median3(a, b, c);
        float minVal = Mathf.Min(a, Mathf.Min(b, c));

        // Blend median with minimum so real close obstacles aren't masked by dropouts
        return Mathf.Min(median, minVal + 0.06f);
    }

    private static float Median3(float a, float b, float c)
    {
        if (a > b) { float t = a; a = b; b = t; }
        if (b > c) { float t = b; b = c; c = t; }
        if (a > b) { float t = a; a = b; b = t; }
        return b;
    }
}
