using System;
using UnityEngine;

public class LLM_Gemini_1_OS : IRoverController
{
    private const int HistorySize = 5;
    private const float SensorMaxRange = 4.0f;
    private const float Dt = 0.05f;
    private const float MaxSpeed = 0.30f;
    private const float MaxTurnRateDeg = 90.0f;

    private enum NavMode
    {
        GoalSeek,
        WallFollow
    }

    // Sliding sensor history to mitigate Gaussian noise and occasional false 4.0 readings
    private float[] _frontHist;
    private float[] _leftHist;
    private float[] _rightHist;
    private float[] _leftSideHist;
    private float[] _rightSideHist;
    private int _histCount;
    private int _histIndex;

    // Navigation & escape state
    private NavMode _mode;
    private int _wallSide; // +1 = follow wall on left (keep obstacle on left), -1 = follow wall on right
    private int _modeTicks;
    private int _totalTicks;
    private float _prevTurn;
    private int _oscillationTicks;

    // Progress tracking to escape U-shaped dead ends and local minima
    private float _bestGoalDist;
    private float _goalDistAtModeStart;
    private int _ticksWithoutProgress;
    private float _accumTurnDeg;

    public string Name => "LLM_Controller";

    public void ResetController()
    {
        _frontHist = new float[HistorySize];
        _leftHist = new float[HistorySize];
        _rightHist = new float[HistorySize];
        _leftSideHist = new float[HistorySize];
        _rightSideHist = new float[HistorySize];
        _histCount = 0;
        _histIndex = 0;

        _mode = NavMode.GoalSeek;
        _wallSide = 1;
        _modeTicks = 0;
        _totalTicks = 0;
        _prevTurn = 0f;
        _oscillationTicks = 0;

        _bestGoalDist = float.MaxValue;
        _goalDistAtModeStart = float.MaxValue;
        _ticksWithoutProgress = 0;
        _accumTurnDeg = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        if (_frontHist == null)
        {
            ResetController();
        }

        _totalTicks++;
        _modeTicks++;

        RecordReading(_frontHist, SanitizeSensor(obs.front));
        RecordReading(_leftHist, SanitizeSensor(obs.left));
        RecordReading(_rightHist, SanitizeSensor(obs.right));
        RecordReading(_leftSideHist, SanitizeSensor(obs.leftSide));
        RecordReading(_rightSideHist, SanitizeSensor(obs.rightSide));

        _histIndex = (_histIndex + 1) % HistorySize;
        if (_histCount < HistorySize)
        {
            _histCount++;
        }

        // Filtered distances (median resists occasional false 4.0 spikes and single-tick noise)
        float f = MedianFiltered(_frontHist, _histCount);
        float l = MedianFiltered(_leftHist, _histCount);
        float r = MedianFiltered(_rightHist, _histCount);
        float ls = MedianFiltered(_leftSideHist, _histCount);
        float rs = MedianFiltered(_rightSideHist, _histCount);

        // Also guard against sudden false 4.0 when recent minimum was close
        f = GuardFalseClearance(f, _frontHist, _histCount);
        l = GuardFalseClearance(l, _leftHist, _histCount);
        r = GuardFalseClearance(r, _rightHist, _histCount);
        ls = GuardFalseClearance(ls, _leftSideHist, _histCount);
        rs = GuardFalseClearance(rs, _rightSideHist, _histCount);

        float goalDist = Mathf.Max(0f, obs.goalDistance);
        float goalAngle = Mathf.Clamp(obs.goalAngleDeg, -180f, 180f);

        if (goalDist < _bestGoalDist - 0.04f)
        {
            _bestGoalDist = goalDist;
            _ticksWithoutProgress = 0;
        }
        else
        {
            _ticksWithoutProgress++;
        }

        // Detect left/right oscillation
        if ((_prevTurn > 0.25f && obs.goalAngleDeg < -15f) || (_prevTurn < -0.25f && obs.goalAngleDeg > 15f))
        {
            _oscillationTicks++;
        }
        else if (_oscillationTicks > 0)
        {
            _oscillationTicks--;
        }

        // Forward and angular clearances
        float minFrontCone = Mathf.Min(f, Mathf.Min(l, r));
        bool frontBlocked = f < 0.34f || minFrontCone < 0.25f;
        bool pathTowardGoalClear = IsGoalDirectionClear(goalAngle, f, l, r, ls, rs);

        // If stuck without progress for ~7 seconds (140 ticks), flip wall-follow side to try the other way around a dead end
        if (_ticksWithoutProgress >= 140)
        {
            _wallSide = -_wallSide;
            _ticksWithoutProgress = 0;
            _bestGoalDist = goalDist;
            _mode = NavMode.WallFollow;
            _modeTicks = 0;
            _accumTurnDeg = 0f;
            _goalDistAtModeStart = goalDist;
        }

        if (_mode == NavMode.GoalSeek)
        {
            if (frontBlocked || !pathTowardGoalClear || _oscillationTicks >= 16)
            {
                _mode = NavMode.WallFollow;
                _modeTicks = 0;
                _accumTurnDeg = 0f;
                _oscillationTicks = 0;
                _goalDistAtModeStart = goalDist;
                _wallSide = ChooseInitialWallSide(goalAngle, l, r, ls, rs);
            }
        }
        else // NavMode.WallFollow
        {
            // Exit wall-following back to GoalSeek once we have cleared the obstacle, made meaningful progress or turned out of a pocket,
            // and the direct path toward the goal is open.
            bool clearedPocket = (_accumTurnDeg >= 135f) || (goalDist < _goalDistAtModeStart - 0.18f && _modeTicks >= 16);
            if (_modeTicks >= 12 && !frontBlocked && pathTowardGoalClear && (clearedPocket || _modeTicks >= 55))
            {
                _mode = NavMode.GoalSeek;
                _modeTicks = 0;
                _accumTurnDeg = 0f;
            }
        }

        float speed01;
        float turn;

        if (_mode == NavMode.GoalSeek)
        {
            ComputeGoalSeek(goalDist, goalAngle, f, l, r, ls, rs, out speed01, out turn);
        }
        else
        {
            ComputeWallFollow(goalAngle, f, l, r, ls, rs, out speed01, out turn);
        }

        // Emergency close-range reflex to avoid clipping walls/obstacles due to noise or turn lag
        ApplyCloseProximityReflex(f, l, r, ls, rs, ref speed01, ref turn);

        speed01 = Mathf.Clamp01(speed01);
        turn = Mathf.Clamp(turn, -1f, 1f);

        _accumTurnDeg += Mathf.Abs(turn) * MaxTurnRateDeg * Dt;
        _prevTurn = turn;

        // Ensure we don't stall completely if time is running out and space is open
        if (_totalTicks > 1050 && speed01 < 0.25f && f > 0.28f)
        {
            speed01 = 0.35f;
        }

        return (speed01, turn);
    }

