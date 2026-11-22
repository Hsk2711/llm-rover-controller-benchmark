using System;
using UnityEngine;

public class LLM_ChatGPT_3_FB : IRoverController
{
    public string Name => "Adaptive Safe Navigation V2";

    private const float MAX_RANGE = 4.0f;

    // Conservative clearance thresholds.
    private const float FRONT_SAFE = 0.75f;
    private const float SIDE_SAFE = 0.48f;
    private const float DIAGONAL_SAFE = 0.65f;

    private const float EMERGENCY_FRONT = 0.30f;
    private const float EMERGENCY_SIDE = 0.23f;

    private float frontF;
    private float leftF;
    private float rightF;
    private float leftSideF;
    private float rightSideF;

    private bool initialized;

    private float lastTurn;

    private int stuckCounter;
    private int escapeCounter;
    private int clearCounter;

    private float bestGoalDistance;

    private int preferredTurn;

    private bool wallFollowing;
    private bool escaping;

    public void ResetController()
    {
        frontF = MAX_RANGE;
        leftF = MAX_RANGE;
        rightF = MAX_RANGE;
        leftSideF = MAX_RANGE;
        rightSideF = MAX_RANGE;

        initialized = false;

        lastTurn = 0f;

        stuckCounter = 0;
        escapeCounter = 0;
        clearCounter = 0;

        bestGoalDistance = float.MaxValue;

        preferredTurn = 1;

        wallFollowing = false;
        escaping = false;
    }

    private float ClampRange(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return MAX_RANGE;

        return Mathf.Clamp(value, 0.02f, MAX_RANGE);
    }

