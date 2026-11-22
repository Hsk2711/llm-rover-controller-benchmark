using System;
using UnityEngine;

public class LLM_Gemini_2_FB : IRoverController
{
    private enum NavMode
    {
        SeekGoal,
        FollowLeftWall,
        FollowRightWall,
        CommitTurn
    }

    private const int HistorySize = 3;
    private const float MaxTurnDeltaPerTick = 0.22f;
    private const float MaxSpeedDeltaPerTick = 0.18f;

    private NavMode _mode;
    private int _modeSteps;
    private int _commitDir; // +1 = left, -1 = right
    private int _preferredWallDir; // +1 = left wall, -1 = right wall

    private float[] _frontHist;
    private float[] _leftHist;
    private float[] _rightHist;
    private float[] _leftSideHist;
    private float[] _rightSideHist;
    private int _histIndex;
    private bool _histWarm;

    private float _bestGoalDist;
    private int _stepsWithoutProgress;
    private int _signChanges;
    private int _prevTurnDir;
    private int _commitCooldown;

    private float _smoothedTurn;
    private float _smoothedSpeed;

    public string Name => "LLM_Controller";

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _modeSteps = 0;
        _commitDir = 1;
        _preferredWallDir = 1;

        _frontHist = new float[HistorySize];
        _leftHist = new float[HistorySize];
        _rightHist = new float[HistorySize];
        _leftSideHist = new float[HistorySize];
        _rightSideHist = new float[HistorySize];
        _histIndex = 0;
        _histWarm = false;

        _bestGoalDist = float.MaxValue;
        _stepsWithoutProgress = 0;
        _signChanges = 0;
        _prevTurnDir = 0;
        _commitCooldown = 0;

        _smoothedTurn = 0f;
        _smoothedSpeed = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        if (_frontHist == null)
        {
            ResetController();
        }

        _modeSteps++;
        if (_commitCooldown > 0)
        {
            _commitCooldown--;
        }

        UpdateHistory(obs);

        float f = MedianSensor(_frontHist);
        float l = MedianSensor(_leftHist);
        float r = MedianSensor(_rightHist);
        float ls = MedianSensor(_leftSideHist);
        float rs = MedianSensor(_rightSideHist);

        float minForward = Mathf.Min(f, Mathf.Min(l, r));
        float rawTightFront = Mathf.Min(obs.front, Mathf.Min(obs.left * 1.05f, obs.right * 1.05f));

        // Track meaningful progress toward the goal
        if (obs.goalDistance < _bestGoalDist - 0.07f)
        {
            _bestGoalDist = obs.goalDistance;
            _stepsWithoutProgress = 0;
            _signChanges = Math.Max(0, _signChanges - 2);
        }
        else
        {
            _stepsWithoutProgress++;
        }

        // Count true turn-direction reversals (ignoring near-straight jitter)
        int turnDir = Math.Abs(_smoothedTurn) < 0.14f ? 0 : (_smoothedTurn > 0f ? 1 : -1);
        if (turnDir != 0 && _prevTurnDir != 0 && turnDir != _prevTurnDir)
        {
            _signChanges++;
        }
        if (turnDir != 0)
        {
            _prevTurnDir = turnDir;
        }

        bool trappedDeadEnd = f < 0.26f && l < 0.34f && r < 0.34f;
        bool chronicOscillation = _signChanges >= 12 && _modeSteps >= 15 && _commitCooldown == 0;
        bool stalledRun = _stepsWithoutProgress >= 120 && _commitCooldown == 0;

        if ((trappedDeadEnd || chronicOscillation || stalledRun) && _mode != NavMode.CommitTurn)
        {
            _commitDir = ChooseTurnDirection(obs.goalAngleDeg, l, ls, r, rs);
            _preferredWallDir = _commitDir;
            _signChanges = 0;
            _stepsWithoutProgress = 0;
            _commitCooldown = 65;
            SwitchMode(NavMode.CommitTurn);
        }

