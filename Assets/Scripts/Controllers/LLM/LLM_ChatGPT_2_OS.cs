using System;
using UnityEngine;

public class LLM_ChatGPT_2_OS : IRoverController
{
    public string Name => "Adaptive Hybrid Rover Navigator";

    // Sensor memory
    private float fFront, fLeft, fRight;
    private float fLeftSide, fRightSide;
    private bool initialized;

    // Navigation state
    private bool avoiding;
    private int avoidDirection;
    private int avoidTicks;
    private int clearTicks;

    // Stuck detection
    private float previousGoalDistance;
    private int progressTicks;
    private int stuckTicks;
    private int recoveryTicks;
    private int recoveryDirection;

    // Exploration memory
    private int totalTicks;
    private int lastTurnDirection;
    private int turnPersistence;

    // Constants
    private const float MAX_RANGE = 4.0f;
    private const float SAFE_FRONT = 0.65f;
    private const float SAFE_DIAGONAL = 0.48f;
    private const float WALL_TARGET = 0.38f;

    public void ResetController()
    {
        fFront = MAX_RANGE;
        fLeft = MAX_RANGE;
        fRight = MAX_RANGE;
        fLeftSide = MAX_RANGE;
        fRightSide = MAX_RANGE;

        initialized = false;

        avoiding = false;
        avoidDirection = 0;
        avoidTicks = 0;
        clearTicks = 0;

        previousGoalDistance = 0f;
        progressTicks = 0;
        stuckTicks = 0;
        recoveryTicks = 0;
        recoveryDirection = 1;

        totalTicks = 0;
        lastTurnDirection = 1;
        turnPersistence = 0;
    }

    private float Clamp(float value, float min, float max)
    {
        return Mathf.Clamp(value, min, max);
    }

    private float Filter(float previous, float current)
    {
        current = Clamp(current, 0.02f, MAX_RANGE);

        // Limit sudden upward jumps caused by false 4m readings.
        float maximumIncrease = 0.30f;

        if (current > previous + maximumIncrease)
            current = previous + maximumIncrease;

        // Exponential smoothing
        return previous * 0.65f + current * 0.35f;
    }

    private void UpdateSensors(Observation obs)
    {
        if (!initialized)
        {
            fFront = Clamp(obs.front, 0.02f, MAX_RANGE);
            fLeft = Clamp(obs.left, 0.02f, MAX_RANGE);
            fRight = Clamp(obs.right, 0.02f, MAX_RANGE);
            fLeftSide = Clamp(obs.leftSide, 0.02f, MAX_RANGE);
            fRightSide = Clamp(obs.rightSide, 0.02f, MAX_RANGE);

            initialized = true;
            return;
        }

        fFront = Filter(fFront, obs.front);
        fLeft = Filter(fLeft, obs.left);
        fRight = Filter(fRight, obs.right);
        fLeftSide = Filter(fLeftSide, obs.leftSide);
        fRightSide = Filter(fRightSide, obs.rightSide);
    }

    private float NormalizeAngle(float angle)
    {
        while (angle > 180f) angle -= 360f;
        while (angle < -180f) angle += 360f;
        return angle;
    }

    private int ChooseAvoidDirection(float goalAngle)
    {
        // Prefer the side with more clearance.
        float leftClearance =
            0.65f * fLeft +
            0.35f * fLeftSide;

        float rightClearance =
            0.65f * fRight +
            0.35f * fRightSide;

        // Account for goal direction.
        leftClearance += Clamp(goalAngle / 180f, -0.3f, 0.3f);
        rightClearance -= Clamp(goalAngle / 180f, -0.3f, 0.3f);

        // Small deterministic preference to prevent oscillation.
        if (Mathf.Abs(leftClearance - rightClearance) < 0.12f)
        {
            return goalAngle >= 0f ? 1 : -1;
        }

        return leftClearance > rightClearance ? 1 : -1;
    }

