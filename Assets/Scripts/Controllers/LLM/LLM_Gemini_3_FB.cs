using System;
using UnityEngine;

public class LLM_Gemini_3_FB : IRoverController
{
    public string Name => "LLM_Controller";

    private const float Dt = 0.05f;
    private const float MaxSpeed = 0.30f;       // m/s
    private const float MaxTurnRateDeg = 90.0f; // deg/s
    private const float SensorMin = 0.02f;
    private const float SensorMax = 4.0f;
    private const int FilterWindow = 7;

    private enum NavMode
    {
        SeekGoal,
        FollowLeftWall,
        FollowRightWall,
        UnstickTurn
    }

    private NavMode _mode;
    private int _modeTicks;
    private int _clearGoalTicks;
    private int _unstickSign;      // +1 = left, -1 = right
    private int _unstickTicksLeft;
    private int _unstickCooldown;
    private int _wallFlipCount;

    private float _bestGoalDist;
    private float _wallStartGoalDist;
    private float _prevFilteredGoalDist;
    private int _ticksWithoutBestGoal;

    private float _prevTurn;
    private float _fFront, _fLeft, _fRight, _fLeftSide, _fRightSide;
    private bool _hasFilteredOnce;

    private readonly float[] _bufFront = new float[FilterWindow];
    private readonly float[] _bufLeft = new float[FilterWindow];
    private readonly float[] _bufRight = new float[FilterWindow];
    private readonly float[] _bufLeftSide = new float[FilterWindow];
    private readonly float[] _bufRightSide = new float[FilterWindow];
    private readonly float[] _scratch = new float[FilterWindow];
    private int _bufIdx;
    private int _bufCount;

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _modeTicks = 0;
        _clearGoalTicks = 0;
        _unstickSign = 1;
        _unstickTicksLeft = 0;
        _unstickCooldown = 0;
        _wallFlipCount = 0;

        _bestGoalDist = float.MaxValue;
        _wallStartGoalDist = float.MaxValue;
        _prevFilteredGoalDist = float.MaxValue;
        _ticksWithoutBestGoal = 0;

        _prevTurn = 0.0f;
        _fFront = _fLeft = _fRight = _fLeftSide = _fRightSide = SensorMax;
        _hasFilteredOnce = false;

        _bufIdx = 0;
        _bufCount = 0;

        for (int i = 0; i < FilterWindow; i++)
        {
            _bufFront[i] = SensorMax;
            _bufLeft[i] = SensorMax;
            _bufRight[i] = SensorMax;
            _bufLeftSide[i] = SensorMax;
            _bufRightSide[i] = SensorMax;
        }
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTicks++;
        if (_unstickCooldown > 0)
            _unstickCooldown--;

        float rFront = SanitizeSensor(obs.front);
        float rLeft = SanitizeSensor(obs.left);
        float rRight = SanitizeSensor(obs.right);
        float rLeftSide = SanitizeSensor(obs.leftSide);
        float rRightSide = SanitizeSensor(obs.rightSide);

        PushSensors(rFront, rLeft, rRight, rLeftSide, rRightSide);

        float mFront = MedianOf(_bufFront);
        float mLeft = MedianOf(_bufLeft);
        float mRight = MedianOf(_bufRight);
        float mLeftSide = MedianOf(_bufLeftSide);
        float mRightSide = MedianOf(_bufRightSide);

        // Suppress occasional false 4.0 readings when recent median shows a closer obstacle
        float cFront = SuppressFalseMax(rFront, mFront);
        float cLeft = SuppressFalseMax(rLeft, mLeft);
        float cRight = SuppressFalseMax(rRight, mRight);
        float cLeftSide = SuppressFalseMax(rLeftSide, mLeftSide);
        float cRightSide = SuppressFalseMax(rRightSide, mRightSide);

        if (!_hasFilteredOnce)
        {
            _fFront = cFront;
            _fLeft = cLeft;
            _fRight = cRight;
            _fLeftSide = cLeftSide;
            _fRightSide = cRightSide;
            _hasFilteredOnce = true;
        }
        else
        {
            const float ema = 0.45f;
            _fFront = Mathf.Lerp(_fFront, cFront, ema);
            _fLeft = Mathf.Lerp(_fLeft, cLeft, ema);
            _fRight = Mathf.Lerp(_fRight, cRight, ema);
            _fLeftSide = Mathf.Lerp(_fLeftSide, cLeftSide, ema);
            _fRightSide = Mathf.Lerp(_fRightSide, cRightSide, ema);
        }

