using System;
using UnityEngine;

public class LLM_Gemini_1_FB : IRoverController
{
    private enum Mode
    {
        SeekGoal,
        AvoidTurn,
        WallFollow,
        DeadEndTurn
    }

    private const float Dt = 0.05f;
    private const float NoDetectValue = 4.0f;

    // Rover size: 0.20 m long x 0.15 m wide
    private const float FrontStopDist = 0.31f;
    private const float FrontSlowDist = 0.72f;
    private const float SideDangerDist = 0.21f;
    private const float SideComfortDist = 0.36f;

    private Mode _mode;

    // Filtered ultrasonic distances (m)
    private float _fFront;
    private float _fLeft;
    private float _fRight;
    private float _fLeftSide;
    private float _fRightSide;

    // Consecutive 4.0 (no-detection) counters
    private int _noDetFront;
    private int _noDetLeft;
    private int _noDetRight;
    private int _noDetLeftSide;
    private int _noDetRightSide;

    // Navigation & anti-oscillation state
    private float _escapeDir;           // +1 = left, -1 = right
    private float _avoidDir;            // hysteresis-locked avoidance side
    private float _modeTimer;           // seconds spent in current mode
    private float _goalDistAtModeStart; // goal distance when entering AvoidTurn / WallFollow
    private float _bestGoalDist;        // best goal distance achieved in run
    private float _noProgressTimer;     // time without meaningful improvement in _bestGoalDist
    private float _prevGoalAngle;       // smoothed goal angle for deadband stability
    private float _lastSignTurn;        // last non-trivial turn direction (+1 / -1)
    private int _signFlipCount;         // recent direction reversals
    private float _flipDecayTimer;      // decay timer for _signFlipCount

    public string Name => "SteadyProgressNavigator";

