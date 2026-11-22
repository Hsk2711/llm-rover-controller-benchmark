using System;
using UnityEngine;

public sealed class LLM_Gemini_5_FB : IRoverController
{
    public string Name => "LLM_StableWallGoalController";

    private enum NavMode
    {
        SeekGoal,
        FollowLeftWall,
        FollowRightWall,
        EscapeTurn
    }

    private const float MaxSensorRange = 4.0f;
    private const int HistorySize = 5;

    private NavMode _mode;
    private int _modeTicks;
    private int _escapeTicks;
    private float _escapeDir; // +1 left, -1 right
    private int _wallSwitchCount;

    private float _bestGoalDist;
    private int _ticksSinceProgress;

    private float _prevRawTurn;
    private int _signFlips;
    private int _flipWindowTicks;

    private float[,] _hist;
    private int[] _histCount;
    private int[] _histIdx;

    public LLM_Gemini_5_FB()
    {
        _hist = new float[5, HistorySize];
        _histCount = new int[5];
        _histIdx = new int[5];
        ResetController();
    }

    public void ResetController()
    {
        _mode = NavMode.SeekGoal;
        _modeTicks = 0;
        _escapeTicks = 0;
        _escapeDir = 1.0f;
        _wallSwitchCount = 0;

        _bestGoalDist = float.MaxValue;
        _ticksSinceProgress = 0;

        _prevRawTurn = 0f;
        _signFlips = 0;
        _flipWindowTicks = 0;

        for (int s = 0; s < 5; s++)
        {
            _histCount[s] = 0;
            _histIdx[s] = 0;
            for (int k = 0; k < HistorySize; k++)
            {
                _hist[s, k] = MaxSensorRange;
            }
        }
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        _modeTicks++;

        if (obs.goalDistance < _bestGoalDist - 0.04f)
        {
            _bestGoalDist = obs.goalDistance;
            _ticksSinceProgress = 0;
        }
        else
        {
            _ticksSinceProgress++;
        }

        float f = FilterSensor(0, obs.front);
        float l45 = FilterSensor(1, obs.left);
        float r45 = FilterSensor(2, obs.right);
        float l90 = FilterSensor(3, obs.leftSide);
        float r90 = FilterSensor(4, obs.rightSide);

        float minFront = Mathf.Min(f, Mathf.Min(l45, r45));

        // Goal close and front clear: drive smoothly into goal
        if (obs.goalDistance < 0.14f && minFront > 0.24f)
        {
            float tGoal = Mathf.Clamp(obs.goalAngleDeg / 32.0f, -1f, 1f);
            float sGoal = Mathf.Clamp01(0.45f * (1f - 0.4f * Mathf.Abs(tGoal)));
            RecordTurnSign(tGoal);
            return (sGoal, tGoal);
        }

        // Detect sustained back-and-forth oscillation
        _flipWindowTicks++;
        if (_flipWindowTicks >= 40)
        {
            _flipWindowTicks = 0;
            _signFlips = Mathf.Max(0, _signFlips - 2);
        }

        bool isOscillating = _signFlips >= 8;
        bool isStalled = _ticksSinceProgress > 240; // ~12s without new best goal distance

        if (_mode != NavMode.EscapeTurn && (isOscillating || isStalled))
        {
            _signFlips = 0;
            _flipWindowTicks = 0;
            _ticksSinceProgress = 90;
            _escapeTicks = 28; // ~1.4s committed escape turn

            if (_mode == NavMode.FollowLeftWall)
            {
                _escapeDir = -1.0f;
            }
            else if (_mode == NavMode.FollowRightWall)
            {
                _escapeDir = 1.0f;
            }
            else
            {
                float leftRoom = l45 + 0.8f * l90;
                float rightRoom = r45 + 0.8f * r90;
                if (Mathf.Abs(leftRoom - rightRoom) < 0.10f)
                {
                    _escapeDir = obs.goalAngleDeg >= 0f ? 1.0f : -1.0f;
                }
                else
                {
                    _escapeDir = leftRoom >= rightRoom ? 1.0f : -1.0f;
                }
            }

            _mode = NavMode.EscapeTurn;
            _modeTicks = 0;
        }

        if (_mode == NavMode.EscapeTurn)
        {
            _escapeTicks--;
            float turnEsc = _escapeDir * 0.75f;
            float speedEsc = (f > 0.28f && l45 > 0.22f && r45 > 0.22f) ? 0.18f : 0.05f;

            if (_escapeTicks <= 0 || (f > 0.65f && l45 > 0.45f && r45 > 0.45f && _modeTicks >= 10))
            {
                if (_wallSwitchCount < 3 && (l90 < 0.65f || r90 < 0.65f))
                {
                    _wallSwitchCount++;
                    _mode = (_escapeDir > 0f) ? NavMode.FollowLeftWall : NavMode.FollowRightWall;
                }
                else
                {
                    _mode = NavMode.SeekGoal;
                }
                _modeTicks = 0;
            }

            RecordTurnSign(turnEsc);
            return (speedEsc, turnEsc);
        }

        // Normal mode transitions
        bool frontObstacle = f < 0.42f || l45 < 0.30f || r45 < 0.30f;

        if (_mode == NavMode.SeekGoal)
        {
            if (frontObstacle && _modeTicks >= 3)
            {
                _mode = ChooseWallFollowMode(obs.goalAngleDeg, l45, r45, l90, r90);
                _modeTicks = 0;
            }
        }
        else // FollowLeftWall or FollowRightWall
        {
            bool leftWall = (_mode == NavMode.FollowLeftWall);
            float sideDist = leftWall ? l90 : r90;
            float diagWallDist = leftWall ? l45 : r45;
            float oppDiagDist = leftWall ? r45 : l45;

            if (_modeTicks >= 18)
            {
                bool lostWall = sideDist > 1.45f && diagWallDist > 1.35f && f > 0.85f;

                // Rejoin goal only when the direction toward the goal is clearly open
                bool goalTowardLeft = obs.goalAngleDeg > 8f;
                bool goalTowardRight = obs.goalAngleDeg < -8f;
                bool goalDirOpen =
                    (goalTowardLeft && f > 0.85f && l45 > 0.75f && oppDiagDist > 0.35f) ||
                    (goalTowardRight && f > 0.85f && r45 > 0.75f && oppDiagDist > 0.35f) ||
                    (!goalTowardLeft && !goalTowardRight && f > 0.90f && l45 > 0.55f && r45 > 0.55f);

                if (lostWall || (goalDirOpen && Mathf.Abs(obs.goalAngleDeg) < 55f))
                {
                    _mode = NavMode.SeekGoal;
                    _modeTicks = 0;
                }
            }
        }

        float speed01;
        float turn;

        if (_mode == NavMode.FollowLeftWall)
        {
            ComputeWallFollow(
                followLeft: true,
                f: f,
                diagSame: l45,
                diagOpp: r45,
                sideSame: l90,
                sideOpp: r90,
                out speed01,
                out turn
            );
        }
        else if (_mode == NavMode.FollowRightWall)
        {
            ComputeWallFollow(
                followLeft: false,
                f: f,
                diagSame: r45,
                diagOpp: l45,
                sideSame: r90,
                sideOpp: l90,
                out speed01,
                out turn
            );
        }
        else
        {
            ComputeSeekGoal(obs.goalAngleDeg, f, l45, r45, l90, r90, out speed01, out turn);
        }

        RecordTurnSign(turn);
        return (speed01, turn);
    }

