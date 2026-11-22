using System;
using UnityEngine;

public class LLM_ChatGPT_3_OS : IRoverController
{
    public string Name => "Adaptive Goal Navigation";

    private const float MAX_RANGE = 4.0f;
    private const float SAFE_DISTANCE = 0.32f;
    private const float CRITICAL_DISTANCE = 0.19f;
    private const float MAX_SPEED = 1.0f;

    private float frontF, leftF, rightF;
    private float leftSideF, rightSideF;

    private bool initialized;

    private int stuckTimer;
    private int clearTimer;
    private int escapeTimer;

    private float bestGoalDistance;
    private float previousGoalDistance;

    private int preferredSide;
    private int wallFollowSide;

    private bool wallFollowing;
    private bool escaping;

    private float lastTurn;

    public void ResetController()
    {
        frontF = MAX_RANGE;
        leftF = MAX_RANGE;
        rightF = MAX_RANGE;
        leftSideF = MAX_RANGE;
        rightSideF = MAX_RANGE;

        initialized = false;

        stuckTimer = 0;
        clearTimer = 0;
        escapeTimer = 0;

        bestGoalDistance = float.MaxValue;
        previousGoalDistance = float.MaxValue;

        preferredSide = 1;
        wallFollowSide = 1;

        wallFollowing = false;
        escaping = false;

        lastTurn = 0f;
    }

    private float ClampRange(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return MAX_RANGE;

        return Mathf.Clamp(value, 0.02f, MAX_RANGE);
    }

    // Asymmetric filtering rejects occasional false maximum readings.
    private float Filter(float previous, float current)
    {
        current = ClampRange(current);

        if (!initialized)
            return current;

        if (current > previous + 0.8f)
            return Mathf.Min(previous + 0.12f, current);

        float alpha = current < previous ? 0.55f : 0.30f;

        return Mathf.Lerp(previous, current, alpha);
    }

    private float NormalizeAngle(float angle)
    {
        return Mathf.Clamp(angle / 90f, -1f, 1f);
    }

    private float ObstacleStrength(float distance)
    {
        if (distance >= SAFE_DISTANCE)
            return 0f;

        if (distance <= CRITICAL_DISTANCE)
            return 1f;

        return (SAFE_DISTANCE - distance) /
               (SAFE_DISTANCE - CRITICAL_DISTANCE);
    }

    private float GetLeftClearance()
    {
        return Mathf.Min(leftF, leftSideF);
    }

    private float GetRightClearance()
    {
        return Mathf.Min(rightF, rightSideF);
    }

    private void UpdateSensors(Observation obs)
    {
        float f = ClampRange(obs.front);
        float l = ClampRange(obs.left);
        float r = ClampRange(obs.right);
        float ls = ClampRange(obs.leftSide);
        float rs = ClampRange(obs.rightSide);

        if (!initialized)
        {
            frontF = f;
            leftF = l;
            rightF = r;
            leftSideF = ls;
            rightSideF = rs;

            initialized = true;
            return;
        }

        frontF = Filter(frontF, f);
        leftF = Filter(leftF, l);
        rightF = Filter(rightF, r);
        leftSideF = Filter(leftSideF, ls);
        rightSideF = Filter(rightSideF, rs);
    }

    private void UpdateProgress(float goalDistance)
    {
        if (goalDistance < bestGoalDistance - 0.035f)
        {
            bestGoalDistance = goalDistance;
            stuckTimer = 0;
        }
        else
        {
            stuckTimer++;
        }

        previousGoalDistance = goalDistance;

        // Approximately 1.5 seconds without meaningful progress.
        if (stuckTimer > 30 && !escaping)
        {
            escaping = true;
            escapeTimer = 0;

            wallFollowSide =
                GetLeftClearance() >= GetRightClearance() ? 1 : -1;

            wallFollowing = true;
        }

        if (escaping)
        {
            escapeTimer++;

            if (escapeTimer > 35)
            {
                escaping = false;
                stuckTimer = 0;
            }
        }
    }

    private float CalculateObstacleTurn()
    {
        float leftDanger = ObstacleStrength(leftF);
        float rightDanger = ObstacleStrength(rightF);

        float leftSideDanger = ObstacleStrength(leftSideF);
        float rightSideDanger = ObstacleStrength(rightSideF);

        float frontDanger = ObstacleStrength(frontF);

        // Positive means turn left; negative means turn right.
        float turn = 0f;

        turn += (rightDanger - leftDanger) * 0.85f;

        turn += (rightSideDanger - leftSideDanger) * 0.55f;

        // Strong avoidance when something is directly ahead.
        if (frontDanger > 0f)
        {
            float leftClearance = GetLeftClearance();
            float rightClearance = GetRightClearance();

            float difference = leftClearance - rightClearance;

            if (Mathf.Abs(difference) < 0.12f)
                difference = wallFollowSide * 0.25f;

            turn += Mathf.Sign(difference) *
                    (0.55f + frontDanger * 0.65f);
        }

        // Maintain distance from nearby side walls.
        if (leftSideF < 0.38f)
            turn -= ObstacleStrength(leftSideF) * 0.45f;

        if (rightSideF < 0.38f)
            turn += ObstacleStrength(rightSideF) * 0.45f;

        return Mathf.Clamp(turn, -1f, 1f);
    }

