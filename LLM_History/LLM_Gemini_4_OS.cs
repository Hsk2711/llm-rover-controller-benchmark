using System;
using UnityEngine;

public class LLM_Gemini_4_OS : IRoverController
{
    private enum Mode
    {
        GoalSeek,
        FollowLeft,
        FollowRight
    }

    private const float MaxSpeedMps = 0.3f;
    private const float MaxTurnDegPerSec = 90f;
    private const float Dt = 0.05f;
    private const float SensorMaxRange = 4.0f;

    // Sensor filter state
    private float _fFront;
    private float _fLeft;
    private float _fRight;
    private float _fLeftSide;
    private float _fRightSide;
    private bool _initialized;

    // Navigation & progress state
    private Mode _mode;
    private int _tick;
    private int _modeTicks;
    private float _bestGoalDist;
    private int _ticksSinceProgress;
    private int _wallFollowCycles;
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
        _tick = 0;
        _modeTicks = 0;
        _bestGoalDist = float.MaxValue;
        _ticksSinceProgress = 0;
        _wallFollowCycles = 0;
        _smoothedTurn = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _tick++;
        _modeTicks++;

        UpdateFilteredSensors(obs);
        UpdateProgressTracking(obs.goalDistance);

        float minForward = Mathf.Min(_fFront, Mathf.Min(_fLeft, _fRight));
        float minAny = Mathf.Min(minForward, Mathf.Min(_fLeftSide, _fRightSide));

        // Goal direction alignment: > 0 means front is roughly facing the goal
        float goalAlignment = Mathf.Cos(obs.goalAngleDeg * Mathf.Deg2Rad);
        bool goalBlockedAhead = (goalAlignment > 0.2f && _fFront < 0.55f) ||
                                (obs.goalAngleDeg > 15f && _fLeft < 0.45f) ||
                                (obs.goalAngleDeg < -15f && _fRight < 0.45f);

        // State transitions
        if (_mode == Mode.GoalSeek)
        {
            // Switch to wall-following if blocked toward goal or stuck without progress for ~3.5s
            if (goalBlockedAhead && minForward < 0.48f)
            {
                EnterWallFollow(ChoosePreferredWallSide(obs));
            }
            else if (_ticksSinceProgress > 70 && minAny < 1.2f)
            {
                EnterWallFollow(ChoosePreferredWallSide(obs));
            }
        }
        else
        {
            // Exit wall-following when goal is reasonably aligned, path ahead is clear,
            // and we have spent enough time clearing the obstacle/dead-end.
            bool clearForGoal = _fFront > 0.65f &&
                                ((obs.goalAngleDeg >= 0f && _fLeft > 0.55f) ||
                                 (obs.goalAngleDeg <= 0f && _fRight > 0.55f));

            if (_modeTicks > 24 && clearForGoal && Mathf.Abs(obs.goalAngleDeg) < 75f)
            {
                _mode = Mode.GoalSeek;
                _modeTicks = 0;
                _ticksSinceProgress = 0;
                _bestGoalDist = Mathf.Min(_bestGoalDist, obs.goalDistance);
            }
            else if (_modeTicks > 220)
            {
                // If wall-following for ~11s without exiting, flip side or return to goal seek
                _wallFollowCycles++;
                if (_wallFollowCycles % 2 == 1)
                {
                    EnterWallFollow(_mode == Mode.FollowLeft ? Mode.FollowRight : Mode.FollowLeft);
                }
                else
                {
                    _mode = Mode.GoalSeek;
                    _modeTicks = 0;
                    _ticksSinceProgress = 0;
                }
            }
        }

        float desiredTurn;
        float desiredSpeed01;

        if (_mode == Mode.GoalSeek)
        {
            ComputeGoalSeekCommand(obs, out desiredSpeed01, out desiredTurn);
        }
        else
        {
            ComputeWallFollowCommand(_mode == Mode.FollowLeft, out desiredSpeed01, out desiredTurn);
        }

        // Emergency close-proximity override so the rover never drives into a close obstacle
        if (_fFront < 0.24f || (_fLeft < 0.18f && _fRight < 0.18f))
        {
            desiredSpeed01 = 0f;
            if (Mathf.Abs(desiredTurn) < 0.65f)
            {
                desiredTurn = (_fLeft >= _fRight) ? 1f : -1f;
            }
        }
        else if (_fFront < 0.36f)
        {
            desiredSpeed01 = Mathf.Min(desiredSpeed01, 0.22f);
        }

        // Smooth turn slightly to damp sensor noise jitter while staying responsive
        float turnAlpha = 0.65f;
        _smoothedTurn = Mathf.Lerp(_smoothedTurn, desiredTurn, turnAlpha);

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
        // Guard against occasional false 4.0 readings when an obstacle was recently close
        if (current >= SensorMaxRange - 0.01f && prev < 1.35f)
        {
            // Slowly relax toward 4.0 over ~0.04m per tick instead of snapping immediately
            return Mathf.Min(SensorMaxRange, prev + 0.04f);
        }

