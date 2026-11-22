using System;
using UnityEngine;

public class LLM_Gemini_4_FB : IRoverController
{
    private enum Mode
    {
        GoalSeek,
        FollowLeft,
        FollowRight
    }

    private const float SensorMaxRange = 4.0f;

    // Filtered sensor readings
    private float _fFront;
    private float _fLeft;
    private float _fRight;
    private float _fLeftSide;
    private float _fRightSide;
    private bool _initialized;

    // Navigation & anti-oscillation state
    private Mode _mode;
    private int _modeTicks;
    private int _clearTicks;
    private float _windowGoalDist;
    private int _windowTicks;
    private float _wallEntryGoalDist;
    private int _sideSwitches;

    // Anti-oscillation tracking
    private int _avoidBiasSign;      // -1 = prefer right turn, +1 = prefer left turn, 0 = none
    private int _avoidBiasTicks;
    private float _prevRawTurn;
    private int _turnReversals;
    private int _reversalWindowTicks;
    private int _commitTicks;
    private float _commitTurn;
    private float _smoothedTurn;

    public string Name => "LLM_Controller";

    public void ResetController()
    {
        _fFront = SensorMaxRange;
        _fLeft = SensorMaxRange;
        _fRight = SensorMaxRange;
        _fLeftSide = SensorMaxRange;
        _fRightSide = SensorMaxRange;
        _initialized = false;

        _mode = Mode.GoalSeek;
        _modeTicks = 0;
        _clearTicks = 0;
        _windowGoalDist = float.MaxValue;
        _windowTicks = 0;
        _wallEntryGoalDist = float.MaxValue;
        _sideSwitches = 0;

        _avoidBiasSign = 0;
        _avoidBiasTicks = 0;
        _prevRawTurn = 0f;
        _turnReversals = 0;
        _reversalWindowTicks = 0;
        _commitTicks = 0;
        _commitTurn = 0f;
        _smoothedTurn = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTicks++;
        _windowTicks++;

        if (_avoidBiasTicks > 0)
        {
            _avoidBiasTicks--;
            if (_avoidBiasTicks == 0)
            {
                _avoidBiasSign = 0;
            }
        }

        UpdateFilteredSensors(obs);

        // Rolling 3.0s (60-tick) progress window
        if (obs.goalDistance < _windowGoalDist - 0.07f)
        {
            _windowGoalDist = obs.goalDistance;
            _windowTicks = 0;
        }
        else if (_windowTicks >= 60)
        {
            _windowGoalDist = obs.goalDistance;
            _windowTicks = 0;
        }

        float minForward = Mathf.Min(_fFront, Mathf.Min(_fLeft, _fRight));
        float minAny = Mathf.Min(minForward, Mathf.Min(_fLeftSide, _fRightSide));

        bool facingGoal = Mathf.Abs(obs.goalAngleDeg) < 55f;
        bool blockedTowardGoal = facingGoal && (_fFront < 0.65f || minForward < 0.48f);
        bool stalledInArena = (_windowTicks >= 52 && minAny < 1.6f);

        if (_mode == Mode.GoalSeek)
        {
            if (blockedTowardGoal || stalledInArena)
            {
                Mode chosen = ChooseInitialWallSide(obs);
                EnterWallFollow(chosen, obs.goalDistance, resetSwitches: true);
            }
        }
        else
        {
            bool sideTowardGoalOpen = obs.goalAngleDeg >= 0f
                ? (_fLeft > 0.68f && _fLeftSide > 0.35f)
                : (_fRight > 0.68f && _fRightSide > 0.35f);

            bool currentlyClear = _modeTicks > 45 &&
                                  _fFront > 0.80f &&
                                  minForward > 0.58f &&
                                  sideTowardGoalOpen &&
                                  Mathf.Abs(obs.goalAngleDeg) < 50f &&
                                  obs.goalDistance <= _wallEntryGoalDist + 0.25f;

            if (currentlyClear)
            {
                _clearTicks++;
            }
            else
            {
                _clearTicks = 0;
            }

            if (_clearTicks >= 12)
            {
                _mode = Mode.GoalSeek;
                _modeTicks = 0;
                _clearTicks = 0;
                _avoidBiasSign = 0;
                _avoidBiasTicks = 0;
                _windowGoalDist = obs.goalDistance;
                _windowTicks = 0;
            }
            else if (_modeTicks > 380)
            {
                if (_sideSwitches < 1)
                {
                    _sideSwitches++;
                    Mode next = (_mode == Mode.FollowLeft) ? Mode.FollowRight : Mode.FollowLeft;
                    EnterWallFollow(next, Mathf.Min(_wallEntryGoalDist, obs.goalDistance), resetSwitches: false);
                }
                else
                {
                    _mode = Mode.GoalSeek;
                    _modeTicks = 0;
                    _clearTicks = 0;
                    _sideSwitches = 0;
                    _windowGoalDist = obs.goalDistance;
                    _windowTicks = 0;
                }
            }
        }

        float desiredSpeed01;
        float desiredTurn;

        if (_mode == Mode.GoalSeek)
        {
            ComputeGoalSeekCommand(obs, out desiredSpeed01, out desiredTurn);
        }
        else
        {
            ComputeWallFollowCommand(_mode == Mode.FollowLeft, out desiredSpeed01, out desiredTurn);
        }

        // Detect repeated left/right turn reversals (oscillation) and lock a steady turn to break out
        UpdateOscillationGuard(desiredTurn, minForward, obs);
        if (_commitTicks > 0)
        {
            _commitTicks--;
            desiredTurn = _commitTurn;
            desiredSpeed01 = Mathf.Min(desiredSpeed01, _fFront < 0.36f ? 0.08f : 0.25f);
        }

        // Close-range collision guard
        if (_fFront < 0.24f || (_fLeft < 0.18f && _fRight < 0.18f))
        {
            desiredSpeed01 = 0f;
            if (Mathf.Abs(desiredTurn) < 0.85f)
            {
                desiredTurn = (_fLeft >= _fRight) ? 1f : -1f;
            }
        }
        else if (_fFront < 0.35f)
        {
            desiredSpeed01 = Mathf.Min(desiredSpeed01, 0.10f);
        }

        // Moderate smoothing so noise doesn't chatter, while keeping turns decisive
        float alpha = (_commitTicks > 0 || Mathf.Abs(desiredTurn) > 0.85f) ? 0.75f : 0.50f;
        _smoothedTurn = Mathf.Lerp(_smoothedTurn, desiredTurn, alpha);

        return (Mathf.Clamp01(desiredSpeed01), Mathf.Clamp(_smoothedTurn, -1f, 1f));
    }

