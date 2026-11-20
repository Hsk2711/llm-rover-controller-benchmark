using System;
using UnityEngine;

public class LLM_Gemini_1_OS : IRoverController
{
    private enum Mode
    {
        SeekGoal,
        AvoidTurn,
        WallFollow
    }

    private const float Dt = 0.05f;
    private const float MaxSpeedMps = 0.30f;
    private const float MaxTurnDegPerSec = 90.0f;
    private const float MaxStepTurnDeg = MaxTurnDegPerSec * Dt; // 4.5 deg/step
    private const float NoDetectValue = 4.0f;

    // Rover dimensions: 0.20 m long x 0.15 m wide (half-width ~ 0.075 m)
    private const float FrontStopDist = 0.28f;
    private const float FrontSlowDist = 0.65f;
    private const float SideDangerDist = 0.20f;
    private const float SideComfortDist = 0.34f;

    private Mode _mode;
    private int _stepCount;

    // Filtered sensor estimates (in metres)
    private float _fFront;
    private float _fLeft;
    private float _fRight;
    private float _fLeftSide;
    private float _fRightSide;

    // Consecutive "no-detection" (4.0) counters to filter out occasional false 4.0s
    private int _noDetFront;
    private int _noDetLeft;
    private int _noDetRight;
    private int _noDetLeftSide;
    private int _noDetRightSide;

    // Wall-follow / turn commitment state
    private float _escapeDir;          // +1.0 = turn/follow left, -1.0 = turn/follow right
    private float _modeTimer;          // seconds spent in current non-seek mode
    private float _accumTurnDeg;     // approximate integrated turn during escape/wall-follow
    private float _wallTargetDist;     // desired side clearance during wall-follow
    private float _prevGoalDist;
    private float _stuckTimer;

    public string Name => "DeterministicSensorNavigator";

    public void ResetController()
    {
        _mode = Mode.SeekGoal;
        _stepCount = 0;

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
        _modeTimer = 0.0f;
        _accumTurnDeg = 0.0f;
        _wallTargetDist = SideComfortDist;
        _prevGoalDist = -1.0f;
        _stuckTimer = 0.0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _stepCount++;
        _modeTimer += Dt;

        UpdateFilteredSensors(obs);

        float goalAngle = Mathf.Clamp(obs.goalAngleDeg, -180.0f, 180.0f);
        float goalDist = Mathf.Max(0.0f, obs.goalDistance);

        // Track progress toward goal to detect stalls / U-turn traps
        if (_prevGoalDist >= 0.0f)
        {
            float deltaGoal = _prevGoalDist - goalDist;
            float maxPossibleProgress = MaxSpeedMps * Dt;
            if (deltaGoal < maxPossibleProgress * 0.15f && _fFront < 1.2f)
            {
                _stuckTimer += Dt;
            }
            else
            {
                _stuckTimer = Mathf.Max(0.0f, _stuckTimer - Dt * 0.5f);
            }
        }
        _prevGoalDist = goalDist;

        // Effective front & diagonal/side clearances
        float frontClear = _fFront;
        float leftClear = Mathf.Min(_fLeft, _fLeftSide * 1.05f);
        float rightClear = Mathf.Min(_fRight, _fRightSide * 1.05f);

        bool blockedAhead = frontClear < FrontStopDist ||
                            (_fLeft < 0.22f && Mathf.Abs(goalAngle) < 55.0f) ||
                            (_fRight < 0.22f && Mathf.Abs(goalAngle) < 55.0f);

        bool tightCorridor = (_fLeftSide < SideDangerDist && _fRightSide < SideDangerDist) ||
                             (frontClear < FrontSlowDist && leftClear < SideComfortDist && rightClear < SideComfortDist);

        // State transitions
        if (_mode == Mode.SeekGoal)
        {
            if (blockedAhead || _stuckTimer > 2.2f)
            {
                _escapeDir = ChooseTurnDirection(goalAngle, leftClear, rightClear);
                _mode = tightCorridor ? Mode.WallFollow : Mode.AvoidTurn;
                _modeTimer = 0.0f;
                _accumTurnDeg = 0.0f;
                _wallTargetDist = Mathf.Clamp(
                    _escapeDir > 0.0f ? _fLeftSide : _fRightSide,
                    SideDangerDist,
                    0.45f
                );
            }
        }
        else if (_mode == Mode.AvoidTurn)
        {
            // Once front is reasonably clear and we have turned enough or goal is ahead & open
            bool pathOpened = frontClear > FrontSlowDist &&
                              ((_escapeDir > 0.0f && goalAngle >= -10.0f) ||
                               (_escapeDir < 0.0f && goalAngle <= 10.0f) ||
                               Mathf.Abs(goalAngle) < 35.0f);

            if (pathOpened && _modeTimer >= 0.35f)
            {
                _mode = Mode.SeekGoal;
                _modeTimer = 0.0f;
                _stuckTimer = 0.0f;
            }
            else if (_modeTimer > 2.5f || Mathf.Abs(_accumTurnDeg) > 155.0f)
            {
                // Likely in a U-shaped pocket or tight bend: switch to structured wall-following
                _mode = Mode.WallFollow;
                _modeTimer = 0.0f;
                _wallTargetDist = SideComfortDist;
            }
        }
        else if (_mode == Mode.WallFollow)
        {
            // Exit wall-follow when front is open, we have rotated substantially (e.g., out of U-turn),
            // and the goal lies in a non-blocked direction
            bool goalDirectionClear = IsDirectionClear(goalAngle);
            if (_modeTimer >= 0.8f && frontClear > FrontSlowDist && goalDirectionClear &&
                (Mathf.Abs(_accumTurnDeg) >= 65.0f || _modeTimer >= 2.2f))
            {
                _mode = Mode.SeekGoal;
                _modeTimer = 0.0f;
                _stuckTimer = 0.0f;
            }
            else if (_modeTimer > 7.5f)
            {
                // Safety fallback: re-evaluate goal seeking if wall-following runs long
                _mode = Mode.SeekGoal;
                _modeTimer = 0.0f;
            }
        }

        float speed01;
        float turnCmd;

        if (_mode == Mode.SeekGoal)
        {
            (speed01, turnCmd) = ComputeSeekCommand(goalAngle, goalDist, frontClear, leftClear, rightClear);
        }
        else if (_mode == Mode.AvoidTurn)
        {
            (speed01, turnCmd) = ComputeAvoidTurnCommand(goalAngle, frontClear, leftClear, rightClear);
        }
        else
        {
            (speed01, turnCmd) = ComputeWallFollowCommand(frontClear, leftClear, rightClear);
        }

        // Hard emergency safety override so contact is avoided even under noisy readings
        if (frontClear < 0.16f)
        {
            speed01 = Mathf.Min(speed01, 0.10f);
            turnCmd = _escapeDir != 0.0f ? _escapeDir : (leftClear >= rightClear ? 1.0f : -1.0f);
        }
        if (_fLeftSide < 0.11f && turnCmd > 0.0f)
        {
            turnCmd = Mathf.Min(turnCmd, -0.35f);
            speed01 = Mathf.Min(speed01, 0.35f);
        }
        if (_fRightSide < 0.11f && turnCmd < 0.0f)
        {
            turnCmd = Mathf.Max(turnCmd, 0.35f);
            speed01 = Mathf.Min(speed01, 0.35f);
        }

        speed01 = Mathf.Clamp01(speed01);
        turnCmd = Mathf.Clamp(turnCmd, -1.0f, 1.0f);

        _accumTurnDeg += turnCmd * MaxStepTurnDeg;
        return (speed01, turnCmd);
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
            // Guard against occasional false 4.0 readings: only clear out quickly if we already saw far/clear,
            // otherwise require 2-3 consecutive 4.0s before trusting open space.
            if (prevFiltered < 1.2f && noDetCount < 3)
            {
                return Mathf.Max(0.02f, prevFiltered + 0.05f);
            }
            if (prevFiltered < 2.5f && noDetCount < 2)
            {
                return Mathf.Max(0.02f, prevFiltered + 0.15f);
            }
            return NoDetectValue;
        }