    public void ResetController()
    {
        _mode = Mode.SeekGoal;

        _fFront = NoDetectValue;
        _fLeft = NoDetectValue;
        _fRight = NoDetectValue;
        _fLeftSide = NoDetectValue;
        _fRightSide = NoDetectValue;

        _noDetFront = 0;
        _noDetLeft = 0;
        _noDetRight = 0;
        _noDetLeftSide = 0;
        _noDetRightSide = 0;

        _escapeDir = 1.0f;
        _avoidDir = 1.0f;
        _modeTimer = 0.0f;
        _goalDistAtModeStart = float.MaxValue;
        _bestGoalDist = float.MaxValue;
        _noProgressTimer = 0.0f;
        _prevGoalAngle = 0.0f;
        _lastSignTurn = 0.0f;
        _signFlipCount = 0;
        _flipDecayTimer = 0.0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTimer += Dt;
        _flipDecayTimer += Dt;
        if (_flipDecayTimer >= 1.0f)
        {
            _flipDecayTimer = 0.0f;
            if (_signFlipCount > 0)
            {
                _signFlipCount--;
            }
        }

        UpdateFilteredSensors(obs);

        float rawGoalAngle = Mathf.Clamp(obs.goalAngleDeg, -180.0f, 180.0f);
        float goalDist = Mathf.Max(0.0f, obs.goalDistance);

        // Smooth goal angle slightly and apply a small deadband to prevent centerline chatter
        _prevGoalAngle = Mathf.Lerp(_prevGoalAngle, rawGoalAngle, 0.5f);
        float goalAngle = Mathf.Abs(_prevGoalAngle) < 3.5f ? 0.0f : _prevGoalAngle;

        if (goalDist < _bestGoalDist - 0.06f)
        {
            _bestGoalDist = goalDist;
            _noProgressTimer = 0.0f;
        }
        else
        {
            _noProgressTimer += Dt;
        }

        float leftClear = Mathf.Min(_fLeft, _fLeftSide * 1.05f);
        float rightClear = Mathf.Min(_fRight, _fRightSide * 1.05f);

        bool frontBlocked = _fFront < FrontStopDist ||
                            (_fLeft < 0.25f && Mathf.Abs(goalAngle) < 65.0f) ||
                            (_fRight < 0.25f && Mathf.Abs(goalAngle) < 65.0f);

        bool inTightSpace = (_fLeftSide < SideDangerDist && _fRightSide < SideDangerDist) ||
                            (_fFront < 0.48f && leftClear < SideComfortDist && rightClear < SideComfortDist);

        // Mode transitions
        if (_mode == Mode.SeekGoal)
        {
            if (_noProgressTimer > 5.5f)
            {
                // Stuck / looping without net progress: commit to a long dead-end turn
                _escapeDir = ChooseTurnDirection(goalAngle, leftClear, rightClear, flipPrevious: true);
                _mode = Mode.DeadEndTurn;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
                _noProgressTimer = 0.0f;
                _signFlipCount = 0;
            }
            else if (frontBlocked)
            {
                _escapeDir = ChooseTurnDirection(goalAngle, leftClear, rightClear, flipPrevious: false);
                _mode = inTightSpace ? Mode.WallFollow : Mode.AvoidTurn;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
                _signFlipCount = 0;
            }
        }
        else if (_mode == Mode.AvoidTurn)
        {
            bool clearAhead = _fFront > FrontSlowDist && _fLeft > 0.38f && _fRight > 0.38f;
            bool goalAligned = Mathf.Abs(goalAngle) < 45.0f && IsDirectionClear(goalAngle);
            bool madeProgress = goalDist < _goalDistAtModeStart - 0.14f;

            // Commit for at least 0.85 s so we clear the corner before re-aiming at goal
            if (_modeTimer >= 0.85f && clearAhead && (goalAligned || (madeProgress && _modeTimer >= 1.3f)))
            {
                SwitchToSeek();
            }
            else if (_fLeftSide < SideDangerDist && _fRightSide < SideDangerDist)
            {
                _mode = Mode.WallFollow;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
            }
            else if (_modeTimer >= 2.8f)
            {
                // If still turning after 2.8 s, switch to WallFollow rather than re-entering SeekGoal into a wall
                _mode = Mode.WallFollow;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
            }
        }
        else if (_mode == Mode.WallFollow)
        {
            bool frontOpen = _fFront > FrontSlowDist && _fLeft > 0.40f && _fRight > 0.40f;
            bool goalClear = IsDirectionClear(goalAngle) && Mathf.Abs(goalAngle) < 60.0f;
            bool improvedSinceStart = goalDist < _goalDistAtModeStart - 0.18f;

            // Stay in WallFollow at least 1.8 s and require either clear goal path + progress or 3.5 s sweep
            if (_modeTimer >= 1.8f && frontOpen && goalClear && (improvedSinceStart || _modeTimer >= 3.5f))
            {
                SwitchToSeek();
            }
            else if (_modeTimer >= 7.5f && !improvedSinceStart)
            {
                // Likely trapped in a U-shape: rotate firmly to try escaping out of the pocket
                _escapeDir = -_escapeDir;
                _mode = Mode.DeadEndTurn;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
            }
        }
        else if (_mode == Mode.DeadEndTurn)
        {
            bool pathOut = _fFront > 0.75f && leftClear > 0.45f && rightClear > 0.45f && Mathf.Abs(goalAngle) < 85.0f;
            if (_modeTimer >= 1.6f && pathOut)
            {
                _mode = Mode.WallFollow;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
            }
            else if (_modeTimer >= 3.4f)
            {
                _mode = Mode.WallFollow;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
            }
        }

        float speed01;
        float turnCmd;

        switch (_mode)
        {
            case Mode.AvoidTurn:
                (speed01, turnCmd) = ComputeAvoidTurn(goalAngle, leftClear, rightClear);
                break;
            case Mode.WallFollow:
                (speed01, turnCmd) = ComputeWallFollow(leftClear, rightClear);
                break;
            case Mode.DeadEndTurn:
                (speed01, turnCmd) = ComputeDeadEndTurn(leftClear, rightClear);
                break;
            default:
                (speed01, turnCmd) = ComputeSeekGoal(goalAngle, goalDist, leftClear, rightClear);
                break;
        }

        // Track rapid sign reversals in SeekGoal; if oscillating, escalate to WallFollow
        if (_mode == Mode.SeekGoal && Mathf.Abs(turnCmd) > 0.22f)
        {
            float sign = Mathf.Sign(turnCmd);
            if (_lastSignTurn != 0.0f && sign != _lastSignTurn)
            {
                _signFlipCount++;
            }
            _lastSignTurn = sign;

            if (_signFlipCount >= 5)
            {
                _escapeDir = _avoidDir;
                _mode = Mode.WallFollow;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
                _signFlipCount = 0;
                (speed01, turnCmd) = ComputeWallFollow(leftClear, rightClear);
            }
        }

        // Hard safety overrides to guarantee zero collisions under sensor noise
        if (_fFront < 0.18f)
        {
            speed01 = Mathf.Min(speed01, 0.08f);
            turnCmd = _escapeDir;
        }
        if (_fLeft < 0.15f && turnCmd > 0.0f)
        {
            turnCmd = -0.65f;
            speed01 = Mathf.Min(speed01, 0.22f);
        }
        if (_fRight < 0.15f && turnCmd < 0.0f)
        {
            turnCmd = 0.65f;
            speed01 = Mathf.Min(speed01, 0.22f);
        }
        if (_fLeftSide < 0.12f && turnCmd > 0.0f)
        {
            turnCmd = Mathf.Min(turnCmd, -0.40f);
            speed01 = Mathf.Min(speed01, 0.28f);
        }
        if (_fRightSide < 0.12f && turnCmd < 0.0f)
        {
            turnCmd = Mathf.Max(turnCmd, 0.40f);
            speed01 = Mathf.Min(speed01, 0.28f);
        }

        speed01 = Mathf.Clamp01(speed01);
        turnCmd = Mathf.Clamp(turnCmd, -1.0f, 1.0f);
        return (speed01, turnCmd);
    }

