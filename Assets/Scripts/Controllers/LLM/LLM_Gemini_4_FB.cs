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

    // Sensor filter state
    private float _fFront;
    private float _fLeft;
    private float _fRight;
    private float _fLeftSide;
    private float _fRightSide;
    private bool _initialized;

    // Navigation & dead-end escape state
    private Mode _mode;
    private int _modeTicks;
    private float _bestGoalDist;
    private float _wallEntryGoalDist;
    private int _ticksSinceProgress;
    private int _sideFlipsInCurrentTrap;
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
        _bestGoalDist = float.MaxValue;
        _wallEntryGoalDist = float.MaxValue;
        _ticksSinceProgress = 0;
        _sideFlipsInCurrentTrap = 0;
        _smoothedTurn = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTicks++;

        UpdateFilteredSensors(obs);
        UpdateProgressTracking(obs.goalDistance);

        float minForward = Mathf.Min(_fFront, Mathf.Min(_fLeft, _fRight));
        float minAny = Mathf.Min(minForward, Mathf.Min(_fLeftSide, _fRightSide));

        float goalAlignment = Mathf.Cos(obs.goalAngleDeg * Mathf.Deg2Rad);
        bool goalPathBlocked = (goalAlignment > 0.15f && _fFront < 0.62f) ||
                               (obs.goalAngleDeg > 12f && _fLeft < 0.50f) ||
                               (obs.goalAngleDeg < -12f && _fRight < 0.50f);

        if (_mode == Mode.GoalSeek)
        {
            if (goalPathBlocked && minForward < 0.55f)
            {
                EnterWallFollow(ChoosePreferredWallSide(obs), obs.goalDistance, resetFlips: true);
            }
            else if (_ticksSinceProgress > 55 && minAny < 1.5f)
            {
                EnterWallFollow(ChoosePreferredWallSide(obs), obs.goalDistance, resetFlips: true);
            }
        }
        else
        {
            // Only leave wall-following early if we've traveled/turned enough to clear the obstacle,
            // made real progress past where we entered the wall, and the goal path is open.
            bool madeRealProgress = obs.goalDistance < _wallEntryGoalDist - 0.15f;
            bool goalSideClear = obs.goalAngleDeg >= 0f ? _fLeft > 0.62f : _fRight > 0.62f;
            bool canExitToGoal = _modeTicks > 36 &&
                                 madeRealProgress &&
                                 _fFront > 0.72f &&
                                 goalSideClear &&
                                 Mathf.Abs(obs.goalAngleDeg) < 65f;

            if (canExitToGoal)
            {
                _mode = Mode.GoalSeek;
                _modeTicks = 0;
                _ticksSinceProgress = 0;
                _sideFlipsInCurrentTrap = 0;
            }
            else if (_modeTicks > 320)
            {
                // After ~16s on one wall without clearing, flip side once to try escaping the other way
                if (_sideFlipsInCurrentTrap < 1)
                {
                    _sideFlipsInCurrentTrap++;
                    Mode nextSide = (_mode == Mode.FollowLeft) ? Mode.FollowRight : Mode.FollowLeft;
                    EnterWallFollow(nextSide, Mathf.Min(_wallEntryGoalDist, obs.goalDistance), resetFlips: false);
                }
                else
                {
                    _mode = Mode.GoalSeek;
                    _modeTicks = 0;
                    _ticksSinceProgress = 0;
                    _sideFlipsInCurrentTrap = 0;
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

        // Emergency close-proximity guard so the rover never drives into a wall or box
        if (_fFront < 0.23f || (_fLeft < 0.17f && _fRight < 0.17f))
        {
            desiredSpeed01 = 0f;
            if (Mathf.Abs(desiredTurn) < 0.85f)
            {
                desiredTurn = (_fLeft >= _fRight) ? 1f : -1f;
            }
        }
        else if (_fFront < 0.34f)
        {
            desiredSpeed01 = Mathf.Min(desiredSpeed01, 0.16f);
        }

        _smoothedTurn = Mathf.Lerp(_smoothedTurn, desiredTurn, 0.70f);

        float clampedSpeed = Mathf.Clamp01(desiredSpeed01);
        float clampedTurn = Mathf.Clamp(_smoothedTurn, -1f, 1f);

        return (clampedSpeed, clampedTurn);
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
        // Guard against false 4.0 dropouts when a nearby obstacle was recently detected
        if (current >= SensorMaxRange - 0.01f && prev < 1.50f)
        {
            return Mathf.Min(SensorMaxRange, prev + 0.03f);
        }

        float alpha = (current < prev) ? 0.70f : 0.40f;
        return Mathf.Lerp(prev, current, alpha);
    }

    private void UpdateProgressTracking(float goalDistance)
    {
        if (goalDistance < _bestGoalDist - 0.06f)
        {
            _bestGoalDist = goalDistance;
            _ticksSinceProgress = 0;
        }
        else
        {
            _ticksSinceProgress++;
        }
    }

    private Mode ChoosePreferredWallSide(Observation obs)
    {
        float leftClearance = Mathf.Min(_fLeft, _fLeftSide);
        float rightClearance = Mathf.Min(_fRight, _fRightSide);

        // If goal is to the left and front-left has room, hugging the right wall helps swing left around obstacles
        if (obs.goalAngleDeg > 25f && leftClearance > 0.42f && _fRightSide < 1.2f)
        {
            return Mode.FollowRight;
        }
        if (obs.goalAngleDeg < -25f && rightClearance > 0.42f && _fLeftSide < 1.2f)
        {
            return Mode.FollowLeft;
        }

        return (leftClearance <= rightClearance) ? Mode.FollowLeft : Mode.FollowRight;
    }

    private void EnterWallFollow(Mode targetMode, float currentGoalDist, bool resetFlips)
    {
        _mode = targetMode;
        _modeTicks = 0;
        _ticksSinceProgress = 0;
        _wallEntryGoalDist = currentGoalDist;
        if (resetFlips)
        {
            _sideFlipsInCurrentTrap = 0;
        }
    }

    private void ComputeGoalSeekCommand(Observation obs, out float speed01, out float turn)
    {
        float goalTurn = Mathf.Clamp(obs.goalAngleDeg / 40f, -1f, 1f);

        float leftThreat = ThreatWeight(_fLeft, 0.75f) * 0.90f + ThreatWeight(_fLeftSide, 0.48f) * 0.70f;
        float rightThreat = ThreatWeight(_fRight, 0.75f) * 0.90f + ThreatWeight(_fRightSide, 0.48f) * 0.70f;
        float frontThreat = ThreatWeight(_fFront, 0.90f);

        float obstacleTurn = rightThreat - leftThreat;

        if (frontThreat > 0.20f)
        {
            float leftSpace = _fLeft + 0.5f * _fLeftSide;
            float rightSpace = _fRight + 0.5f * _fRightSide;
            float biasSign = (Mathf.Abs(leftSpace - rightSpace) < 0.08f)
                ? (obs.goalAngleDeg >= 0f ? 1f : -1f)
                : (leftSpace > rightSpace ? 1f : -1f);

            obstacleTurn += biasSign * frontThreat * 0.95f;
        }

        turn = Mathf.Clamp(goalTurn * (1f - 0.60f * frontThreat) + obstacleTurn, -1f, 1f);

        float turnFactor = 1f - 0.55f * Mathf.Abs(turn);
        float frontFactor = Mathf.Clamp01((_fFront - 0.22f) / 0.75f);
        float minSideFactor = Mathf.Clamp01((Mathf.Min(_fLeft, _fRight) - 0.16f) / 0.45f);
        float goalApproachFactor = obs.goalDistance < 0.25f
            ? Mathf.Clamp(obs.goalDistance / 0.25f, 0.25f, 1f)
            : 1f;

        speed01 = Mathf.Clamp01(turnFactor * frontFactor * minSideFactor * goalApproachFactor);
    }

    private void ComputeWallFollowCommand(bool followLeft, out float speed01, out float turn)
    {
        const float targetWallDist = 0.30f;

        if (followLeft)
        {
            float sideDist = Mathf.Min(_fLeftSide, _fLeft + 0.08f);
            float error = targetWallDist - sideDist;

            if (_fFront < 0.42f || _fLeft < 0.26f)
            {
                turn = -1.0f;
                speed01 = (_fFront < 0.28f) ? 0.08f : 0.18f;
            }
            else if (_fLeftSide > 0.75f && _fLeft > 0.75f)
            {
                // Left wall ended: turn left to wrap around the corner
                turn = 0.65f;
                speed01 = 0.42f;
            }
            else
            {
                turn = Mathf.Clamp(-error * 2.8f, -0.85f, 0.85f);
                speed01 = Mathf.Clamp01(0.55f * Mathf.Clamp01((_fFront - 0.26f) / 0.65f));
            }
        }
        else
        {
            float sideDist = Mathf.Min(_fRightSide, _fRight + 0.08f);
            float error = targetWallDist - sideDist;

            if (_fFront < 0.42f || _fRight < 0.26f)
            {
                turn = 1.0f;
                speed01 = (_fFront < 0.28f) ? 0.08f : 0.18f;
            }
            else if (_fRightSide > 0.75f && _fRight > 0.75f)
            {
                // Right wall ended: turn right to wrap around the corner
                turn = -0.65f;
                speed01 = 0.42f;
            }
            else
            {
                turn = Mathf.Clamp(error * 2.8f, -0.85f, 0.85f);
                speed01 = Mathf.Clamp01(0.55f * Mathf.Clamp01((_fFront - 0.26f) / 0.65f));
            }
        }

        // Centering nudge if the opposite wall is very close (e.g., narrow corridor)
        if (followLeft && _fRightSide < 0.20f)
        {
            turn += 0.40f;
        }
        else if (!followLeft && _fLeftSide < 0.20f)
        {
            turn -= 0.40f;
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