        noDetCount = 0;

        // If a closer reading arrives, react quickly (asymmetric low-pass) so obstacles aren't clipped
        if (clamped < prevFiltered)
        {
            float alpha = clamped < 0.45f ? 0.75f : 0.55f;
            return Mathf.Lerp(prevFiltered, clamped, alpha);
        }

        // If reading increases, smooth moderately to suppress upward Gaussian noise spikes
        return Mathf.Lerp(prevFiltered, clamped, 0.35f);
    }

    private static float ChooseTurnDirection(float goalAngleDeg, float leftClear, float rightClear)
    {
        // Bias toward whichever side has noticeably more room; tie-break toward the goal direction
        float diff = leftClear - rightClear;
        if (Mathf.Abs(diff) > 0.12f)
        {
            return diff > 0.0f ? 1.0f : -1.0f;
        }
        if (Mathf.Abs(goalAngleDeg) > 10.0f)
        {
            return goalAngleDeg > 0.0f ? 1.0f : -1.0f;
        }
        return leftClear >= rightClear ? 1.0f : -1.0f;
    }

    private bool IsDirectionClear(float angleDeg)
    {
        if (angleDeg > 20.0f)
        {
            return _fLeft > 0.55f && _fLeftSide > 0.25f;
        }
        if (angleDeg < -20.0f)
        {
            return _fRight > 0.55f && _fRightSide > 0.25f;
        }
        return _fFront > FrontSlowDist && _fLeft > 0.40f && _fRight > 0.40f;
    }

    private (float speed01, float turn) ComputeSeekCommand(
        float goalAngleDeg,
        float goalDist,
        float frontClear,
        float leftClear,
        float rightClear)
    {
        // Base turn toward goal (proportional gain in normalized [-1, 1] turn units)
        float goalTurn = Mathf.Clamp(goalAngleDeg / 35.0f, -1.0f, 1.0f);

        // Potential-field style obstacle repulsion from 45 deg and 90 deg sensors
        float leftRepulsion = 0.0f;
        if (_fLeft < 0.55f)
        {
            leftRepulsion += (0.55f - _fLeft) / 0.55f;
        }
        if (_fLeftSide < SideComfortDist)
        {
            leftRepulsion += 0.8f * ((SideComfortDist - _fLeftSide) / SideComfortDist);
        }

        float rightRepulsion = 0.0f;
        if (_fRight < 0.55f)
        {
            rightRepulsion += (0.55f - _fRight) / 0.55f;
        }
        if (_fRightSide < SideComfortDist)
        {
            rightRepulsion += 0.8f * ((SideComfortDist - _fRightSide) / SideComfortDist);
        }

        // Positive repulsion on left pushes turn negative (right); positive on right pushes turn positive (left)
        float obstacleBias = (rightRepulsion - leftRepulsion) * 0.85f;

        // When front is getting close, blend in stronger directional turn away from tighter side
        float frontUrgency = frontClear < FrontSlowDist
            ? Mathf.Clamp01((FrontSlowDist - frontClear) / (FrontSlowDist - FrontStopDist))
            : 0.0f;

        float avoidDir = leftClear >= rightClear ? 1.0f : -1.0f;
        float turn = Mathf.Lerp(goalTurn + obstacleBias, avoidDir, frontUrgency * 0.75f);

        // Speed modulation: slow down for sharp turns, near obstacles, or very close to goal
        float minSide = Mathf.Min(_fLeft, Mathf.Min(_fRight, Mathf.Min(_fLeftSide, _fRightSide)));
        float clearanceFactor = Mathf.Clamp01((Mathf.Min(frontClear, minSide * 1.5f) - 0.15f) / 0.55f);
        float turnFactor = Mathf.Clamp01(1.0f - 0.55f * Mathf.Abs(turn));
        float goalFactor = goalDist < 0.30f ? Mathf.Clamp(goalDist / 0.30f, 0.25f, 1.0f) : 1.0f;

        float speed01 = Mathf.Lerp(0.15f, 1.0f, clearanceFactor) * turnFactor * goalFactor;
        if (Mathf.Abs(goalAngleDeg) > 110.0f && frontClear > 0.6f)
        {
            // Pivot faster when goal is behind and path is open
            speed01 = Mathf.Min(speed01, 0.35f);
        }

        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeAvoidTurnCommand(
        float goalAngleDeg,
        float frontClear,
        float leftClear,
        float rightClear)
    {
        float turn = _escapeDir;

        // If front is moderately open and goal is on the same side as our escape turn, ease toward goal
        if (frontClear > FrontStopDist * 1.25f && Mathf.Sign(goalAngleDeg) == _escapeDir && Mathf.Abs(goalAngleDeg) < 75.0f)
        {
            turn = Mathf.Lerp(_escapeDir, Mathf.Clamp(goalAngleDeg / 35.0f, -1.0f, 1.0f), 0.35f);
        }

        float minClear = Mathf.Min(frontClear, Mathf.Min(leftClear, rightClear));
        float speed01 = minClear < 0.22f ? 0.18f : 0.42f;
        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeWallFollowCommand(
        float frontClear,
        float leftClear,
        float rightClear)
    {
        // Keep the wall on the side opposite _escapeDir:
        // If _escapeDir == +1 (turning/moving left), keep obstacle on right (_fRight / _fRightSide)
        // If _escapeDir == -1 (turning/moving right), keep obstacle on left (_fLeft / _fLeftSide)
        float sideDist = _escapeDir > 0.0f
            ? Mathf.Min(_fRightSide, _fRight * 0.9f)
            : Mathf.Min(_fLeftSide, _fLeft * 0.9f);

        float diagDist = _escapeDir > 0.0f ? _fRight : _fLeft;

        // If blocked ahead, turn strongly toward _escapeDir
        if (frontClear < FrontStopDist * 1.15f)
        {
            float tightFactor = Mathf.Clamp01((FrontStopDist * 1.15f - frontClear) / 0.18f);
            float speed = Mathf.Lerp(0.40f, 0.16f, tightFactor);
            return (speed, _escapeDir);
        }

        // Maintain _wallTargetDist from the followed wall
        float error = _wallTargetDist - sideDist;
        // Positive error means too close to wall -> steer away from wall (which is _escapeDir)
        float wallSteer = Mathf.Clamp(error * 3.2f, -0.75f, 0.85f) * _escapeDir;

        // If diagonal sensor ahead on the wall side sees an approaching corner, steer away early
        if (diagDist < 0.28f)
        {
            wallSteer = _escapeDir * 0.85f;
        }
        else if (sideDist > _wallTargetDist + 0.22f && frontClear > 0.55f)
        {
            // Wall fell away (e.g., rounded U-turn tip): curve gently back toward wall side
            wallSteer = -_escapeDir * 0.45f;
        }

        float speed01 = Mathf.Clamp(frontClear * 0.9f, 0.25f, 0.65f);
        return (speed01, wallSteer);
    }
}
