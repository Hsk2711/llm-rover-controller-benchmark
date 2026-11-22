using System;
using UnityEngine;

public class LLM_Gemini_2_FB : IRoverController
{
    private enum NavMode
    {
        SeekGoal,
        FollowLeftWall,
        FollowRightWall,
        EscapeTurn
    }

    private const int HistorySize = 3;
    private const float MaxTurnDeltaPerTick = 0.32f;
    private const float MaxSpeedDeltaPerTick = 0.25f;

    private NavMode _mode;
    private int _modeSteps;
    private int _escapeDir; // +1 = left, -1 = right
    private int _wallSide;   // +1 = left wall, -1 = right wall

    private float[] _frontHist;
    private float[] _leftHist;
    private float[] _rightHist;
    private float[] _leftSideHist;
    private float[] _rightSideHist;
    private int _histIndex;
    private bool _histWarm;

    private float _bestGoalDist;
    private float _wallEntryGoalDist;
    private int _stepsWithoutProgress;
    private int _escapeCooldown;

    private float _smoothedTurn;
    private float _smoothedSpeed;

    public string Name => "LLM_Controller";

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _modeSteps = 0;
        _escapeDir = 1;
        _wallSide = 1;

        _frontHist = new float[HistorySize];
        _leftHist = new float[HistorySize];
        _rightHist = new float[HistorySize];
        _leftSideHist = new float[HistorySize];
        _rightSideHist = new float[HistorySize];
        _histIndex = 0;
        _histWarm = false;

        _bestGoalDist = float.MaxValue;
        _wallEntryGoalDist = float.MaxValue;
        _stepsWithoutProgress = 0;
        _escapeCooldown = 0;

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
        if (_escapeCooldown > 0)
        {
            _escapeCooldown--;
        }

        UpdateHistory(obs);

        float f = MedianSensor(_frontHist);
        float l = MedianSensor(_leftHist);
        float r = MedianSensor(_rightHist);
        float ls = MedianSensor(_leftSideHist);
        float rs = MedianSensor(_rightSideHist);

        float minForward = Mathf.Min(f, Mathf.Min(l, r));
        float rawFrontDanger = Mathf.Min(obs.front, Mathf.Min(obs.left, obs.right));

        // Track overall best distance to goal
        if (obs.goalDistance < _bestGoalDist - 0.05f)
        {
            _bestGoalDist = obs.goalDistance;
            _stepsWithoutProgress = 0;
        }
        else
        {
            _stepsWithoutProgress++;
        }

        bool inTightPocket = f < 0.25f && l < 0.34f && r < 0.34f;
        bool stalledTooLong = _stepsWithoutProgress >= 180 && _escapeCooldown == 0; // ~9s without new best distance

        if ((inTightPocket || stalledTooLong) && _mode != NavMode.EscapeTurn)
        {
            _escapeDir = ChooseClearerDirection(obs.goalAngleDeg, l, ls, r, rs);
            _wallSide = _escapeDir;
            _wallEntryGoalDist = obs.goalDistance;
            _stepsWithoutProgress = 0;
            _escapeCooldown = 60;
            SwitchMode(NavMode.EscapeTurn);
        }

        if (_mode == NavMode.EscapeTurn)
        {
            // Turn until a clear path opens ahead (~1.0s to 2.2s)
            if ((minForward > 0.58f && _modeSteps >= 20) || _modeSteps >= 44)
            {
                SwitchMode(_wallSide > 0 ? NavMode.FollowLeftWall : NavMode.FollowRightWall);
            }
        }
        else if (_mode == NavMode.SeekGoal)
        {
            // Enter wall-following when obstacle blocks forward path or diagonal is close
            if (f < 0.34f || Mathf.Min(l, r) < 0.24f)
            {
                _wallSide = ChooseWallSide(obs.goalAngleDeg, l, ls, r, rs);
                _wallEntryGoalDist = obs.goalDistance;
                SwitchMode(_wallSide > 0 ? NavMode.FollowLeftWall : NavMode.FollowRightWall);
            }
        }
        else if (_mode == NavMode.FollowLeftWall || _mode == NavMode.FollowRightWall)
        {
            bool followingLeft = _mode == NavMode.FollowLeftWall;
            bool madeProgressSinceWall = obs.goalDistance < _wallEntryGoalDist - 0.12f;
            bool facingGoal = Mathf.Abs(obs.goalAngleDeg) < 32f;
            bool goalPathClear = followingLeft
                ? (f > 0.65f && (obs.goalAngleDeg <= 0f || l > 0.65f))
                : (f > 0.65f && (obs.goalAngleDeg >= 0f || r > 0.65f));

            // Only leave wall-following after real progress toward goal and a clear path
            if (_modeSteps >= 20 && madeProgressSinceWall && facingGoal && goalPathClear)
            {
                SwitchMode(NavMode.SeekGoal);
            }
        }

        float targetTurn;
        float targetSpeed01;