        if (_mode == NavMode.CommitTurn)
        {
            // Turn decisively until front/diagonal opens up or ~3.2s elapses
            bool pathOpened = minForward > 0.62f && _modeSteps >= 26;
            if (pathOpened || _modeSteps >= 64)
            {
                _signChanges = 0;
                SwitchMode(_preferredWallDir > 0 ? NavMode.FollowLeftWall : NavMode.FollowRightWall);
            }
        }
        else if (_mode == NavMode.SeekGoal)
        {
            if (f < 0.36f || Mathf.Min(l, r) < 0.24f)
            {
                _preferredWallDir = ChooseWallSide(obs.goalAngleDeg, l, ls, r, rs);
                SwitchMode(_preferredWallDir > 0 ? NavMode.FollowLeftWall : NavMode.FollowRightWall);
            }
        }
        else if (_mode == NavMode.FollowLeftWall || _mode == NavMode.FollowRightWall)
        {
            bool followingLeft = _mode == NavMode.FollowLeftWall;
            bool goalSideClear = followingLeft
                ? (obs.goalAngleDeg <= 5f || (l > 0.72f && ls > 0.55f))
                : (obs.goalAngleDeg >= -5f || (r > 0.72f && rs > 0.55f));

            bool safeToResumeGoal = _modeSteps >= 35 &&
                                    minForward > 0.72f &&
                                    Mathf.Abs(obs.goalAngleDeg) < 28f &&
                                    goalSideClear;

            if (safeToResumeGoal)
            {
                SwitchMode(NavMode.SeekGoal);
            }
        }

        float targetTurn;
        float targetSpeed01;

        switch (_mode)
        {
            case NavMode.CommitTurn:
                targetTurn = _commitDir * 0.90f;
                targetSpeed01 = rawTightFront < 0.15f ? 0.04f : 0.18f;
                break;

            case NavMode.FollowLeftWall:
                SteerWallFollow(
                    leftWall: true,
                    front: f,
                    diagSame: l,
                    sideSame: ls,
                    diagOpp: r,
                    sideOpp: rs,
                    goalAngleDeg: obs.goalAngleDeg,
                    out targetTurn,
                    out targetSpeed01);
                break;

            case NavMode.FollowRightWall:
                SteerWallFollow(
                    leftWall: false,
                    front: f,
                    diagSame: r,
                    sideSame: rs,
                    diagOpp: l,
                    sideOpp: ls,
                    goalAngleDeg: obs.goalAngleDeg,
                    out targetTurn,
                    out targetSpeed01);
                break;

            case NavMode.SeekGoal:
            default:
                SteerSeekGoal(
                    obs: obs,
                    front: f,
                    l: l,
                    r: r,
                    ls: ls,
                    rs: rs,
                    out targetTurn,
                    out targetSpeed01);
                break;
        }

        _smoothedTurn = Mathf.Clamp(
            targetTurn,
            _smoothedTurn - MaxTurnDeltaPerTick,
            _smoothedTurn + MaxTurnDeltaPerTick);

        _smoothedSpeed = Mathf.Clamp(
            targetSpeed01,
            _smoothedSpeed - MaxSpeedDeltaPerTick,
            _smoothedSpeed + MaxSpeedDeltaPerTick);

        float finalTurn = Mathf.Clamp(_smoothedTurn, -1f, 1f);
        float finalSpeed = Mathf.Clamp(_smoothedSpeed, 0f, 1f);

        if (rawTightFront < 0.12f)
        {
            finalSpeed = Mathf.Min(finalSpeed, 0.08f);
        }