    private void SwitchToSeek()
    {
        _mode = Mode.SeekGoal;
        _modeTimer = 0.0f;
        _lastSignTurn = 0.0f;
        _signFlipCount = 0;
    }

    private void UpdateFilteredSensors(Observation obs)
    {
        _fFront = FilterSensor(obs.front, _fFront, ref _noDetFront);
        _fLeft = FilterSensor(obs.left, _fLeft, ref _noDetLeft);
        _fRight = FilterSensor(obs.right, _fRight, ref _noDetRight);
        _fLeftSide = FilterSensor(obs.leftSide, _fLeftSide, ref _noDetLeftSide);
        _fRightSide = FilterSensor(obs.rightSide, _fRightSide, ref _noDetRightSide);
    }

    private static float FilterSensor(float raw, float prevFiltered, ref int noDetCount)
    {
        float clamped = Mathf.Clamp(raw, 0.02f, NoDetectValue);
        bool isNoDetect = clamped >= NoDetectValue - 0.001f;

        if (isNoDetect)
        {
            noDetCount++;
            if (prevFiltered < 1.2f && noDetCount < 3)
            {
                return Mathf.Max(0.02f, prevFiltered + 0.04f);
            }
            if (prevFiltered < 2.5f && noDetCount < 2)
            {
                return Mathf.Max(0.02f, prevFiltered + 0.10f);
            }
            return NoDetectValue;
        }

        noDetCount = 0;

        if (clamped < prevFiltered)
        {
            float alpha = clamped < 0.45f ? 0.75f : 0.55f;
            return Mathf.Lerp(prevFiltered, clamped, alpha);
        }

        return Mathf.Lerp(prevFiltered, clamped, 0.30f);
    }

    private float ChooseTurnDirection(float goalAngleDeg, float leftClear, float rightClear, bool flipPrevious)
    {
        if (flipPrevious)
        {
            return -_escapeDir;
        }

        float diff = leftClear - rightClear;
        if (Mathf.Abs(diff) > 0.18f)
        {
            return diff > 0.0f ? 1.0f : -1.0f;
        }
        if (Mathf.Abs(goalAngleDeg) > 20.0f)
        {
            return goalAngleDeg > 0.0f ? 1.0f : -1.0f;
        }
        return _escapeDir != 0.0f ? _escapeDir : (leftClear >= rightClear ? 1.0f : -1.0f);
    }

    private bool IsDirectionClear(float angleDeg)
    {
        if (angleDeg > 22.0f)
        {
            return _fLeft > 0.60f && _fLeftSide > 0.26f;
        }
        if (angleDeg < -22.0f)
        {
            return _fRight > 0.60f && _fRightSide > 0.26f;
        }
        return _fFront > FrontSlowDist && _fLeft > 0.44f && _fRight > 0.44f;
    }

