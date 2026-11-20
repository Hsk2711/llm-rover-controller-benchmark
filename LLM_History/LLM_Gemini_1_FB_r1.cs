using System;
using UnityEngine;

public class LLM_Gemini_1_FB : IRoverController
{
    private enum Mode
    {
        SeekGoal,
        AvoidTurn,
        WallFollow
    }

    private const float Dt = 0.05f;
    private const float MaxSpeedMps = 0.30f;
    private const float NoDetectValue = 4.0f;

    // Rover dimensions: 0.20 m long x 0.15 m wide
    private const float FrontStopDist = 0.30f;
    private const float FrontSlowDist = 0.70f;
    private const float SideDangerDist = 0.21f;
    private const float SideComfortDist = 0.35f;

    private Mode _mode;

    // Filtered sensor estimates (in metres)
    private float _fFront;
    private float _fLeft;
    private float _fRight;
    private float _fLeftSide;
    private float _fRightSide;

    // Consecutive "no-detection" (4.0) counters
    private int _noDetFront;
    private int _noDetLeft;
    private int _noDetRight;
    private int _noDetLeftSide;
    private int _noDetRightSide;

    // Committed escape / wall-follow state
    private float _escapeDir;         // +1.0 = left, -1.0 = right
    private float _avoidDir;          // hysteresis-locked avoidance direction in SeekGoal
    private float _modeTimer;         // time in current mode
    private float _seekLockTimer;     // minimum time to stay in SeekGoal after exiting obstacle mode
    private float _prevTurn;          // smoothed turn output to prevent chatter
    private float _prevTurnSign;      // sign of previous turn to detect sign oscillation
    private float _oscillationTimer;  // accumulates when turn direction flips rapidly

    // Progress / stall detection
    private float _bestGoalDist;
    private float _sinceProgressTimer;
    private float _goalDistAtModeStart;

    public string Name => "AntiOscillationSensorNavigator";

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
        _seekLockTimer = 0.0f;
        _prevTurn = 0.0f;
        _prevTurnSign = 0.0f;
        _oscillationTimer = 0.0f;

        _bestGoalDist = float.MaxValue;
        _sinceProgressTimer = 0.0f;
        _goalDistAtModeStart = float.MaxValue;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTimer += Dt;
        if (_seekLockTimer > 0.0f)
        {
            _seekLockTimer = Mathf.Max(0.0f, _seekLockTimer - Dt);
        }

        UpdateFilteredSensors(obs);

        float goalAngle = Mathf.Clamp(obs.goalAngleDeg, -180.0f, 180.0f);
        float goalDist = Mathf.Max(0.0f, obs.goalDistance);

        // Track meaningful progress toward goal over time
        if (goalDist < _bestGoalDist - 0.05f)
        {
            _bestGoalDist = goalDist;
            _sinceProgressTimer = 0.0f;
        }
        else
        {
            _sinceProgressTimer += Dt;
        }

        float leftClear = Mathf.Min(_fLeft, _fLeftSide * 1.05f);
        float rightClear = Mathf.Min(_fRight, _fRightSide * 1.05f);

        bool blockedAhead = _fFront < FrontStopDist ||
                            (_fLeft < 0.24f && Mathf.Abs(goalAngle) < 60.0f) ||
                            (_fRight < 0.24f && Mathf.Abs(goalAngle) < 60.0f);

        bool tightCorridor = (_fLeftSide < SideDangerDist && _fRightSide < SideDangerDist) ||
                             (_fFront < 0.45f && leftClear < SideComfortDist && rightClear < SideComfortDist);

        bool stalledOrOscillating = _sinceProgressTimer > 4.5f || _oscillationTimer > 1.2f;

        // State transitions with strong anti-oscillation hysteresis
        if (_mode == Mode.SeekGoal)
        {
            if (blockedAhead || stalledOrOscillating)
            {
                _escapeDir = ChooseEscapeDirection(goalAngle, leftClear, rightClear, stalledOrOscillating);
                _mode = (tightCorridor || stalledOrOscillating) ? Mode.WallFollow : Mode.AvoidTurn;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
                _oscillationTimer = 0.0f;
                if (stalledOrOscillating)
                {
                    _sinceProgressTimer = 0.0f;
                }
            }
        }
        else if (_mode == Mode.AvoidTurn)
        {
            bool hasMadeGoalProgress = goalDist < _goalDistAtModeStart - 0.12f;
            bool frontClearEnough = _fFront > FrontSlowDist && _fLeft > 0.36f && _fRight > 0.36f;
            bool goalFacingClear = IsDirectionClear(goalAngle) && Mathf.Abs(goalAngle) < 55.0f;

            // Require at least 0.65 s of committed turn before re-entering SeekGoal
            if (_modeTimer >= 0.65f && frontClearEnough && (goalFacingClear || (hasMadeGoalProgress && _modeTimer >= 1.1f)))
            {
                EnterSeekGoal();
            }
            else if (_modeTimer >= 2.6f)
            {
                // Escalate to sustained wall-following to clear corners/U-shapes cleanly
                _mode = Mode.WallFollow;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
            }
        }
        else if (_mode == Mode.WallFollow)
        {
            bool goalDirectionClear = IsDirectionClear(goalAngle) && Mathf.Abs(goalAngle) < 70.0f;
            bool improvedGoal = goalDist < _goalDistAtModeStart - 0.15f;

            // Commit to wall-following for at least 1.5 s so we don't flip back at the mouth of a pocket
            if (_modeTimer >= 1.5f && _fFront > FrontSlowDist && goalDirectionClear && (improvedGoal || _modeTimer >= 2.8f))
            {
                EnterSeekGoal();
            }
            else if (_modeTimer >= 8.5f)
            {
                // Flip wall side once if a long follow hasn't broken out, then keep trying
                _escapeDir = -_escapeDir;
                _modeTimer = 0.0f;
                _goalDistAtModeStart = goalDist;
            }
        }