        // Stronger weight on closer readings for safety, moderate smoothing otherwise
        float alpha = (current < prev) ? 0.65f : 0.40f;
        return Mathf.Lerp(prev, current, alpha);
    }

    private void UpdateProgressTracking(float goalDistance)
    {
        if (goalDistance < _bestGoalDist - 0.08f)
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
        // Choose wall side that keeps us turning generally toward the goal or away from tighter side
        float leftClearance = Mathf.Min(_fLeft, _fLeftSide);
        float rightClearance = Mathf.Min(_fRight, _fRightSide);

        if (obs.goalAngleDeg > 20f && leftClearance > 0.35f)
        {
            return Mode.FollowRight; // Hug right wall to swing left toward goal
        }
        if (obs.goalAngleDeg < -20f && rightClearance > 0.35f)
        {
            return Mode.FollowLeft;  // Hug left wall to swing right toward goal
        }

        return (leftClearance <= rightClearance) ? Mode.FollowLeft : Mode.FollowRight;
    }

    private void EnterWallFollow(Mode targetMode)
    {
        _mode = targetMode;
        _modeTicks = 0;
        _ticksSinceProgress = 0;
    }

    private void ComputeGoalSeekCommand(Observation obs, out float speed01, out float turn)
    {
        // Base turn toward goalAngleDeg (positive = left)
        float goalTurn = Mathf.Clamp(obs.goalAngleDeg / 45f, -1f, 1f);

        // Obstacle repulsion from left (+) and right (-) sensors
        float leftThreat = ThreatWeight(_fLeft, 0.70f) * 0.85f + ThreatWeight(_fLeftSide, 0.45f) * 0.65f;
        float rightThreat = ThreatWeight(_fRight, 0.70f) * 0.85f + ThreatWeight(_fRightSide, 0.45f) * 0.65f;
        float frontThreat = ThreatWeight(_fFront, 0.85f);

        // Negative turn = turn right (away from left threat); positive turn = turn left (away from right threat)
        float obstacleTurn = rightThreat - leftThreat;

        if (frontThreat > 0.25f)
        {
            // When something is ahead, bias turn toward whichever angled/side direction has more space
            float leftSpace = _fLeft + 0.5f * _fLeftSide;
            float rightSpace = _fRight + 0.5f * _fRightSide;
            float biasSign = (Mathf.Abs(leftSpace - rightSpace) < 0.08f)
                ? (obs.goalAngleDeg >= 0f ? 1f : -1f)
                : (leftSpace > rightSpace ? 1f : -1f);

            obstacleTurn += biasSign * frontThreat * 0.9f;
        }

        turn = Mathf.Clamp(goalTurn * (1f - 0.55f * frontThreat) + obstacleTurn, -1f, 1f);

        // Speed scales down with sharp turns and close front obstacles
        float turnFactor = 1f - 0.55f * Mathf.Abs(turn);
        float frontFactor = Mathf.Clamp01((_fFront - 0.22f) / 0.75f);
        float minSideFactor = Mathf.Clamp01((Mathf.Min(_fLeft, _fRight) - 0.16f) / 0.45f);

        // Gentle approach near goal center
        float goalApproachFactor = obs.goalDistance < 0.25f
            ? Mathf.Clamp(obs.goalDistance / 0.25f, 0.25f, 1f)
            : 1f;

        speed01 = Mathf.Clamp01(turnFactor * frontFactor * minSideFactor * goalApproachFactor);
    }

    private void ComputeWallFollowCommand(bool followLeft, out float speed01, out float turn)
    {
        const float targetWallDist = 0.32f;

        if (followLeft)
        {
            // Keep wall on our left (+90 deg side and +45 deg front-left)
            float sideDist = Mathf.Min(_fLeftSide, _fLeft + 0.10f);
            float error = targetWallDist - sideDist; // > 0 => too close to left wall => turn right (-)

            if (_fFront < 0.36f || _fLeft < 0.24f)
            {
                // Corner or dead-end ahead: turn right away from obstacle
                turn = -0.95f;
                speed01 = (_fFront < 0.26f) ? 0.12f : 0.28f;
            }
            else if (_fLeftSide > 0.75f && _fLeft > 0.75f)
            {
                // Wall ended on left: curve left to find/wrap around the corner
                turn = 0.55f;
                speed01 = 0.45f;
            }
            else
            {
                turn = Mathf.Clamp(-error * 2.6f, -0.85f, 0.85f);
                speed01 = Mathf.Clamp01(0.55f * Mathf.Clamp01((_fFront - 0.25f) / 0.65f));
            }
        }
        else
        {
            // Keep wall on our right (-90 deg side and -45 deg front-right)
            float sideDist = Mathf.Min(_fRightSide, _fRight + 0.10f);
            float error = targetWallDist - sideDist; // > 0 => too close to right wall => turn left (+)

            if (_fFront < 0.36f || _fRight < 0.24f)
            {
                // Corner or dead-end ahead: turn left away from obstacle
                turn = 0.95f;
                speed01 = (_fFront < 0.26f) ? 0.12f : 0.28f;
            }
            else if (_fRightSide > 0.75f && _fRight > 0.75f)
            {
                // Wall ended on right: curve right to find/wrap around the corner
                turn = -0.55f;
                speed01 = 0.45f;
            }
            else
            {
                turn = Mathf.Clamp(error * 2.6f, -0.85f, 0.85f);
                speed01 = Mathf.Clamp01(0.55f * Mathf.Clamp01((_fFront - 0.25f) / 0.65f));
            }
        }

        // Extra safety if the opposite side is very close in a narrow corridor
        if (followLeft && _fRightSide < 0.18f)
        {
            turn += 0.35f;
        }
        else if (!followLeft && _fLeftSide < 0.18f)
        {
            turn -= 0.35f;
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