        float rawGoalDist = float.IsNaN(obs.goalDistance) || float.IsInfinity(obs.goalDistance)
            ? _prevFilteredGoalDist
            : Mathf.Max(0.0f, obs.goalDistance);
        float goalDist = (_prevFilteredGoalDist < 1e6f)
            ? Mathf.Lerp(_prevFilteredGoalDist, rawGoalDist, 0.5f)
            : rawGoalDist;
        _prevFilteredGoalDist = goalDist;

        float goalAngle = NormalizeAngleDeg(obs.goalAngleDeg);

        if (goalDist + 0.03f < _bestGoalDist)
        {
            _bestGoalDist = goalDist;
            _ticksWithoutBestGoal = 0;
        }
        else
        {
            _ticksWithoutBestGoal++;
        }

        if (goalDist < 0.10f)
        {
            float finalTurn = Mathf.Clamp(goalAngle / 45.0f, -0.3f, 0.3f);
            _prevTurn = finalTurn;
            return (0.15f, finalTurn);
        }

        float minForward = Mathf.Min(_fFront, Mathf.Min(_fLeft, _fRight));
        bool frontBlocked = _fFront < 0.48f || minForward < 0.32f;

        if (IsGoalDirectionOpen(goalAngle, _fFront, _fLeft, _fRight, _fLeftSide, _fRightSide))
            _clearGoalTicks++;
        else
            _clearGoalTicks = 0;

        // Unstick escalation when no new best goal distance is achieved for ~8 seconds
        if (_ticksWithoutBestGoal >= 160 && _unstickCooldown == 0 && _mode != NavMode.UnstickTurn)
        {
            _unstickCooldown = 120;
            _ticksWithoutBestGoal = 0;

            if (_wallFlipCount < 1 && (_mode == NavMode.FollowLeftWall || _mode == NavMode.FollowRightWall))
            {
                _wallFlipCount++;
                _mode = (_mode == NavMode.FollowLeftWall) ? NavMode.FollowRightWall : NavMode.FollowLeftWall;
                _modeTicks = 0;
                _clearGoalTicks = 0;
                _wallStartGoalDist = goalDist;
            }
            else
            {
                _wallFlipCount = 0;
                _mode = NavMode.UnstickTurn;
                _modeTicks = 0;
                _unstickTicksLeft = 28; // ~126 deg turn at 90 deg/s
                _unstickSign = ChooseTurnSign(goalAngle, _fLeft, _fRight, _fLeftSide, _fRightSide);
            }
        }

        // Mode state machine
        if (_mode == NavMode.UnstickTurn)
        {
            _unstickTicksLeft--;
            if (_unstickTicksLeft <= 0 || (_modeTicks >= 16 && _fFront > 0.90f && Mathf.Abs(goalAngle) < 45.0f))
            {
                _mode = NavMode.SeekGoal;
                _modeTicks = 0;
                _clearGoalTicks = 0;
            }
        }
        else if (_mode == NavMode.SeekGoal)
        {
            if (frontBlocked && _modeTicks >= 6)
            {
                bool followLeft = ChooseWallFollowLeft(goalAngle, _fLeft, _fRight, _fLeftSide, _fRightSide);
                _mode = followLeft ? NavMode.FollowLeftWall : NavMode.FollowRightWall;
                _modeTicks = 0;
                _clearGoalTicks = 0;
                _wallStartGoalDist = goalDist;
            }
        }
        else // FollowLeftWall or FollowRightWall
        {
            bool madeProgressPastWall = goalDist < _wallStartGoalDist - 0.14f;
            bool minElapsed = _modeTicks >= 24;
            bool canLeaveToGoal = minElapsed &&
                                  madeProgressPastWall &&
                                  _clearGoalTicks >= 6 &&
                                  !frontBlocked;

            if (canLeaveToGoal)
            {
                _mode = NavMode.SeekGoal;
                _modeTicks = 0;
                _clearGoalTicks = 0;
                _wallFlipCount = 0;
            }
        }