        float rawSpeed01;
        float rawTurn;

        if (_mode == Mode.SeekGoal)
        {
            (rawSpeed01, rawTurn) = ComputeSeekCommand(goalAngle, goalDist, leftClear, rightClear);
        }
        else if (_mode == Mode.AvoidTurn)
        {
            (rawSpeed01, rawTurn) = ComputeAvoidTurnCommand(goalAngle, leftClear, rightClear);
        }
        else
        {
            (rawSpeed01, rawTurn) = ComputeWallFollowCommand(leftClear, rightClear);
        }

        // Apply turn damping to prevent rapid sign/magnitude chatter from sensor noise
        float blend = _mode == Mode.SeekGoal ? 0.55f : 0.70f;
        float turnCmd = Mathf.Lerp(_prevTurn, rawTurn, blend);
        float speed01 = rawSpeed01;

        // Track sign flips to detect oscillation early
        float turnSign = Mathf.Abs(turnCmd) > 0.15f ? Mathf.Sign(turnCmd) : 0.0f;
        if (turnSign != 0.0f && _prevTurnSign != 0.0f && turnSign != _prevTurnSign && _mode == Mode.SeekGoal)
        {
            _oscillationTimer += Dt * 2.5f;
        }
        else
        {
            _oscillationTimer = Mathf.Max(0.0f, _oscillationTimer - Dt * 0.4f);
        }
        if (turnSign != 0.0f)
        {
            _prevTurnSign = turnSign;
        }

        // Hard emergency safety overrides
        if (_fFront < 0.17f)
        {
            speed01 = Mathf.Min(speed01, 0.10f);
            turnCmd = _escapeDir;
        }
        if (_fLeft < 0.14f && turnCmd > 0.0f)
        {
            turnCmd = -0.55f;
            speed01 = Mathf.Min(speed01, 0.25f);
        }
        if (_fRight < 0.14f && turnCmd < 0.0f)
        {
            turnCmd = 0.55f;
            speed01 = Mathf.Min(speed01, 0.25f);
        }
        if (_fLeftSide < 0.11f && turnCmd > 0.0f)
        {
            turnCmd = Mathf.Min(turnCmd, -0.35f);
            speed01 = Mathf.Min(speed01, 0.30f);
        }
        if (_fRightSide < 0.11f && turnCmd < 0.0f)
        {
            turnCmd = Mathf.Max(turnCmd, 0.35f);
            speed01 = Mathf.Min(speed01, 0.30f);
        }

        speed01 = Mathf.Clamp01(speed01);
        turnCmd = Mathf.Clamp(turnCmd, -1.0f, 1.0f);
        _prevTurn = turnCmd;

