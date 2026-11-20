using System;
using UnityEngine;

public class LLM_Gemini_1_FB : IRoverController
{
    private enum Mode
    {
        SeekGoal,
        WallFollow
    }

    private const float Dt = 0.05f;
    private const float NoDetectValue = 4.0f;

    // Rover size: 0.20 m long x 0.15 m wide
    private const float FrontBlockDist = 0.30f;
    private const float SideHugTarget = 0.34f;
    private const float SideTooClose = 0.20f;

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

    // Wall-follow & progress state
    private float _wallSide;            // +1.0 = hug wall on left, -1.0 = hug wall on right
    private float _modeTimer;           // seconds spent in current mode
    private float _goalDistAtWallStart; // goal distance when entering WallFollow
    private bool _flippedWallOnce;      // whether we already flipped wall side in current pocket
    private float _bestGoalDist;        // best goal distance achieved overall
    private float _noProgressTimer;     // seconds without new best goal distance

    public string Name => "CleanWallSeekNavigator";

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

        _wallSide = -1.0f;
        _modeTimer = 0.0f;
        _goalDistAtWallStart = float.MaxValue;
        _flippedWallOnce = false;
        _bestGoalDist = float.MaxValue;
        _noProgressTimer = 0.0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTimer += Dt;

        UpdateFilteredSensors(obs);

        float goalAngle = Mathf.Clamp(obs.goalAngleDeg, -180.0f, 180.0f);
        float goalDist = Mathf.Max(0.0f, obs.goalDistance);

        if (goalDist < _bestGoalDist - 0.05f)
        {
            _bestGoalDist = goalDist;
            _noProgressTimer = 0.0f;
        }
        else
        {
            _noProgressTimer += Dt;
        }

        if (_mode == Mode.SeekGoal)
        {
            bool frontBlocked = _fFront < FrontBlockDist;
            bool diagonalBlocked = (_fLeft < 0.22f && goalAngle > -20.0f) ||
                                   (_fRight < 0.22f && goalAngle < 20.0f);
            bool stalled = _noProgressTimer > 6.0f && _modeTimer > 2.0f;

            if (frontBlocked || diagonalBlocked || stalled)
            {
                _wallSide = ChooseWallSide(goalAngle);
                _mode = Mode.WallFollow;
                _modeTimer = 0.0f;
                _goalDistAtWallStart = goalDist;
                _flippedWallOnce = false;
            }
        }
        else // Mode.WallFollow
        {
            bool clearToSeek = _modeTimer >= 1.6f &&
                               _fFront > 0.85f &&
                               _fLeft > 0.48f &&
                               _fRight > 0.48f &&
                               Mathf.Abs(goalAngle) < 35.0f &&
                               (goalDist < _goalDistAtWallStart - 0.12f || _modeTimer >= 4.0f);

            if (clearToSeek)
            {
                _mode = Mode.SeekGoal;
                _modeTimer = 0.0f;
                _noProgressTimer = 0.0f;
            }
            else if (_modeTimer >= 9.0f && goalDist >= _goalDistAtWallStart - 0.10f)
            {
                if (!_flippedWallOnce)
                {
                    // Reverse wall side once to escape a U-shaped dead end
                    _wallSide = -_wallSide;
                    _flippedWallOnce = true;
                    _modeTimer = 0.0f;
                    _goalDistAtWallStart = goalDist;
                }
                else
                {
                    // Re-evaluate goal direction after trying both wall sides
                    _mode = Mode.SeekGoal;
                    _modeTimer = 0.0f;
                    _noProgressTimer = 0.0f;
                }
            }
        }

        float speed01;
        float turn;

        if (_mode == Mode.SeekGoal)
        {
            (speed01, turn) = ComputeSeekGoal(goalAngle, goalDist);
        }
        else
        {
            (speed01, turn) = ComputeWallFollow();
        }

        // Hard safety guards against wall/obstacle contact under noisy sensors
        if (_fFront < 0.17f)
        {
            speed01 = Mathf.Min(speed01, 0.22f);
            turn = _wallSide != 0.0f ? -_wallSide : (_fLeft >= _fRight ? 1.0f : -1.0f);
        }
        if (_fLeftSide < 0.12f && turn > 0.0f)
        {
            turn = -0.50f;
            speed01 = Mathf.Max(0.22f, Mathf.Min(speed01, 0.35f));
        }
        if (_fRightSide < 0.12f && turn < 0.0f)
        {
            turn = 0.50f;
            speed01 = Mathf.Max(0.22f, Mathf.Min(speed01, 0.35f));
        }