    private NavMode ChooseWallFollowMode(float goalAngleDeg, float l45, float r45, float l90, float r90)
    {
        // If one side has a clear wall and the other is wide open, follow the side with more space to turn around
        if (l90 < 0.35f && r90 > 0.90f) return NavMode.FollowLeftWall;
        if (r90 < 0.35f && l90 > 0.90f) return NavMode.FollowRightWall;

        // Otherwise follow the wall on the side that lets us turn toward the goal without cutting across the obstacle
        if (goalAngleDeg < -10f)
        {
            // Goal is to the right: following left wall often guides around obstacle toward right,
            // or following right wall if right side is closer
            return (r45 < l45) ? NavMode.FollowRightWall : NavMode.FollowLeftWall;
        }
        if (goalAngleDeg > 10f)
        {
            return (l45 < r45) ? NavMode.FollowLeftWall : NavMode.FollowRightWall;
        }

        return (l45 + l90) <= (r45 + r90) ? NavMode.FollowLeftWall : NavMode.FollowRightWall;
    }

    private void ComputeSeekGoal(
        float goalAngleDeg,
        float f,
        float l45,
        float r45,
        float l90,
        float r90,
        out float speed01,
        out float turn)
    {
        float goalTurn = Mathf.Clamp(goalAngleDeg / 36.0f, -1.0f, 1.0f);

        float leftRepulse = Repulsion(l45, 0.55f) * 1.1f + Repulsion(l90, 0.32f) * 0.6f;
        float rightRepulse = Repulsion(r45, 0.55f) * 1.1f + Repulsion(r90, 0.32f) * 0.6f;
        float frontRepulse = Repulsion(f, 0.68f);

        float avoidTurn = rightRepulse - leftRepulse;
        if (frontRepulse > 0.05f)
        {
            float turnSign;
            if (Mathf.Abs(rightRepulse - leftRepulse) > 0.12f)
            {
                turnSign = (rightRepulse > leftRepulse) ? 1.0f : -1.0f;
            }
            else
            {
                turnSign = (goalAngleDeg >= 0f) ? 1.0f : -1.0f;
            }
            avoidTurn += turnSign * frontRepulse * 1.1f;
        }

        float blend = Mathf.Clamp01(frontRepulse * 0.75f + Mathf.Max(leftRepulse, rightRepulse) * 0.45f);
        turn = Mathf.Clamp(goalTurn * (1.0f - blend) + avoidTurn, -1.0f, 1.0f);

        float minFront = Mathf.Min(f, Mathf.Min(l45, r45));
        float distScale = Mathf.Clamp01((minFront - 0.16f) / 0.60f);
        float turnScale = 1.0f - 0.45f * Mathf.Abs(turn);
        speed01 = Mathf.Clamp(0.90f * distScale * turnScale, 0.15f, 0.95f);
    }