        float speed01;
        float targetTurn;

        switch (_mode)
        {
            case NavMode.UnstickTurn:
                targetTurn = _unstickSign * 1.0f;
                speed01 = (_fFront > 0.45f && _fLeft > 0.35f && _fRight > 0.35f) ? 0.25f : 0.0f;
                break;

            case NavMode.FollowLeftWall:
                (speed01, targetTurn) = ComputeSmoothWallFollow(
                    followLeft: true,
                    front: _fFront,
                    diagSame: _fLeft,
                    diagOpp: _fRight,
                    sideSame: _fLeftSide,
                    sideOpp: _fRightSide
                );
                break;

            case NavMode.FollowRightWall:
                (speed01, targetTurn) = ComputeSmoothWallFollow(
                    followLeft: false,
                    front: _fFront,
                    diagSame: _fRight,
                    diagOpp: _fLeft,
                    sideSame: _fRightSide,
                    sideOpp: _fLeftSide
                );
                break;

            case NavMode.SeekGoal:
            default:
                (speed01, targetTurn) = ComputeSeekGoal(
                    goalAngle: goalAngle,
                    front: _fFront,
                    left: _fLeft,
                    right: _fRight,
                    leftSide: _fLeftSide,
                    rightSide: _fRightSide
                );
                break;
        }

        // Smooth turn rate to prevent jerky steering
        float turnRateLimit = (_mode == NavMode.UnstickTurn) ? 0.35f : 0.22f;
        float turn = Mathf.Clamp(targetTurn, _prevTurn - turnRateLimit, _prevTurn + turnRateLimit);
        turn = Mathf.Clamp(turn, -1.0f, 1.0f);
        _prevTurn = turn;

        speed01 = ApplySafetySpeedClamp(speed01, turn, _fFront, _fLeft, _fRight, _fLeftSide, _fRightSide);
        speed01 = Mathf.Clamp(speed01, 0.0f, 1.0f);