    private void ComputeGoalSeek(
        float goalDist,
        float goalAngle,
        float f,
        float l,
        float r,
        float ls,
        float rs,
        out float speed01,
        out float turn)
    {
        float desiredTurn = Mathf.Clamp(goalAngle / 32f, -1f, 1f);

        // Gentle repulsion from angled/side obstacles when passing near them
        float leftThreat = Mathf.Max(0f, 0.36f - l) / 0.36f + 0.5f * Mathf.Max(0f, 0.24f - ls) / 0.24f;
        float rightThreat = Mathf.Max(0f, 0.36f - r) / 0.36f + 0.5f * Mathf.Max(0f, 0.24f - rs) / 0.24f;
        float avoidanceBias = Mathf.Clamp((rightThreat - leftThreat) * 0.65f, -0.75f, 0.75f);

        // Centering in narrow corridors when both sides are relatively close
        float corridorDiff = 0f;
        if (ls < 0.55f && rs < 0.55f)
        {
            corridorDiff = Mathf.Clamp((ls - rs) * 0.9f, -0.45f, 0.45f);
        }

        turn = Mathf.Clamp(desiredTurn * 0.85f + avoidanceBias + corridorDiff, -1f, 1f);

        // If goal angle is sharp, slow down so turning radius stays tight
        float absAngle = Mathf.Abs(goalAngle);
        float angleFactor = 1.0f - Mathf.Clamp01(absAngle / 110f) * 0.65f;
        float clearanceFactor = Mathf.Clamp01((f - 0.18f) / 0.75f);

        if (goalDist < 0.22f)
        {
            speed01 = Mathf.Clamp(goalDist * 2.2f, 0.15f, 0.55f);
        }
        else
        {
            speed01 = Mathf.Lerp(0.22f, 1.0f, clearanceFactor * angleFactor);
        }
    }