        switch (_mode)
        {
            case NavMode.EscapeTurn:
                targetTurn = _escapeDir * 0.85f;
                targetSpeed01 = rawFrontDanger < 0.14f ? 0.05f : 0.22f;
                break;

            case NavMode.FollowLeftWall:
                ComputeWallFollow(
                    leftWall: true,
                    front: f,
                    diagSame: l,
                    sideSame: ls,
                    diagOpp: r,
                    sideOpp: rs,
                    out targetTurn,
                    out targetSpeed01);
                break;

            case NavMode.FollowRightWall:
                ComputeWallFollow(
                    leftWall: false,
                    front: f,
                    diagSame: r,
                    sideSame: rs,
                    diagOpp: l,
                    sideOpp: ls,
                    out targetTurn,
                    out targetSpeed01);
                break;

            case NavMode.SeekGoal:
            default:
                ComputeSeekGoal(
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

        if (rawFrontDanger < 0.11f)
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
        // Prefer the side with more clearance, breaking ties toward the goal side
        float leftOpen = Mathf.Min(l, 1.5f) + 0.6f * Mathf.Min(ls, 1.5f) + (goalAngleDeg > 10f ? 0.15f : 0f);
        float rightOpen = Mathf.Min(r, 1.5f) + 0.6f * Mathf.Min(rs, 1.5f) + (goalAngleDeg < -10f ? 0.15f : 0f);
        if (Mathf.Abs(leftOpen - rightOpen) < 0.08f)
        {
            return goalAngleDeg >= 0f ? 1 : -1;
        }
        return leftOpen > rightOpen ? 1 : -1;
    }

    private int ChooseClearerDirection(float goalAngleDeg, float l, float ls, float r, float rs)
    {
        float leftRoom = Mathf.Min(l, 1.5f) + 0.8f * Mathf.Min(ls, 1.5f);
        float rightRoom = Mathf.Min(r, 1.5f) + 0.8f * Mathf.Min(rs, 1.5f);
        if (Mathf.Abs(leftRoom - rightRoom) < 0.08f)
        {
            return goalAngleDeg >= 0f ? 1 : -1;
        }
        return leftRoom > rightRoom ? 1 : -1;
    }

    private void ComputeSeekGoal(
        Observation obs,
        float front,
        float l,
        float r,
        float ls,
        float rs,
        out float turn,
        out float speed01)
    {
        float angle = Mathf.Abs(obs.goalAngleDeg) < 3.0f ? 0f : obs.goalAngleDeg;
        float goalTurn = Mathf.Clamp(angle / 38f, -1f, 1f);

        // Proximity-only repulsion so faraway noisy readings never cause drift
        float leftPush = ProximityRepulsion(l, 0.45f) * 0.85f + ProximityRepulsion(ls, 0.28f) * 0.55f;
        float rightPush = ProximityRepulsion(r, 0.45f) * 0.85f + ProximityRepulsion(rs, 0.28f) * 0.55f;
        float avoidTurn = rightPush - leftPush;

        float frontSlow = Mathf.Clamp01((0.55f - front) / 0.35f);
        turn = Mathf.Clamp(goalTurn * (1f - 0.5f * frontSlow) + avoidTurn, -1f, 1f);

        float clearFactor = Mathf.Lerp(0.22f, 0.95f, Mathf.Clamp01((front - 0.18f) / 0.60f));
        float turnFactor = 1f - 0.45f * Mathf.Abs(turn);
        float goalSlow = obs.goalDistance < 0.35f ? Mathf.Clamp(obs.goalDistance / 0.35f, 0.35f, 1f) : 1f;

        speed01 = Mathf.Clamp(clearFactor * turnFactor * goalSlow, 0.15f, 1.0f);
    }

    private void ComputeWallFollow(
        bool leftWall,
        float front,
        float diagSame,
        float sideSame,
        float diagOpp,
        float sideOpp,
        out float turn,
        out float speed01)
    {
        float towardWall = leftWall ? 1f : -1f;
        float awayWall = -towardWall;

        const float desiredDist = 0.24f;
        float wallDist = Mathf.Min(sideSame, diagSame + 0.04f);

        // Turn away from the wall when front or same-side diagonal gets close
        float frontDanger = Mathf.Clamp01((0.38f - front) / 0.22f);
        float diagDanger = Mathf.Clamp01((0.26f - diagSame) / 0.16f);
        float avoidWeight = Mathf.Max(frontDanger, diagDanger);
        float awayCmd = awayWall * (0.45f + 0.45f * avoidWeight);

        // Gentle distance holding with deadband
        float err = wallDist - desiredDist;
        if (Mathf.Abs(err) < 0.04f)
        {
            err = 0f;
        }
        float trackCmd = Mathf.Clamp(err * 1.5f, -0.35f, 0.35f) * towardWall;

        // Avoid opposite wall if corridor is tight
        float oppDanger = ProximityRepulsion(Mathf.Min(diagOpp, sideOpp + 0.04f), 0.26f);
        float oppCmd = -awayWall * oppDanger * 0.35f;

        float followCmd = Mathf.Clamp(trackCmd + oppCmd, -0.75f, 0.75f);
        turn = Mathf.Clamp(Mathf.Lerp(followCmd, awayCmd, avoidWeight), -1f, 1f);

        float minClear = Mathf.Min(front, diagSame);
        float baseSpeed = Mathf.Lerp(0.22f, 0.72f, Mathf.Clamp01((minClear - 0.18f) / 0.50f));
        speed01 = Mathf.Clamp(baseSpeed * (1f - 0.35f * Mathf.Abs(turn)), 0.15f, 0.80f);
    }

    private static float ProximityRepulsion(float dist, float range)
    {
        if (dist >= range) return 0f;
        float d = Mathf.Max(dist, 0.02f);
        return Mathf.Clamp01((range - d) / (range - 0.02f));
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