    private void ComputeWallFollow(
        bool followLeft,
        float f,
        float diagSame,
        float diagOpp,
        float sideSame,
        float sideOpp,
        out float speed01,
        out float turn)
    {
        // +1 = left turn, -1 = right turn
        float s = followLeft ? 1.0f : -1.0f;
        const float desiredDist = 0.24f;

        // Obstacle ahead or corner on followed diagonal: turn away from followed wall
        if (f < 0.32f || diagSame < 0.25f)
        {
            turn = -s * 0.85f;
            speed01 = (f < 0.18f || diagSame < 0.16f) ? 0.08f : 0.25f;
            return;
        }

        // Narrow corridor: keep roughly centered between left and right walls
        if (sideSame < 0.45f && sideOpp < 0.45f)
        {
            float diff = sideOpp - sideSame; // > 0 means more room on opposite side
            turn = Mathf.Clamp(diff * 2.0f, -0.55f, 0.55f);
            speed01 = Mathf.Clamp(0.55f * Mathf.Clamp01((f - 0.22f) / 0.55f), 0.18f, 0.65f);
            return;
        }

        float err = sideSame - desiredDist;
        if (Mathf.Abs(err) < 0.02f) err = 0f;

        // Positive err (too far from wall) -> steer toward wall (+s); negative err -> steer away (-s)
        float wallCmd = s * Mathf.Clamp(err * 2.1f, -0.45f, 0.45f);

        if (diagSame < 0.42f)
        {
            wallCmd -= s * Mathf.Clamp((0.42f - diagSame) * 2.2f, 0f, 0.65f);
        }

        turn = Mathf.Clamp(wallCmd, -1.0f, 1.0f);

        float minClear = Mathf.Min(f, Mathf.Min(diagSame, Mathf.Min(diagOpp, sideSame)));
        float clearScale = Mathf.Clamp01((minClear - 0.16f) / 0.50f);
        float turnScale = 1.0f - 0.40f * Mathf.Abs(turn);
        speed01 = Mathf.Clamp(0.78f * clearScale * turnScale, 0.18f, 0.85f);
    }

    private static float Repulsion(float dist, float radius)
    {
        if (dist >= radius) return 0f;
        float d = Mathf.Max(dist, 0.02f);
        float x = (radius - d) / radius;
        return x * x;
    }

    private void RecordTurnSign(float turn)
    {
        if (Mathf.Abs(turn) > 0.18f && Mathf.Abs(_prevRawTurn) > 0.18f)
        {
            if (Mathf.Sign(turn) != Mathf.Sign(_prevRawTurn))
            {
                _signFlips++;
            }
        }
        _prevRawTurn = turn;
    }

    private float FilterSensor(int sensorIndex, float raw)
    {
        float val = Mathf.Clamp(raw, 0.02f, MaxSensorRange);

        // Guard against occasional false 4.0m readings when recent readings showed a close object
        if (val >= MaxSensorRange - 1e-4f && _histCount[sensorIndex] > 0)
        {
            float avg = ComputeAverage(sensorIndex);
            if (avg < 1.25f)
            {
                val = Mathf.Clamp(avg + 0.12f, 0.02f, 1.6f);
            }
        }

        int idx = _histIdx[sensorIndex];
        _hist[sensorIndex, idx] = val;
        _histIdx[sensorIndex] = (idx + 1) % HistorySize;
        if (_histCount[sensorIndex] < HistorySize)
        {
            _histCount[sensorIndex]++;
        }

        int count = _histCount[sensorIndex];
        float[] sorted = new float[count];
        for (int i = 0; i < count; i++)
        {
            sorted[i] = _hist[sensorIndex, i];
        }
        Array.Sort(sorted);
        float median = sorted[count / 2];

        float mean = ComputeAverage(sensorIndex);
        return Mathf.Clamp(0.65f * median + 0.35f * mean, 0.02f, MaxSensorRange);
    }

    private float ComputeAverage(int sensorIndex)
    {
        int count = _histCount[sensorIndex];
        if (count == 0) return MaxSensorRange;
        float sum = 0f;
        for (int i = 0; i < count; i++)
        {
            sum += _hist[sensorIndex, i];
        }
        return sum / count;
    }
}