    private float CalculateWallFollowTurn()
    {
        float left = GetLeftClearance();
        float right = GetRightClearance();

        float sideDistance = wallFollowSide == 1 ? left : right;

        float targetDistance = 0.42f;

        float correction = Mathf.Clamp(
            (sideDistance - targetDistance) * 1.5f,
            -0.55f,
            0.55f
        );

        // Follow the selected wall while keeping a safe distance.
        float turn = -wallFollowSide * correction;

        // Avoid frontal obstacles during wall following.
        if (frontF < 0.65f)
        {
            float leftClearance = GetLeftClearance();
            float rightClearance = GetRightClearance();

            turn = leftClearance >= rightClearance
                ? 0.85f
                : -0.85f;
        }

        return Mathf.Clamp(turn, -1f, 1f);
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        if (obs == null)
            return (0f, 0f);

        UpdateSensors(obs);

        float goalDistance = Mathf.Max(0f, obs.goalDistance);

        float goalAngle = Mathf.Clamp(
            obs.goalAngleDeg, -180f, 180f
        );

        // Goal reached or extremely close.
        if (goalDistance < 0.15f)
            return (0f, 0f);

        UpdateProgress(goalDistance);

        float goalTurn = NormalizeAngle(goalAngle);

        float obstacleTurn = CalculateObstacleTurn();

        float leftClearance = GetLeftClearance();
        float rightClearance = GetRightClearance();

        float frontDanger = ObstacleStrength(frontF);

        // Determine whether direct movement toward the goal is safe.
        bool goalDirectionBlocked =
            (Mathf.Abs(goalAngle) < 35f && frontF < 0.65f) ||
            (goalAngle > 25f && leftF < 0.45f) ||
            (goalAngle < -25f && rightF < 0.45f);

        if (goalDirectionBlocked && !wallFollowing)
        {
            wallFollowing = true;

            wallFollowSide =
                leftClearance >= rightClearance ? 1 : -1;
        }

        // Return to goal-directed navigation when sufficient clearance
        // is available.
        if (wallFollowing)
        {
            if (frontF > 1.0f &&
                leftF > 0.65f &&
                rightF > 0.65f &&
                Mathf.Abs(goalAngle) < 40f)
            {
                clearTimer++;
            }
            else
            {
                clearTimer = 0;
            }

            if (clearTimer > 12 && !escaping)
            {
                wallFollowing = false;
                clearTimer = 0;
                stuckTimer = 0;
            }
        }

        float turn;

        if (escaping)
        {
            // Escape a possible dead end by turning toward the
            // side with more available clearance.
            turn = wallFollowSide * 0.85f;

            if (frontF < 0.30f)
                turn = wallFollowSide * 1.0f;
        }
        else if (wallFollowing)
        {
            float wallTurn = CalculateWallFollowTurn();

            // Gradually incorporate the goal direction.
            turn = wallTurn * 0.75f + goalTurn * 0.20f;
        }
        else
        {
            // Combine goal seeking and obstacle avoidance.
            float goalWeight = 0.75f - frontDanger * 0.40f;
            float obstacleWeight = 1.0f - goalWeight;

            turn = goalTurn * goalWeight +
                   obstacleTurn * obstacleWeight;

            // Avoid turning too sharply toward a nearby obstacle.
            if (frontDanger > 0.5f)
                turn = obstacleTurn;
        }

        turn = Mathf.Clamp(turn, -1f, 1f);

        // Smooth steering to reduce oscillation.
        turn = Mathf.Lerp(lastTurn, turn, 0.65f);

        lastTurn = turn;

        // Adaptive speed based on obstacle proximity and turning.
        float speed = MAX_SPEED;

        speed *= 1f - frontDanger * 0.85f;

        speed *= 1f - Mathf.Abs(turn) * 0.55f;

        if (Mathf.Abs(goalAngle) > 75f)
            speed *= 0.45f;

        if (Mathf.Abs(goalAngle) > 120f)
            speed *= 0.30f;

        if (frontF < 0.35f)
            speed = Mathf.Min(speed, 0.12f);

        if (frontF < 0.22f)
            speed = 0f;

        // Be conservative when both diagonal directions are blocked.
        if (leftF < 0.25f && rightF < 0.25f)
            speed = 0f;

        if (escaping)
            speed = Mathf.Min(speed, 0.20f);

        if (wallFollowing)
            speed = Mathf.Min(speed, 0.45f);

        speed = Mathf.Clamp(speed, 0f, 1f);

        return (speed, turn);
    }
}