    private void ComputeWallFollow(
        float goalAngle,
        float f,
        float l,
        float r,
        float ls,
        float rs,
        out float speed01,
        out float turn)
    {
        // _wallSide == +1: keep obstacle on the LEFT, turn RIGHT (+turn is left, -turn is right)
        // _wallSide == -1: keep obstacle on the RIGHT, turn LEFT (+turn is left, -turn is right)
        float targetWallDist = 0.25f;

        if (_wallSide > 0)
        {
            float sideDist = Mathf.Min(ls, l + 0.05f);
            if (f < 0.30f || l < 0.24f)
            {
                // Obstacle ahead or forward-left: turn right (-turn)
                float urgency = Mathf.Clamp01((0.36f - Mathf.Min(f, l + 0.05f)) / 0.26f);
                turn = -Mathf.Lerp(0.35f, 1.0f, urgency);
                speed01 = Mathf.Lerp(0.45f, 0.20f, urgency);
            }
            else if (sideDist > 0.48f && f > 0.55f)
            {
                // Lost contact with left wall; steer gently back toward left (+turn) while advancing
                turn = 0.28f;
                speed01 = 0.48f;
            }
            else
            {
                // Maintain steady offset from left wall
                float err = sideDist - targetWallDist;
                turn = Mathf.Clamp(err * 1.6f, -0.55f, 0.55f);
                speed01 = Mathf.Lerp(0.25f, 0.65f, Mathf.Clamp01((f - 0.25f) / 0.60f));
            }
        }
        else
        {
            float sideDist = Mathf.Min(rs, r + 0.05f);
            if (f < 0.30f || r < 0.24f)
            {
                // Obstacle ahead or forward-right: turn left (+turn)
                float urgency = Mathf.Clamp01((0.36f - Mathf.Min(f, r + 0.05f)) / 0.26f);
                turn = Mathf.Lerp(0.35f, 1.0f, urgency);
                speed01 = Mathf.Lerp(0.45f, 0.20f, urgency);
            }
            else if (sideDist > 0.48f && f > 0.55f)
            {
                // Lost contact with right wall; steer gently back toward right (-turn) while advancing
                turn = -0.28f;
                speed01 = 0.48f;
            }
            else
            {
                // Maintain steady offset from right wall
                float err = targetWallDist - sideDist;
                turn = Mathf.Clamp(err * 1.6f, -0.55f, 0.55f);
                speed01 = Mathf.Lerp(0.25f, 0.65f, Mathf.Clamp01((f - 0.25f) / 0.60f));
            }
        }

        // If heading toward goal is now right in front and clear, nudge slightly toward goalAngle
        if (Mathf.Abs(goalAngle) < 25f && f > 0.60f && l > 0.40f && r > 0.40f)
        {
            turn = Mathf.Lerp(turn, Mathf.Clamp(goalAngle / 40f, -0.6f, 0.6f), 0.35f);
        }
    }

    private void ApplyCloseProximityReflex(
        float f,
        float l,
        float r,
        float ls,
        float rs,
        ref float speed01,
        ref float turn)
    {
        float minAny = Mathf.Min(f, Mathf.Min(l, Mathf.Min(r, Mathf.Min(ls, rs))));
        if (minAny < 0.16f)
        {
            speed01 = Mathf.Min(speed01, 0.14f);
        }
        else if (f < 0.24f)
        {
            speed01 = Mathf.Min(speed01, 0.22f);
        }

        // Prevent turning directly into a very close obstacle on that side
        if (turn > 0f && (l < 0.16f || ls < 0.13f))
        {
            turn = Mathf.Min(turn, 0f);
            if (r > 0.22f || rs > 0.18f)
            {
                turn = -0.55f;
            }
        }
        else if (turn < 0f && (r < 0.16f || rs < 0.13f))
        {
            turn = Mathf.Max(turn, 0f);
            if (l > 0.22f || ls > 0.18f)
            {
                turn = 0.55f;
            }
        }
    }

    private static int ChooseInitialWallSide(float goalAngle, float l, float r, float ls, float rs)
    {
        float leftOpenness = l + 0.7f * ls;
        float rightOpenness = r + 0.7f * rs;

        // If one side has much more room, follow the side with the obstacle closer so we don't cross open space
        if (Mathf.Abs(leftOpenness - rightOpenness) > 0.25f)
        {
            return leftOpenness < rightOpenness ? 1 : -1;
        }

        // Otherwise turn away from the goal direction to hug the obstacle on the goal side
        return goalAngle >= 0f ? -1 : 1;
    }

    private static bool IsGoalDirectionClear(float goalAngle, float f, float l, float r, float ls, float rs)
    {
        if (goalAngle > 65f)
        {
            return l > 0.38f && ls > 0.32f;
        }
        if (goalAngle > 20f)
        {
            return l > 0.36f && f > 0.34f;
        }
        if (goalAngle < -65f)
        {
            return r > 0.38f && rs > 0.32f;
        }
        if (goalAngle < -20f)
        {
            return r > 0.36f && f > 0.34f;
        }
        return f > 0.36f && l > 0.26f && r > 0.26f;
    }

    private static float SanitizeSensor(float val)
    {
        if (float.IsNaN(val) || float.IsInfinity(val))
        {
            return SensorMaxRange;
        }
        return Mathf.Clamp(val, 0.02f, SensorMaxRange);
    }

    private void RecordReading(float[] buffer, float value)
    {
        buffer[_histIndex] = value;
    }

    private static float MedianFiltered(float[] buffer, int count)
    {
        if (count <= 0)
        {
            return SensorMaxRange;
        }

        float[] temp = new float[count];
        for (int i = 0; i < count; i++)
        {
            temp[i] = buffer[i];
        }
        Array.Sort(temp);
        return temp[count / 2];
    }

    private static float GuardFalseClearance(float medianVal, float[] buffer, int count)
    {
        if (medianVal < SensorMaxRange - 0.01f || count < 2)
        {
            return medianVal;
        }

        // Check if recent readings were close; if so, a 4.0 reading is likely a false dropout
        float minRecent = SensorMaxRange;
        int nonMaxCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (buffer[i] < SensorMaxRange - 0.01f)
            {
                nonMaxCount++;
                if (buffer[i] < minRecent)
                {
                    minRecent = buffer[i];
                }
            }
        }

        if (nonMaxCount >= 2 && minRecent < 0.75f)
        {
            return minRecent;
        }

        return medianVal;
    }
}