        return (finalSpeed, finalTurn);
    }

    private void SwitchMode(NavMode next)
    {
        if (_mode != next)
        {
            _mode = next;
            _modeSteps = 0;
        }
    }

    private int ChooseWallSide(float goalAngleDeg, float l, float ls, float r, float rs)
    {
        float leftScore = 0.55f * l + 0.45f * ls + (goalAngleDeg > 15f ? 0.10f : 0f);
        float rightScore = 0.55f * r + 0.45f * rs + (goalAngleDeg < -15f ? 0.10f : 0f);
        if (Mathf.Abs(leftScore - rightScore) < 0.08f)
        {
            return _preferredWallDir;
        }
        return leftScore > rightScore ? 1 : -1;
    }

    private int ChooseTurnDirection(float goalAngleDeg, float l, float ls, float r, float rs)
    {
        float leftRoom = l + 0.85f * ls + (goalAngleDeg > 25f ? 0.15f : 0f);
        float rightRoom = r + 0.85f * rs + (goalAngleDeg < -25f ? 0.15f : 0f);
        if (Mathf.Abs(leftRoom - rightRoom) < 0.08f)
        {
            return _commitDir != 0 ? _commitDir : (goalAngleDeg >= 0f ? 1 : -1);
        }
        return leftRoom > rightRoom ? 1 : -1;
    }

    private void SteerSeekGoal(
        Observation obs,
        float front,
        float l,
        float r,
        float ls,
        float rs,
        out float turn,
        out float speed01)
    {
        // Deadband prevents chasing tiny angle noise when nearly aligned
        float cleanAngle = Mathf.Abs(obs.goalAngleDeg) < 4.0f ? 0f : obs.goalAngleDeg;
        float goalComponent = Mathf.Clamp(cleanAngle / 42f, -1f, 1f);

        // Single cohesive side-clearance bias (positive = steer left, negative = steer right)
        float leftClearance = 0.70f * l + 0.45f * ls;
        float rightClearance = 0.70f * r + 0.45f * rs;
        float sideBias = Mathf.Clamp((leftClearance - rightClearance) * 0.9f, -0.75f, 0.75f);

        float frontProximity = Mathf.Clamp01((0.65f - front) / 0.45f);
        turn = Mathf.Clamp(
            Mathf.Lerp(goalComponent, sideBias, frontProximity * 0.75f) + sideBias * 0.25f,
            -1f,
            1f);

        float clearSpeed = Mathf.Lerp(0.18f, 0.92f, Mathf.Clamp01((front - 0.18f) / 0.65f));
        float turnSlow = 1f - 0.45f * Mathf.Abs(turn);
        float goalSlow = obs.goalDistance < 0.35f ? Mathf.Clamp(obs.goalDistance / 0.35f, 0.35f, 1f) : 1f;

        speed01 = Mathf.Clamp(clearSpeed * turnSlow * goalSlow, 0.12f, 1.0f);
    }

    private void SteerWallFollow(
        bool leftWall,
        float front,
        float diagSame,
        float sideSame,
        float diagOpp,
        float sideOpp,
        float goalAngleDeg,
        out float turn,
        out float speed01)
    {
        float towardWall = leftWall ? 1f : -1f;
        float awayWall = -towardWall;

        const float desiredDist = 0.24f;
        float measuredWall = Mathf.Min(sideSame, diagSame + 0.04f);

        // Turn away smoothly when obstacle is ahead or on the followed diagonal
        float frontUrgency = Mathf.Clamp01((0.40f - front) / 0.24f);
        float diagUrgency = Mathf.Clamp01((0.26f - diagSame) / 0.16f);
        float avoidWeight = Mathf.Max(frontUrgency, diagUrgency);

        float awayTurn = awayWall * (0.42f + 0.48f * avoidWeight);

        // Gentle wall-distance hold with deadband
        float error = measuredWall - desiredDist;
        if (Mathf.Abs(error) < 0.035f)
        {
            error = 0f;
        }
        float trackTurn = Mathf.Clamp(error * 1.6f, -0.40f, 0.40f) * towardWall;

        // Slight opposite-wall caution in narrow corridors
        float oppClose = Mathf.Clamp01((0.26f - Mathf.Min(diagOpp, sideOpp + 0.04f)) / 0.18f);
        float oppTurn = -awayWall * oppClose * 0.35f;

        // Tiny goal bias only when well clear of walls
        float goalBias = 0f;
        if (avoidWeight < 0.1f && diagSame > 0.55f && front > 0.65f)
        {
            goalBias = Mathf.Clamp(goalAngleDeg / 130f, -0.14f, 0.14f);
        }

        float wallCmd = Mathf.Clamp(trackTurn + oppTurn + goalBias, -0.80f, 0.80f);
        turn = Mathf.Clamp(Mathf.Lerp(wallCmd, awayTurn, avoidWeight), -1f, 1f);

        float baseSpeed = Mathf.Lerp(0.20f, 0.70f, Mathf.Clamp01((Mathf.Min(front, diagSame) - 0.18f) / 0.52f));
        speed01 = Mathf.Clamp(baseSpeed * (1f - 0.35f * Mathf.Abs(turn)), 0.12f, 0.80f);
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

    private float MedianSensor(float[] buf)
    {
        if (!_histWarm && _histIndex == 1)
        {
            return buf[0];
        }

        int count = _histWarm ? HistorySize : _histIndex;
        float a = buf[0];
        float b = count > 1 ? buf[1] : a;
        float c = count > 2 ? buf[2] : b;

        if (a > b) { float t = a; a = b; b = t; }
        if (b > c) { float t = b; b = c; c = t; }
        if (a > b) { float t = a; a = b; b = t; }
        return b;
    }
}