    // React quickly to obstacles.
    // Recover slowly from suspicious sudden increases.
    private float Filter(float previous, float current)
    {
        current = ClampRange(current);

        if (!initialized)
            return current;

        if (current < previous)
            return Mathf.Lerp(previous, current, 0.75f);

        return Mathf.Min(
            current,
            previous + 0.12f
        );
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

    private float Risk(float distance, float safeDistance)
    {
        if (distance >= safeDistance)
            return 0f;

        return Mathf.Clamp01(
            (safeDistance - distance) / safeDistance
        );
    }

    private float LeftClearance()
    {
        return Mathf.Min(
            leftF,
            leftSideF
        );
    }

    private float RightClearance()
    {
        return Mathf.Min(
            rightF,
            rightSideF
        );
    }

    // Determines which direction offers more room.
    private int BestTurningDirection()
    {
        float leftScore =
            Mathf.Min(leftF, leftSideF * 1.15f);

        float rightScore =
            Mathf.Min(rightF, rightSideF * 1.15f);

        if (leftScore > rightScore + 0.08f)
            return 1;

        if (rightScore > leftScore + 0.08f)
            return -1;

        return preferredTurn;
    }

    // Evaluate danger on the side toward which the rover turns.
    private float TurningRisk(int direction)
    {
        if (direction > 0)
        {
            return Mathf.Max(
                Risk(leftF, DIAGONAL_SAFE),
                Risk(leftSideF, SIDE_SAFE) * 0.9f
            );
        }

        return Mathf.Max(
            Risk(rightF, DIAGONAL_SAFE),
            Risk(rightSideF, SIDE_SAFE) * 0.9f
        );
    }

    // Obstacle avoidance using all five sensors.
    private float CalculateAvoidance()
    {
        float frontRisk = Risk(frontF, FRONT_SAFE);

        float leftRisk = Risk(leftF, DIAGONAL_SAFE);
        float rightRisk = Risk(rightF, DIAGONAL_SAFE);

        float leftSideRisk = Risk(leftSideF, SIDE_SAFE);
        float rightSideRisk = Risk(rightSideF, SIDE_SAFE);

        float turn = 0f;

        // Obstacles on the left push the rover right.
        turn -= leftRisk * 0.75f;
        turn -= leftSideRisk * 0.65f;

        // Obstacles on the right push the rover left.
        turn += rightRisk * 0.75f;
        turn += rightSideRisk * 0.65f;

        // Direct frontal obstacle.
        if (frontRisk > 0.05f)
        {
            int direction = BestTurningDirection();

            turn = direction *
                   (0.65f + frontRisk * 0.35f);
        }

        return Mathf.Clamp(turn, -1f, 1f);
    }

    // Check whether the requested turning direction is safe.
    private float ProtectTurning(float turn)
    {
        if (Mathf.Abs(turn) < 0.08f)
            return turn;

        int direction = turn > 0f ? 1 : -1;

        float risk = TurningRisk(direction);

        if (risk > 0.55f)
        {
            int alternative = -direction;

            float alternativeRisk =
                TurningRisk(alternative);

            if (alternativeRisk < risk - 0.10f)
            {
                preferredTurn = alternative;

                return alternative * Mathf.Max(
                    0.65f,
                    Mathf.Abs(turn)
                );
            }

            // Both directions are restricted.
            // Stop forward motion and turn cautiously.
            return direction * 0.35f;
        }

        return turn;
    }

    private void UpdateProgress(float distance)
    {
        if (distance < bestGoalDistance - 0.04f)
        {
            bestGoalDistance = distance;
            stuckCounter = 0;
        }
        else
        {
            stuckCounter++;
        }

        // Detect lack of progress over approximately four seconds.
        if (stuckCounter > 80 && !escaping)
        {
            escaping = true;
            wallFollowing = true;

            escapeCounter = 0;

            preferredTurn = BestTurningDirection();
        }

        if (escaping)
        {
            escapeCounter++;

            if (escapeCounter > 28)
            {
                escaping = false;
                stuckCounter = 0;
                bestGoalDistance = distance;
            }
        }
    }

    private float CalculateSpeed(float turn)
    {
        float speed = 1.0f;

        float frontRisk = Risk(frontF, FRONT_SAFE);

        // Reduce speed as the front obstacle approaches.
        speed *= 1f - frontRisk * 0.85f;

        // Turning requires additional clearance.
        float turnAmount = Mathf.Abs(turn);

        if (turnAmount > 0.25f)
            speed = Mathf.Min(speed, 0.35f);

        if (turnAmount > 0.45f)
            speed = Mathf.Min(speed, 0.18f);

        if (turnAmount > 0.70f)
            speed = Mathf.Min(speed, 0.08f);

        // Critical front clearance: stop and rotate.
        if (frontF < EMERGENCY_FRONT)
            speed = 0f;

        // Extremely close side clearance.
        if (leftSideF < EMERGENCY_SIDE ||
            rightSideF < EMERGENCY_SIDE)
        {
            speed = 0f;
        }

        // Do not drive forward into a blocked frontal corridor.
        if (frontF < 0.42f)
            speed = 0f;

        if (escaping)
            speed = 0f;

        return Mathf.Clamp(speed, 0f, 1f);
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float goalDistance = Mathf.Max(
            0f,
            obs.goalDistance
        );

        float goalAngle = Mathf.Clamp(
            obs.goalAngleDeg,
            -180f,
            180f
        );

        // Goal reached.
        if (goalDistance < 0.15f)
            return (0f, 0f);

        UpdateProgress(goalDistance);

        float goalTurn = Mathf.Clamp(
            goalAngle / 90f,
            -1f,
            1f
        );

        float avoidanceTurn = CalculateAvoidance();

        float frontRisk = Risk(frontF, FRONT_SAFE);

        float turn;

        // Recovery from a possible dead end.
        if (escaping)
        {
            turn = preferredTurn * 0.90f;

            lastTurn = turn;

            return (0f, turn);
        }

        // Detect obstacles along the desired direction.
        bool blocked =
            (Mathf.Abs(goalAngle) < 40f &&
             frontF < 0.85f) ||

            (goalAngle > 20f &&
             leftF < 0.55f) ||

            (goalAngle < -20f &&
             rightF < 0.55f);

        if (blocked && !wallFollowing)
        {
            wallFollowing = true;
            preferredTurn = BestTurningDirection();
        }

        // Wall-following release condition.
        if (wallFollowing)
        {
            if (frontF > 1.15f &&
                leftF > 0.85f &&
                rightF > 0.85f &&
                Mathf.Abs(goalAngle) < 35f)
            {
                clearCounter++;
            }
            else
            {
                clearCounter = 0;
            }

            if (clearCounter > 20)
            {
                wallFollowing = false;
                clearCounter = 0;
            }
        }

        if (wallFollowing)
        {
            // Prefer the direction with more clearance.
            float direction =
                preferredTurn;

            float wallTurn = direction * 0.55f;

            // If frontal space is clear, gradually return
            // toward the goal.
            if (frontF > 1.0f)
            {
                turn = wallTurn * 0.45f +
                       goalTurn * 0.55f;
            }
            else
            {
                turn = wallTurn;
            }
        }
        else
        {
            // Normal goal-directed navigation.
            float goalWeight =
                1f - frontRisk * 0.65f;

            float obstacleWeight =
                1f - goalWeight;

            turn =
                goalTurn * goalWeight +
                avoidanceTurn * obstacleWeight;
        }

        // Emergency frontal obstacle response.
        if (frontF < 0.55f)
        {
            turn = BestTurningDirection();
        }

        // Prevent steering into an obstacle.
        turn = ProtectTurning(turn);

        // Smooth normal steering.
        bool emergency =
            frontF < 0.55f ||
            leftSideF < EMERGENCY_SIDE ||
            rightSideF < EMERGENCY_SIDE;

        if (!emergency)
        {
            turn = Mathf.Lerp(
                lastTurn,
                turn,
                0.70f
            );
        }

        turn = Mathf.Clamp(turn, -1f, 1f);

        lastTurn = turn;

        // Adaptive speed control.
        float speed = CalculateSpeed(turn);

        // Large goal-angle changes: turn first, then move.
        if (Mathf.Abs(goalAngle) > 100f)
            speed = Mathf.Min(speed, 0.08f);

        if (Mathf.Abs(goalAngle) > 145f)
            speed = 0f;

        return (speed, turn);
    }
}