        speed01 = Mathf.Clamp(speed01, 0.20f, 1.0f);
        turn = Mathf.Clamp(turn, -1.0f, 1.0f);
        return (speed01, turn);
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

    private float ChooseWallSide(float goalAngleDeg)
    {
        float leftDist = Mathf.Min(_fLeft, _fLeftSide);
        float rightDist = Mathf.Min(_fRight, _fRightSide);

        // If goal is to the left, hugging a wall on the right lets us curve left toward the goal;
        // if goal is to the right, hugging a wall on the left lets us curve right toward the goal.
        if (Mathf.Abs(leftDist - rightDist) > 0.15f)
        {
            return leftDist < rightDist ? 1.0f : -1.0f;
        }
        if (Mathf.Abs(goalAngleDeg) > 15.0f)
        {
            return goalAngleDeg > 0.0f ? -1.0f : 1.0f;
        }
        return -1.0f;
    }

    private (float speed01, float turn) ComputeSeekGoal(float goalAngleDeg, float goalDist)
    {
        float deadbandAngle = Mathf.Abs(goalAngleDeg) < 3.0f ? 0.0f : goalAngleDeg;
        float turn = Mathf.Clamp(deadbandAngle / 35.0f, -1.0f, 1.0f);

        // Gentle side push only when an obstacle is close to a flank
        if (_fLeft < 0.26f)
        {
            turn -= 0.45f * ((0.26f - _fLeft) / 0.26f);
        }
        if (_fRight < 0.26f)
        {
            turn += 0.45f * ((0.26f - _fRight) / 0.26f);
        }
        if (_fLeftSide < SideTooClose)
        {
            turn -= 0.35f * ((SideTooClose - _fLeftSide) / SideTooClose);
        }
        if (_fRightSide < SideTooClose)
        {
            turn += 0.35f * ((SideTooClose - _fRightSide) / SideTooClose);
        }

        float minForward = Mathf.Min(_fFront, Mathf.Min(_fLeft, _fRight));
        float speedFactor = Mathf.Clamp01((minForward - 0.18f) / 0.55f);
        float turnFactor = Mathf.Clamp01(1.0f - 0.45f * Mathf.Abs(turn));
        float goalFactor = goalDist < 0.35f ? Mathf.Clamp(goalDist / 0.35f, 0.35f, 1.0f) : 1.0f;

        float speed01 = Mathf.Lerp(0.28f, 1.0f, speedFactor) * turnFactor * goalFactor;
        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeWallFollow()
    {
        // _wallSide == +1 means wall is on our LEFT; _wallSide == -1 means wall is on our RIGHT.
        float wallSideDist = _wallSide > 0.0f ? _fLeftSide : _fRightSide;
        float wallDiagDist = _wallSide > 0.0f ? _fLeft : _fRight;

        // If blocked in front, turn away from the wall we're following (-_wallSide)
        if (_fFront < FrontBlockDist)
        {
            float turnAway = -_wallSide * 0.95f;
            float speed = _fFront < 0.22f ? 0.24f : 0.40f;
            return (speed, turnAway);
        }

        // If the diagonal sensor on the wall side sees an inner corner ahead, steer away from wall
        if (wallDiagDist < 0.28f)
        {
            return (0.38f, -_wallSide * 0.85f);
        }

        // Maintain SideHugTarget distance from the followed wall:
        // If wallSideDist < SideHugTarget (too close), steer away from wall (-_wallSide).
        // If wallSideDist > SideHugTarget (too far / outer corner), steer toward wall (+_wallSide).
        float distError = wallSideDist - SideHugTarget;
        float steerTowardWall = Mathf.Clamp(distError * 2.4f, -0.65f, 0.65f);
        float turn = steerTowardWall * _wallSide;

        float speed01 = Mathf.Clamp(_fFront * 0.80f, 0.32f, 0.68f);
        return (speed01, turn);
    }
}