    private void UpdateOscillationGuard(float rawTurn, float minForward, Observation obs)
    {
        _reversalWindowTicks++;
        if (_reversalWindowTicks > 25)
        {
            _turnReversals = 0;
            _reversalWindowTicks = 0;
        }

        if (Mathf.Abs(rawTurn) > 0.25f && Mathf.Abs(_prevRawTurn) > 0.25f)
        {
            if (Mathf.Sign(rawTurn) != Mathf.Sign(_prevRawTurn))
            {
                _turnReversals++;
                _reversalWindowTicks = 0;
            }
        }
        _prevRawTurn = rawTurn;

        // If we reverse sign 4+ times within ~1.25s while obstacles are near or no progress is made, lock a turn
        if (_turnReversals >= 4 && _commitTicks == 0)
        {
            _turnReversals = 0;
            _commitTicks = 22; // ~1.1s committed turn

            float leftSpace = _fLeft + 0.6f * _fLeftSide;
            float rightSpace = _fRight + 0.6f * _fRightSide;
            if (Mathf.Abs(leftSpace - rightSpace) > 0.08f)
            {
                _commitTurn = (leftSpace > rightSpace) ? 0.85f : -0.85f;
            }
            else
            {
                _commitTurn = (obs.goalAngleDeg >= 0f) ? 0.80f : -0.80f;
            }

            if (_mode == Mode.GoalSeek && minForward < 0.65f)
            {
                Mode wf = (_commitTurn > 0f) ? Mode.FollowRight : Mode.FollowLeft;
                EnterWallFollow(wf, obs.goalDistance, resetSwitches: false);
            }
        }
    }

