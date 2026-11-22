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

    // Navigation & escape state
    private Mode _mode;
    private int _modeTicks;
    private int _clearTicks;
    private float _windowGoalDist;
    private int _windowTicks;
    private float _wallEntryGoalDist;
    private int _sideSwitches;
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
        _smoothedTurn = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTicks++;
        _windowTicks++;

        UpdateFilteredSensors(obs);

        // Rolling 3.0s (60-tick) progress check so old low distances don't permanently stale out progress
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
            // Check whether the path ahead and toward the goal is genuinely clear
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

            // Require ~0.6s (12 consecutive ticks) of clean clearance before leaving wall-follow
            if (_clearTicks >= 12)
            {
                _mode = Mode.GoalSeek;
                _modeTicks = 0;
                _clearTicks = 0;
                _windowGoalDist = obs.goalDistance;
                _windowTicks = 0;
            }
            else if (_modeTicks > 380)
            {
                // ~19s on current wall side without clearing: switch wall side once, then try GoalSeek
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

        // Close-range collision guard
        if (_fFront < 0.24f || (_fLeft < 0.18f && _fRight < 0.18f))
        {
            desiredSpeed01 = 0f;
            if (Mathf.Abs(desiredTurn) < 0.90f)
            {
                desiredTurn = (_fLeft >= _fRight) ? 1f : -1f;
            }
        }
        else if (_fFront < 0.35f)
        {
            desiredSpeed01 = Mathf.Min(desiredSpeed01, 0.10f);
        }

        // Fast turn response when reversing turn sign or executing sharp escape turns
        float alpha = (Mathf.Sign(desiredTurn) != Mathf.Sign(_smoothedTurn) || Mathf.Abs(desiredTurn) > 0.8f)
            ? 0.85f
            : 0.65f;
        _smoothedTurn = Mathf.Lerp(_smoothedTurn, desiredTurn, alpha);

        return (Mathf.Clamp01(desiredSpeed01), Mathf.Clamp(_smoothedTurn, -1f, 1f));
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
        // Prevent a single false 4.0 reading from erasing a nearby obstacle
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
        if (resetSwitches)
        {
            _sideSwitches = 0;
        }
    }

    private void ComputeGoalSeekCommand(Observation obs, out float speed01, out float turn)
    {
        float goalTurn = Mathf.Clamp(obs.goalAngleDeg / 38f, -1f, 1f);

        float leftThreat = ThreatWeight(_fLeft, 0.78f) * 0.95f + ThreatWeight(_fLeftSide, 0.50f) * 0.75f;
        float rightThreat = ThreatWeight(_fRight, 0.78f) * 0.95f + ThreatWeight(_fRightSide, 0.50f) * 0.75f;
        float frontThreat = ThreatWeight(_fFront, 0.92f);

        float obstacleTurn = rightThreat - leftThreat;

        if (frontThreat > 0.18f)
        {
            float leftSpace = _fLeft + 0.5f * _fLeftSide;
            float rightSpace = _fRight + 0.5f * _fRightSide;
            float biasSign = (Mathf.Abs(leftSpace - rightSpace) < 0.08f)
                ? (obs.goalAngleDeg >= 0f ? 1f : -1f)
                : (leftSpace > rightSpace ? 1f : -1f);

            obstacleTurn += biasSign * frontThreat * 1.0f;
        }

        turn = Mathf.Clamp(goalTurn * (1f - 0.65f * frontThreat) + obstacleTurn, -1f, 1f);

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

        if (followLeft)
        {
            if (_fFront < 0.45f || _fLeft < 0.28f)
            {
                // Obstacle/wall ahead: pivot right cleanly
                turn = -1.0f;
                speed01 = (_fFront < 0.32f) ? 0.03f : 0.14f;
            }
            else if (_fLeftSide > 0.85f && _fLeft > 0.85f && _fRightSide > 0.35f)
            {
                // Left wall ended and right side has room: curve left around corner
                turn = 0.65f;
                speed01 = 0.38f;
            }
            else
            {
                float sideDist = Mathf.Min(_fLeftSide, _fLeft + 0.06f);
                float error = targetWallDist - sideDist;
                turn = Mathf.Clamp(-error * 2.8f, -0.85f, 0.85f);
                speed01 = Mathf.Clamp01(0.50f * Mathf.Clamp01((_fFront - 0.26f) / 0.65f));
            }

            // Keep clear of a wall on the right side (e.g., exiting a U-shape or narrow corridor)
            if (_fRightSide < 0.24f)
            {
                turn += 0.55f;
            }
            if (_fRight < 0.24f)
            {
                turn += 0.55f;
            }
        }
        else
        {
            if (_fFront < 0.45f || _fRight < 0.28f)
            {
                // Obstacle/wall ahead: pivot left cleanly
                turn = 1.0f;
                speed01 = (_fFront < 0.32f) ? 0.03f : 0.14f;
            }
            else if (_fRightSide > 0.85f && _fRight > 0.85f && _fLeftSide > 0.35f)
            {
                // Right wall ended and left side has room: curve right around corner
                turn = -0.65f;
                speed01 = 0.38f;
            }
            else
            {
                float sideDist = Mathf.Min(_fRightSide, _fRight + 0.06f);
                float error = targetWallDist - sideDist;
                turn = Mathf.Clamp(error * 2.8f, -0.85f, 0.85f);
                speed01 = Mathf.Clamp01(0.50f * Mathf.Clamp01((_fFront - 0.26f) / 0.65f));
            }

            // Keep clear of a wall on the left side (e.g., exiting a U-shape or narrow corridor)
            if (_fLeftSide < 0.24f)
            {
                turn -= 0.55f;
            }
            if (_fLeft < 0.24f)
            {
                turn -= 0.55f;
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