        return (speed01, turn);
    }

    private (float speed01, float turn) ComputeSeekGoal(
        float goalAngle,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float angledGoal = (Mathf.Abs(goalAngle) < 3.5f) ? 0.0f : goalAngle;
        float turn = angledGoal / 45.0f;

        if (left < 0.45f)
            turn -= (0.45f - left) * 1.15f;
        if (right < 0.45f)
            turn += (0.45f - right) * 1.15f;
        if (leftSide < 0.22f)
            turn -= (0.22f - leftSide) * 0.9f;
        if (rightSide < 0.22f)
            turn += (0.22f - rightSide) * 0.9f;

        float align = Mathf.Clamp01(1.0f - Mathf.Abs(goalAngle) / 120.0f);
        float clear = Mathf.Clamp01((front - 0.24f) / 1.0f);
        float speed01 = Mathf.Lerp(0.25f, 0.95f, align * clear);

        if (Mathf.Abs(goalAngle) > 70.0f)
            speed01 = Mathf.Min(speed01, 0.35f);

        return (speed01, Mathf.Clamp(turn, -1.0f, 1.0f));
    }

    private (float speed01, float turn) ComputeSmoothWallFollow(
        bool followLeft,
        float front,
        float diagSame,
        float diagOpp,
        float sideSame,
        float sideOpp)
    {
        // +1 = left, -1 = right
        float towardWall = followLeft ? +1.0f : -1.0f;
        float awayWall = -towardWall;

        const float targetSideDist = 0.34f;
        const float deadband = 0.03f;

        float turn;
        float speed01;

        // Blended obstacle clearance + wall distance so there is no abrupt mode flip at corners
        float frontThreat = Mathf.Clamp01((0.55f - front) / 0.38f);
        float diagThreat = Mathf.Clamp01((0.44f - diagSame) / 0.28f);
        float obstacleAvoid = Mathf.Max(frontThreat, diagThreat);

        if (obstacleAvoid > 0.01f)
        {
            turn = awayWall * Mathf.Lerp(0.35f, 0.95f, obstacleAvoid);
            speed01 = Mathf.Lerp(0.60f, 0.24f, obstacleAvoid);
        }
        else if (sideSame < 1.05f)
        {
            float err = sideSame - targetSideDist;
            if (Mathf.Abs(err) < deadband)
                err = 0.0f;
            turn = Mathf.Clamp(towardWall * (err * 1.4f), -0.35f, 0.35f);
            speed01 = 0.68f;
        }
        else
        {
            // Passed an outer corner: curve gently toward the wall side to re-acquire it
            turn = towardWall * 0.26f;
            speed01 = 0.62f;
        }

        // Prevent brushing walls/obstacles on the opposite side (e.g., narrow corridors)
        if (sideOpp < 0.22f)
            turn += towardWall * ((0.22f - sideOpp) * 1.1f);
        if (diagOpp < 0.26f)
            turn += towardWall * ((0.26f - diagOpp) * 1.0f);

        return (speed01, Mathf.Clamp(turn, -1.0f, 1.0f));
    }

    private float ApplySafetySpeedClamp(
        float speed01,
        float turn,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float minFwd = Mathf.Min(front, Mathf.Min(left, right));
        float minSide = Mathf.Min(leftSide, rightSide);

        if (minFwd < 0.20f || minSide < 0.14f)
            return Mathf.Min(speed01, 0.15f);

        if (minFwd < 0.32f || minSide < 0.19f)
            speed01 = Mathf.Min(speed01, 0.35f);
        else if (minFwd < 0.50f)
            speed01 = Mathf.Min(speed01, 0.60f);

        if (Mathf.Abs(turn) > 0.70f)
            speed01 = Mathf.Min(speed01, 0.32f);

        return speed01;
    }

    private bool ChooseWallFollowLeft(
        float goalAngle,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float leftRoom = left + 0.5f * leftSide;
        float rightRoom = right + 0.5f * rightSide;

        if (Mathf.Abs(leftRoom - rightRoom) > 0.14f)
            return leftRoom > rightRoom;

        return goalAngle >= 0.0f;
    }

    private int ChooseTurnSign(
        float goalAngle,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float leftRoom = left + 0.6f * leftSide;
        float rightRoom = right + 0.6f * rightSide;

        if (Mathf.Abs(leftRoom - rightRoom) > 0.12f)
            return (leftRoom > rightRoom) ? 1 : -1;

        if (Mathf.Abs(goalAngle) > 20.0f)
            return (goalAngle >= 0.0f) ? 1 : -1;

        return (_wallFlipCount % 2 == 0) ? 1 : -1;
    }

    private bool IsGoalDirectionOpen(
        float goalAngle,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        if (Mathf.Abs(goalAngle) <= 20.0f)
            return front > 0.72f && left > 0.48f && right > 0.48f;

        if (goalAngle > 20.0f && goalAngle <= 55.0f)
            return left > 0.72f && front > 0.48f;

        if (goalAngle < -20.0f && goalAngle >= -55.0f)
            return right > 0.72f && front > 0.48f;

        if (goalAngle > 55.0f && goalAngle <= 105.0f)
            return leftSide > 0.72f && left > 0.45f;

        if (goalAngle < -55.0f && goalAngle >= -105.0f)
            return rightSide > 0.72f && right > 0.45f;

        return false;
    }

    private void PushSensors(float f, float l, float r, float ls, float rs)
    {
        _bufFront[_bufIdx] = f;
        _bufLeft[_bufIdx] = l;
        _bufRight[_bufIdx] = r;
        _bufLeftSide[_bufIdx] = ls;
        _bufRightSide[_bufIdx] = rs;

        _bufIdx = (_bufIdx + 1) % FilterWindow;
        if (_bufCount < FilterWindow)
            _bufCount++;
    }

    private float MedianOf(float[] buf)
    {
        int n = _bufCount;
        if (n <= 0)
            return SensorMax;

        for (int i = 0; i < n; i++)
            _scratch[i] = buf[i];

        Array.Sort(_scratch, 0, n);
        return _scratch[n / 2];
    }

    private static float SuppressFalseMax(float current, float median)
    {
        if (current >= SensorMax - 0.01f && median < 2.5f)
            return median;
        return current;
    }

    private static float SanitizeSensor(float val)
    {
        if (float.IsNaN(val) || float.IsInfinity(val))
            return SensorMax;
        return Mathf.Clamp(val, SensorMin, SensorMax);
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