    private void UpdateFilteredSensors(Observation obs)
    {
        float rFront = SanitizeSensor(obs.front);
        float rLeft = SanitizeSensor(obs.left);
        float rRight = SanitizeSensor(obs.right);
        float rLeftSide = SanitizeSensor(obs.leftSide);
        float rRightSide = SanitizeSensor(obs.rightSide);

        if (!_initialized)
        {
            _fFront = rFront;
            _fLeft = rLeft;
            _fRight = rRight;
            _fLeftSide = rLeftSide;
            _fRightSide = rRightSide;
            _initialized = true;
            return;
        }

        _fFront = FilterChannel(_fFront, rFront);
        _fLeft = FilterChannel(_fLeft, rLeft);
        _fRight = FilterChannel(_fRight, rRight);
        _fLeftSide = FilterChannel(_fLeftSide, rLeftSide);
        _fRightSide = FilterChannel(_fRightSide, rRightSide);
    }

    private static float SanitizeSensor(float raw)
    {
        if (float.IsNaN(raw) || float.IsInfinity(raw))
        {
            return SensorMaxRange;
        }
        return Mathf.Clamp(raw, 0.02f, SensorMaxRange);
    }

    private static float FilterChannel(float prev, float current)
    {
        if (current >= SensorMaxRange - 0.01f && prev < 1.60f)
        {
            return Mathf.Min(SensorMaxRange, prev + 0.025f);
        }

        float alpha = (current < prev) ? 0.72f : 0.38f;
        return Mathf.Lerp(prev, current, alpha);
    }

    private Mode ChooseInitialWallSide(Observation obs)
    {
        float leftClear = Mathf.Min(_fLeft, _fLeftSide);
        float rightClear = Mathf.Min(_fRight, _fRightSide);

        if (obs.goalAngleDeg > 20f && leftClear > 0.45f)
        {
            return Mode.FollowRight;
        }
        if (obs.goalAngleDeg < -20f && rightClear > 0.45f)
        {
            return Mode.FollowLeft;
        }

        return (leftClear <= rightClear) ? Mode.FollowLeft : Mode.FollowRight;
    }

    private void EnterWallFollow(Mode mode, float goalDist, bool resetSwitches)
    {
        _mode = mode;
        _modeTicks = 0;
        _clearTicks = 0;
        _wallEntryGoalDist = goalDist;
        _windowGoalDist = goalDist;
        _windowTicks = 0;
        _avoidBiasSign = 0;
        _avoidBiasTicks = 0;
        if (resetSwitches)
        {
            _sideSwitches = 0;
        }
    }