    private float WallFollowSteering()
    {
        float sideDistance =
            avoidDirection > 0 ? fLeftSide : fRightSide;

        float diagonalDistance =
            avoidDirection > 0 ? fLeft : fRight;

        // Maintain a reasonable distance from the wall.
        float wallError = WALL_TARGET - sideDistance;

        float steering =
            avoidDirection * 0.40f +
            avoidDirection * wallError * 1.15f;

        // Front diagonal obstacle avoidance.
        if (diagonalDistance < 0.65f)
        {
            float correction =
                Clamp((0.65f - diagonalDistance) * 1.8f,
                      0f, 0.9f);

            steering -= avoidDirection * correction;
        }

        // Emergency frontal obstacle avoidance.
        if (fFront < 0.40f)
        {
            steering = -avoidDirection;
        }

        return Clamp(steering, -1f, 1f);
    }

    private float CalculateSpeed(float steering)
    {
        float speed = 0.85f;

        // Slow down when steering sharply.
        speed -= Mathf.Abs(steering) * 0.30f;

        // Front obstacle proximity.
        if (fFront < 1.0f)
        {
            speed = Mathf.Min(
                speed,
                0.20f + fFront * 0.40f
            );
        }

        // Diagonal obstacle proximity.
        float nearestDiagonal = Mathf.Min(fLeft, fRight);

        if (nearestDiagonal < 0.65f)
        {
            speed = Mathf.Min(
                speed,
                0.25f + nearestDiagonal * 0.35f
            );
        }

        // Extremely close obstacle.
        if (fFront < 0.25f ||
            fLeft < 0.22f ||
            fRight < 0.22f)
        {
            speed = 0.0f;
        }

        return Clamp(speed, 0.08f, 0.85f);
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        totalTicks++;

        UpdateSensors(obs);

        float goalAngle = NormalizeAngle(obs.goalAngleDeg);
        float goalDistance = obs.goalDistance;

        // Goal reached or sufficiently close.
        if (goalDistance < 0.15f)
        {
            return (0f, 0f);
        }

        // ------------------------------------------------
        // 1. Progress and stuck detection
        // ------------------------------------------------

        if (totalTicks > 1)
        {
            if (goalDistance < previousGoalDistance - 0.008f)
            {
                progressTicks++;
                stuckTicks = 0;
            }
            else
            {
                stuckTicks++;
                progressTicks = 0;
            }
        }

        previousGoalDistance = goalDistance;

        // ------------------------------------------------
        // 2. Recovery behaviour
        // ------------------------------------------------

        if (stuckTicks > 100 && recoveryTicks == 0)
        {
            recoveryTicks = 55;

            // Try the opposite direction.
            recoveryDirection = -recoveryDirection;
            stuckTicks = 0;

            avoiding = true;
            avoidDirection = recoveryDirection;
            avoidTicks = 0;
        }

        if (recoveryTicks > 0)
        {
            recoveryTicks--;

            float recoveryTurn = recoveryDirection;

            // Continue turning away from immediate obstacles.
            if (recoveryDirection > 0 && fLeft < 0.35f)
                recoveryTurn = -1f;

            if (recoveryDirection < 0 && fRight < 0.35f)
                recoveryTurn = 1f;

            float recoverySpeed = 0.12f;

            if (fFront < 0.30f)
                recoverySpeed = 0.0f;

            return (
                recoverySpeed,
                Clamp(recoveryTurn, -1f, 1f)
            );
        }

        // ------------------------------------------------
        // 3. Obstacle detection
        // ------------------------------------------------

        bool frontBlocked = fFront < SAFE_FRONT;

        bool leftBlocked = fLeft < SAFE_DIAGONAL;
        bool rightBlocked = fRight < SAFE_DIAGONAL;

        bool emergency =
            fFront < 0.32f ||
            fLeft < 0.25f ||
            fRight < 0.25f;

        // Enter obstacle avoidance.
        if (!avoiding &&
            (frontBlocked || leftBlocked || rightBlocked))
        {
            avoiding = true;
            avoidDirection = ChooseAvoidDirection(goalAngle);
            avoidTicks = 0;
            clearTicks = 0;
        }

        // ------------------------------------------------
        // 4. Wall-following mode
        // ------------------------------------------------

        if (avoiding)
        {
            avoidTicks++;

            float wallSteering = WallFollowSteering();

            bool pathClear =
                fFront > 1.05f &&
                fLeft > 0.75f &&
                fRight > 0.75f;

            // Require several consecutive clear readings.
            if (pathClear &&
                Mathf.Abs(goalAngle) < 65f &&
                avoidTicks > 35)
            {
                clearTicks++;
            }
            else
            {
                clearTicks = 0;
            }

            // Leave obstacle-following mode when a safe
            // direction toward the goal becomes available.
            if (clearTicks >= 12)
            {
                avoiding = false;
                clearTicks = 0;
                avoidTicks = 0;
            }
            else
            {
                float speed = CalculateSpeed(wallSteering);

                if (emergency)
                    speed = 0f;

                return (
                    speed,
                    Clamp(wallSteering, -1f, 1f)
                );
            }
        }

        // ------------------------------------------------
        // 5. Normal goal navigation
        // ------------------------------------------------

        float angleRadians = goalAngle * Mathf.Deg2Rad;

        // Goal-directed steering.
        float goalSteering = Clamp(
            goalAngle / 65f,
            -1f,
            1f
        );

        // ------------------------------------------------
        // 6. Obstacle repulsion
        // ------------------------------------------------

        float leftRepulsion = 0f;
        float rightRepulsion = 0f;

        if (fFront < 1.2f)
        {
            float strength =
                (1.2f - fFront) / 1.2f;

            leftRepulsion += strength * 0.8f;
            rightRepulsion -= strength * 0.8f;
        }

        if (fLeft < 1.0f)
        {
            leftRepulsion -=
                (1.0f - fLeft) * 1.2f;
        }

        if (fRight < 1.0f)
        {
            rightRepulsion +=
                (1.0f - fRight) * 1.2f;
        }

        // Side clearance corrections.
        if (fLeftSide < 0.35f)
        {
            leftRepulsion -=
                (0.35f - fLeftSide) * 1.5f;
        }

        if (fRightSide < 0.35f)
        {
            rightRepulsion +=
                (0.35f - fRightSide) * 1.5f;
        }

        float obstacleSteering =
            leftRepulsion + rightRepulsion;

        // ------------------------------------------------
        // 7. Combined steering
        // ------------------------------------------------

        float finalSteering =
            goalSteering * 0.65f +
            obstacleSteering * 0.85f;

        // Stronger correction for close frontal obstacles.
        if (fFront < 0.65f)
        {
            finalSteering +=
                goalAngle >= 0f ? 0.35f : -0.35f;
        }

        finalSteering = Clamp(
            finalSteering,
            -1f,
            1f
        );

        // ------------------------------------------------
        // 8. Turn stability
        // ------------------------------------------------

        int currentDirection =
            finalSteering >= 0f ? 1 : -1;

        if (currentDirection == lastTurnDirection)
        {
            turnPersistence++;
        }
        else
        {
            turnPersistence = 0;
            lastTurnDirection = currentDirection;
        }

        // ------------------------------------------------
        // 9. Adaptive speed
        // ------------------------------------------------

        float speed = CalculateSpeed(finalSteering);

        // Reduce speed for large heading corrections.
        if (Mathf.Abs(goalAngle) > 80f)
        {
            speed = Mathf.Min(speed, 0.30f);
        }

        // Avoid driving quickly when approaching the goal.
        if (goalDistance < 0.60f)
        {
            speed = Mathf.Min(speed, 0.30f);
        }

        // Turn in place when the goal is behind the rover.
        if (Mathf.Abs(goalAngle) > 125f)
        {
            speed = 0f;
        }

        // Emergency obstacle protection.
        if (emergency)
        {
            speed = 0f;

            if (fLeft < fRight)
                finalSteering = -1f;
            else
                finalSteering = 1f;
        }

        // Final output constraints.
        speed = Clamp(speed, 0f, 1f);
        finalSteering = Clamp(finalSteering, -1f, 1f);

        return (speed, finalSteering);
    }
}