        return (speed01, turnCmd);
    }

    private void EnterSeekGoal()
    {
        _mode = Mode.SeekGoal;
        _modeTimer = 0.0f;
        _seekLockTimer = 0.75f;
        _oscillationTimer = 0.0f;
        _sinceProgressTimer = 0.0f;
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
                return Mathf.Max(0.02f, prevFiltered + 0.12f);
            }
            return NoDetectValue;
        }

        noDetCount = 0;

        if (clamped < prevFiltered)
        {
            float alpha = clamped < 0.45f ? 0.75f : 0.55f;
            return Mathf.Lerp(prevFiltered, clamped, alpha);
        }

        return Mathf.Lerp(prevFiltered, clamped, 0.32f);
    }

    private float ChooseEscapeDirection(float goalAngleDeg, float leftClear, float rightClear, bool forceSwitchOnStall)
    {
        if (forceSwitchOnStall && _modeTimer > 1.5f)
        {
            return -_escapeDir;
        }

        float diff = leftClear - rightClear;
        if (Mathf.Abs(diff) > 0.14f)
        {
            return diff > 0.0f ? 1.0f : -1.0f;
        }
        if (Mathf.Abs(goalAngleDeg) > 15.0f)
        {
            return goalAngleDeg > 0.0f ? 1.0f : -1.0f;
        }
        return _escapeDir;
    }

    private bool IsDirectionClear(float angleDeg)
    {
        if (angleDeg > 20.0f)
        {
            return _fLeft > 0.58f && _fLeftSide > 0.25f;
        }
        if (angleDeg < -20.0f)
        {
            return _fRight > 0.58f && _fRightSide > 0.25f;
        }
        return _fFront > FrontSlowDist && _fLeft > 0.42f && _fRight > 0.42f;
    }

    private (float speed01, float turn) ComputeSeekCommand(
        float goalAngleDeg,
        float goalDist,
        float leftClear,
        float rightClear)
    {
        float goalTurn = Mathf.Clamp(goalAngleDeg / 35.0f, -1.0f, 1.0f);

        // Smooth obstacle repulsion without sharp step discontinuities
        float leftRepulsion = 0.0f;
        if (_fLeft < 0.55f)
        {
            leftRepulsion += (0.55f - _fLeft) / 0.55f;
        }
        if (_fLeftSide < SideComfortDist)
        {
            leftRepulsion += 0.75f * ((SideComfortDist - _fLeftSide) / SideComfortDist);
        }

        float rightRepulsion = 0.0f;
        if (_fRight < 0.55f)
        {
            rightRepulsion += (0.55f - _fRight) / 0.55f;
        }
        if (_fRightSide < SideComfortDist)
        {
            rightRepulsion += 0.75f * ((SideComfortDist - _fRightSide) / SideComfortDist);
        }

        float obstacleBias = (rightRepulsion - leftRepulsion) * 0.75f;

        // Hysteresis on _avoidDir so noisy readings don't flip avoidance side back and forth
        if (leftClear - rightClear > 0.14f)
        {
            _avoidDir = 1.0f;
        }
        else if (rightClear - leftClear > 0.14f)
        {
            _avoidDir = -1.0f;
        }

        float frontUrgency = _fFront < FrontSlowDist
            ? Mathf.Clamp01((FrontSlowDist - _fFront) / (FrontSlowDist - FrontStopDist))
            : 0.0f;

        // Immediately after exiting obstacle avoidance, damp goal pull slightly if nearly opposite to escapeDir
        float postExitScale = (_seekLockTimer > 0.0f && Mathf.Sign(goalAngleDeg) != _escapeDir && Mathf.Abs(goalAngleDeg) > 45.0f)
            ? 0.45f
            : 1.0f;

        float turn = Mathf.Lerp(goalTurn * postExitScale + obstacleBias, _avoidDir, frontUrgency * 0.70f);

        float minSide = Mathf.Min(_fLeft, Mathf.Min(_fRight, Mathf.Min(_fLeftSide, _fRightSide)));
        float clearanceFactor = Mathf.Clamp01((Mathf.Min(_fFront, minSide * 1.5f) - 0.15f) / 0.55f);
        float turnFactor = Mathf.Clamp01(1.0f - 0.50f * Mathf.Abs(turn));
        float goalFactor = goalDist < 0.30f ? Mathf.Clamp(goalDist / 0.30f, 0.25f, 1.0f) : 1.0f;

        float speed01 = Mathf.Lerp(0.16f, 1.0f, clearanceFactor) * turnFactor * goalFactor;
        if (Mathf.Abs(goalAngleDeg) > 110.0f && _fFront > 0.6f)
        {
            speed01 = Mathf.Min(speed01, 0.35f);
        }

        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeAvoidTurnCommand(
        float goalAngleDeg,
        float leftClear,
        float rightClear)
    {
        float turn = _escapeDir * 0.90f;

        if (_fFront > FrontStopDist * 1.35f && Mathf.Sign(goalAngleDeg) == _escapeDir && Mathf.Abs(goalAngleDeg) < 65.0f)
        {
            turn = Mathf.Lerp(_escapeDir * 0.90f, Mathf.Clamp(goalAngleDeg / 35.0f, -1.0f, 1.0f), 0.30f);
        }

        float minClear = Mathf.Min(_fFront, Mathf.Min(leftClear, rightClear));
        float speed01 = minClear < 0.24f ? 0.18f : 0.42f;
        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeWallFollowCommand(
        float leftClear,
        float rightClear)
    {
        float sideDist = _escapeDir > 0.0f
            ? Mathf.Min(_fRightSide, _fRight * 0.9f)
            : Mathf.Min(_fLeftSide, _fLeft * 0.9f);

        float diagDist = _escapeDir > 0.0f ? _fRight : _fLeft;

        if (_fFront < FrontStopDist * 1.2f)
        {
            float tightFactor = Mathf.Clamp01((FrontStopDist * 1.2f - _fFront) / 0.20f);
            float speed = Mathf.Lerp(0.38f, 0.16f, tightFactor);
            return (speed, _escapeDir);
        }

        float error = SideComfortDist - sideDist;
        float wallSteer = Mathf.Clamp(error * 2.8f, -0.70f, 0.80f) * _escapeDir;

        if (diagDist < 0.30f)
        {
            wallSteer = _escapeDir * 0.85f;
        }
        else if (sideDist > SideComfortDist + 0.24f && _fFront > 0.55f)
        {
            wallSteer = -_escapeDir * 0.40f;
        }

        float minClear = Mathf.Min(_fFront, Mathf.Min(leftClear, rightClear));
        float speed01 = minClear < 0.24f ? 0.22f : Mathf.Clamp(_fFront * 0.85f, 0.25f, 0.62f);
        return (speed01, wallSteer);
    }
}