    private void ComputeGoalSeekCommand(Observation obs, out float speed01, out float turn)
    {
        float goalTurn = Mathf.Clamp(obs.goalAngleDeg / 38f, -1f, 1f);

        float leftThreat = ThreatWeight(_fLeft, 0.78f) * 0.90f + ThreatWeight(_fLeftSide, 0.50f) * 0.70f;
        float rightThreat = ThreatWeight(_fRight, 0.78f) * 0.90f + ThreatWeight(_fRightSide, 0.50f) * 0.70f;
        float frontThreat = ThreatWeight(_fFront, 0.92f);

        // Cancel locked avoidance bias early if the favored direction becomes tightly blocked
        if (_avoidBiasSign > 0 && (_fLeft < 0.34f || _fLeftSide < 0.24f))
        {
            _avoidBiasSign = 0;
            _avoidBiasTicks = 0;
        }
        else if (_avoidBiasSign < 0 && (_fRight < 0.34f || _fRightSide < 0.24f))
        {
            _avoidBiasSign = 0;
            _avoidBiasTicks = 0;
        }

        if (frontThreat > 0.22f && _avoidBiasSign == 0)
        {
            float leftSpace = _fLeft + 0.5f * _fLeftSide;
            float rightSpace = _fRight + 0.5f * _fRightSide;
            if (Mathf.Abs(leftSpace - rightSpace) < 0.08f)
            {
                _avoidBiasSign = (obs.goalAngleDeg >= 0f) ? 1 : -1;
            }
            else
            {
                _avoidBiasSign = (leftSpace > rightSpace) ? 1 : -1;
            }
            _avoidBiasTicks = 24; // Hold turn bias ~1.2s to avoid left/right flip-flopping
        }

        float obstacleTurn = (rightThreat - leftThreat) + (_avoidBiasSign * frontThreat * 0.85f);
        float goalWeight = 1f - 0.70f * frontThreat;

        turn = Mathf.Clamp(goalTurn * goalWeight + obstacleTurn, -1f, 1f);

        float turnFactor = 1f - 0.60f * Mathf.Abs(turn);
        float frontFactor = Mathf.Clamp01((_fFront - 0.22f) / 0.75f);
        float minSideFactor = Mathf.Clamp01((Mathf.Min(_fLeft, _fRight) - 0.16f) / 0.45f);
        float goalApproachFactor = obs.goalDistance < 0.25f
            ? Mathf.Clamp(obs.goalDistance / 0.25f, 0.25f, 1f)
            : 1f;

        speed01 = Mathf.Clamp01(turnFactor * frontFactor * minSideFactor * goalApproachFactor);
    }

    private void ComputeWallFollowCommand(bool followLeft, out float speed01, out float turn)
    {
        const float targetWallDist = 0.28f;
        const float deadband = 0.04f;

        if (followLeft)
        {
            if (_fFront < 0.45f || _fLeft < 0.28f)
            {
                turn = -0.95f;
                speed01 = (_fFront < 0.32f) ? 0.03f : 0.14f;
            }
            else if (_fLeftSide > 0.90f && _fLeft > 0.90f && _fRightSide > 0.35f)
            {
                turn = 0.55f;
                speed01 = 0.36f;
            }
            else
            {
                float sideDist = Mathf.Min(_fLeftSide, _fLeft + 0.06f);
                float error = targetWallDist - sideDist;
                if (Mathf.Abs(error) < deadband)
                {
                    error = 0f;
                }
                turn = Mathf.Clamp(-error * 2.4f, -0.75f, 0.75f);
                speed01 = Mathf.Clamp01(0.48f * Mathf.Clamp01((_fFront - 0.26f) / 0.65f));
            }

            if (_fRightSide < 0.22f || _fRight < 0.22f)
            {
                turn += 0.45f;
            }
        }
        else
        {
            if (_fFront < 0.45f || _fRight < 0.28f)
            {
                turn = 0.95f;
                speed01 = (_fFront < 0.32f) ? 0.03f : 0.14f;
            }
            else if (_fRightSide > 0.90f && _fRight > 0.90f && _fLeftSide > 0.35f)
            {
                turn = -0.55f;
                speed01 = 0.36f;
            }
            else
            {
                float sideDist = Mathf.Min(_fRightSide, _fRight + 0.06f);
                float error = targetWallDist - sideDist;
                if (Mathf.Abs(error) < deadband)
                {
                    error = 0f;
                }
                turn = Mathf.Clamp(error * 2.4f, -0.75f, 0.75f);
                speed01 = Mathf.Clamp01(0.48f * Mathf.Clamp01((_fFront - 0.26f) / 0.65f));
            }

            if (_fLeftSide < 0.22f || _fLeft < 0.22f)
            {
                turn -= 0.45f;
            }
        }

        turn = Mathf.Clamp(turn, -1f, 1f);
        speed01 = Mathf.Max(0f, speed01);
    }

    private static float ThreatWeight(float dist, float influenceRadius)
    {
        if (dist >= influenceRadius)
        {
            return 0f;
        }
        float t = (influenceRadius - dist) / influenceRadius;
        return Mathf.Clamp01(t * t);
    }
}