    private (float speed01, float turn) ComputeSeekGoal(
        float goalAngleDeg,
        float goalDist,
        float leftClear,
        float rightClear)
    {
        // Wide hysteresis on _avoidDir so noisy readings don't flip avoidance side mid-maneuver
        if (leftClear - rightClear > 0.22f)
        {
            _avoidDir = 1.0f;
        }
        else if (rightClear - leftClear > 0.22f)
        {
            _avoidDir = -1.0f;
        }

        float goalTurn = Mathf.Clamp(goalAngleDeg / 38.0f, -1.0f, 1.0f);

        float leftRepulsion = 0.0f;
        if (_fLeft < 0.52f)
        {
            leftRepulsion += (0.52f - _fLeft) / 0.52f;
        }
        if (_fLeftSide < SideComfortDist)
        {
            leftRepulsion += 0.70f * ((SideComfortDist - _fLeftSide) / SideComfortDist);
        }

        float rightRepulsion = 0.0f;
        if (_fRight < 0.52f)
        {
            rightRepulsion += (0.52f - _fRight) / 0.52f;
        }
        if (_fRightSide < SideComfortDist)
        {
            rightRepulsion += 0.70f * ((SideComfortDist - _fRightSide) / SideComfortDist);
        }

        float obstacleBias = (rightRepulsion - leftRepulsion) * 0.65f;

        float frontUrgency = _fFront < FrontSlowDist
            ? Mathf.Clamp01((FrontSlowDist - _fFront) / (FrontSlowDist - FrontStopDist))
            : 0.0f;

        float turn = Mathf.Lerp(goalTurn + obstacleBias, _avoidDir, frontUrgency * 0.65f);

        float minSide = Mathf.Min(_fLeft, Mathf.Min(_fRight, Mathf.Min(_fLeftSide, _fRightSide)));
        float clearanceFactor = Mathf.Clamp01((Mathf.Min(_fFront, minSide * 1.5f) - 0.16f) / 0.54f);
        float turnFactor = Mathf.Clamp01(1.0f - 0.45f * Mathf.Abs(turn));
        float goalFactor = goalDist < 0.30f ? Mathf.Clamp(goalDist / 0.30f, 0.25f, 1.0f) : 1.0f;

        float speed01 = Mathf.Lerp(0.18f, 1.0f, clearanceFactor) * turnFactor * goalFactor;
        if (Mathf.Abs(goalAngleDeg) > 115.0f && _fFront > 0.65f)
        {
            speed01 = Mathf.Min(speed01, 0.35f);
        }

        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeAvoidTurn(
        float goalAngleDeg,
        float leftClear,
        float rightClear)
    {
        float turn = _escapeDir * 0.92f;

        // Only blend toward goal once front is clearly open and goal lies on the same side we are turning
        if (_fFront > FrontSlowDist * 0.85f &&
            Mathf.Sign(goalAngleDeg) == _escapeDir &&
            Mathf.Abs(goalAngleDeg) < 50.0f &&
            _modeTimer > 1.0f)
        {
            turn = Mathf.Lerp(_escapeDir * 0.92f, Mathf.Clamp(goalAngleDeg / 38.0f, -1.0f, 1.0f), 0.25f);
        }

        float minClear = Mathf.Min(_fFront, Mathf.Min(leftClear, rightClear));
        float speed01 = minClear < 0.25f ? 0.18f : 0.44f;
        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeWallFollow(float leftClear, float rightClear)
    {
        float sideDist = _escapeDir > 0.0f
            ? Mathf.Min(_fRightSide, _fRight * 0.9f)
            : Mathf.Min(_fLeftSide, _fLeft * 0.9f);

        float diagDist = _escapeDir > 0.0f ? _fRight : _fLeft;

        if (_fFront < FrontStopDist * 1.18f)
        {
            float tightFactor = Mathf.Clamp01((FrontStopDist * 1.18f - _fFront) / 0.20f);
            float speed = Mathf.Lerp(0.36f, 0.16f, tightFactor);
            return (speed, _escapeDir);
        }

        float error = SideComfortDist - sideDist;
        float wallSteer = Mathf.Clamp(error * 2.5f, -0.65f, 0.75f) * _escapeDir;

        if (diagDist < 0.30f)
        {
            wallSteer = _escapeDir * 0.85f;
        }
        else if (sideDist > SideComfortDist + 0.25f && _fFront > 0.58f)
        {
            wallSteer = -_escapeDir * 0.35f;
        }

        float minClear = Mathf.Min(_fFront, Mathf.Min(leftClear, rightClear));
        float speed01 = minClear < 0.25f ? 0.22f : Mathf.Clamp(_fFront * 0.85f, 0.25f, 0.60f);
        return (speed01, wallSteer);
    }

    private (float speed01, float turn) ComputeDeadEndTurn(float leftClear, float rightClear)
    {
        float minClear = Mathf.Min(_fFront, Mathf.Min(leftClear, rightClear));
        float speed01 = minClear < 0.24f ? 0.12f : 0.28f;
        return (speed01, _escapeDir);
    }
}
