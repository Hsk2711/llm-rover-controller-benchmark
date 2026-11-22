using System;
using UnityEngine;

public class LLM_ChatGPT_6_OS : IRoverController
{
    public string Name => "Adaptive Rover Navigation";

    // Sensor filtering
    private float fFront, fLeft, fRight, fLeftSide, fRightSide;
    private bool initialized;

    // Navigation state
    private int stuckCounter;
    private int wallFollowCounter;
    private int preferredDirection;
    private int directionLockCounter;

    private float previousGoalDistance;
    private float bestGoalDistance;
    private int progressCounter;

    private float lastTurn;
    private int recoveryCounter;

    // Constants
    private const float MAX_RANGE = 4.0f;
    private const float SAFE_DISTANCE = 0.42f;
    private const float CRITICAL_DISTANCE = 0.24f;
    private const float MIN_SPEED = 0.08f;

    public void ResetController()
    {
        fFront = MAX_RANGE;
        fLeft = MAX_RANGE;
        fRight = MAX_RANGE;
        fLeftSide = MAX_RANGE;
        fRightSide = MAX_RANGE;

        initialized = false;

        stuckCounter = 0;
        wallFollowCounter = 0;
        preferredDirection = 0;
        directionLockCounter = 0;

        previousGoalDistance = 0f;
        bestGoalDistance = float.MaxValue;
        progressCounter = 0;

        lastTurn = 0f;
        recoveryCounter = 0;
    }

    // Sensor filter:
    // React quickly to closer obstacles but slowly to sudden increases.
    // This helps reject occasional false 4.0 m readings.
    private float FilterSensor(float raw, float previous)
    {
        raw = Mathf.Clamp(raw, 0.02f, MAX_RANGE);

        if (raw < previous)
        {
            return Mathf.Lerp(previous, raw, 0.85f);
        }

        return Mathf.Min(raw, previous + 0.045f);
    }

    private float ClampTurn(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }

    private float NormalizeAngle(float angle)
    {
        while (angle > 180f) angle -= 360f;
        while (angle < -180f) angle += 360f;
        return angle;
    }

    // Obstacle repulsion.
    // Sensors on the left produce positive steering.
    // Sensors on the right produce negative steering.
    private float CalculateAvoidance()
    {
        float avoidance = 0f;

        float frontDanger = Mathf.Clamp01(
            (SAFE_DISTANCE - fFront) / SAFE_DISTANCE
        );

        float leftDanger = Mathf.Clamp01(
            (SAFE_DISTANCE - fLeft) / SAFE_DISTANCE
        );

        float rightDanger = Mathf.Clamp01(
            (SAFE_DISTANCE - fRight) / SAFE_DISTANCE
        );

        float leftSideDanger = Mathf.Clamp01(
            (SAFE_DISTANCE - fLeftSide) / SAFE_DISTANCE
        );

        float rightSideDanger = Mathf.Clamp01(
            (SAFE_DISTANCE - fRightSide) / SAFE_DISTANCE
        );

        // Front obstacles: choose the more open direction.
        if (frontDanger > 0f)
        {
            float leftOpen =
                0.65f * fLeft + 0.35f * fLeftSide;

            float rightOpen =
                0.65f * fRight + 0.35f * fRightSide;

            float openDifference = Mathf.Clamp(
                (leftOpen - rightOpen) / 2f,
                -1f, 1f
            );

            // Avoid directly facing obstacles.
            avoidance += openDifference *
                         (0.65f + 0.85f * frontDanger);
        }

        // Forward diagonal obstacle avoidance.
        avoidance += leftDanger * -0.65f;
        avoidance += rightDanger * 0.65f;

        // Side obstacle avoidance.
        avoidance += leftSideDanger * -0.42f;
        avoidance += rightSideDanger * 0.42f;

        return Mathf.Clamp(avoidance, -1f, 1f);
    }

    // Select a wall-following direction.
    // A positive value means follow the left side.
    private int ChooseDirection()
    {
        float leftSpace = fLeft + fLeftSide;
        float rightSpace = fRight + fRightSide;

        if (Mathf.Abs(leftSpace - rightSpace) < 0.15f)
        {
            return preferredDirection == 0 ? 1 : preferredDirection;
        }

        return leftSpace > rightSpace ? 1 : -1;
    }

    private float CalculateWallFollowing()
    {
        float direction = preferredDirection;

        float sideDistance = direction > 0
            ? fLeftSide
            : fRightSide;

        float diagonalDistance = direction > 0
            ? fLeft
            : fRight;

        // Maintain a reasonable distance from the selected wall.
        float desiredDistance = 0.48f;

        float distanceError = sideDistance - desiredDistance;

        float wallCorrection = Mathf.Clamp(
            distanceError * 1.5f,
            -0.65f,
            0.65f
        );

        // Turn away from a nearby side wall.
        float sideSteering = direction * wallCorrection;

        // Avoid an obstacle directly ahead.
        float frontCorrection = 0f;

        if (fFront < 0.75f)
        {
            float leftSpace =
                0.5f * fLeft + 0.5f * fLeftSide;

            float rightSpace =
                0.5f * fRight + 0.5f * fRightSide;

            frontCorrection =
                Mathf.Sign(leftSpace - rightSpace) * 0.8f;
        }

        // Keep the rover from drifting into diagonal obstacles.
        float diagonalCorrection =
            direction * Mathf.Clamp(
                (diagonalDistance - 0.65f) * 0.5f,
                -0.4f,
                0.4f
            );

        return Mathf.Clamp(
            sideSteering + frontCorrection + diagonalCorrection,
            -1f,
            1f
        );
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        // -------------------------------------------------
        // 1. Filter all sensor measurements
        // -------------------------------------------------

        if (!initialized)
        {
            fFront = obs.front;
            fLeft = obs.left;
            fRight = obs.right;
            fLeftSide = obs.leftSide;
            fRightSide = obs.rightSide;

            initialized = true;
        }
        else
        {
            fFront = FilterSensor(obs.front, fFront);
            fLeft = FilterSensor(obs.left, fLeft);
            fRight = FilterSensor(obs.right, fRight);
            fLeftSide = FilterSensor(obs.leftSide, fLeftSide);
            fRightSide = FilterSensor(obs.rightSide, fRightSide);
        }

        // -------------------------------------------------
        // 2. Goal direction
        // -------------------------------------------------

        float goalAngle = NormalizeAngle(obs.goalAngleDeg);

        float goalSteering = Mathf.Clamp(
            goalAngle / 65f,
            -1f,
            1f
        );

        float goalDistance = Mathf.Max(0f, obs.goalDistance);

        // Stop when sufficiently close to the goal.
        if (goalDistance < 0.13f)
        {
            return (0f, 0f);
        }

        // -------------------------------------------------
        // 3. Detect lack of progress
        // -------------------------------------------------

        if (goalDistance < bestGoalDistance - 0.025f)
        {
            bestGoalDistance = goalDistance;
            progressCounter = 0;
        }
        else
        {
            progressCounter++;
        }

        // Approximately 2 seconds without improvement.
        if (progressCounter > 40)
        {
            stuckCounter++;
            progressCounter = 0;
            bestGoalDistance = goalDistance;

            preferredDirection = ChooseDirection();

            wallFollowCounter = 100;
            directionLockCounter = 60;
        }

        // -------------------------------------------------
        // 4. Detect immediate obstacles
        // -------------------------------------------------

        bool frontBlocked =
            fFront < 0.55f;

        bool leftBlocked =
            fLeft < 0.35f || fLeftSide < 0.28f;

        bool rightBlocked =
            fRight < 0.35f || fRightSide < 0.28f;

        // -------------------------------------------------
        // 5. Enter obstacle-following mode
        // -------------------------------------------------

        if (wallFollowCounter > 0)
        {
            wallFollowCounter--;
        }

        if (frontBlocked && wallFollowCounter <= 0)
        {
            preferredDirection = ChooseDirection();

            wallFollowCounter = 65;
            directionLockCounter = 40;
        }

        if (directionLockCounter > 0)
        {
            directionLockCounter--;
        }
        else if (wallFollowCounter > 0)
        {
            // Allow direction changes if the selected side
            // becomes significantly more obstructed.
            if (preferredDirection > 0 && leftBlocked &&
                !rightBlocked)
            {
                preferredDirection = -1;
                directionLockCounter = 30;
            }
            else if (preferredDirection < 0 && rightBlocked &&
                     !leftBlocked)
            {
                preferredDirection = 1;
                directionLockCounter = 30;
            }
        }

        // -------------------------------------------------
        // 6. Calculate steering
        // -------------------------------------------------

        float avoidanceSteering = CalculateAvoidance();

        float turn;

        if (wallFollowCounter > 0)
        {
            float wallSteering = CalculateWallFollowing();

            // Retain some goal direction to avoid following
            // walls unnecessarily.
            turn =
                0.75f * wallSteering +
                0.25f * goalSteering;
        }
        else
        {
            turn =
                0.58f * goalSteering +
                1.15f * avoidanceSteering;
        }

        // Additional emergency steering.
        if (fFront < CRITICAL_DISTANCE)
        {
            float leftSpace =
                fLeft + fLeftSide;

            float rightSpace =
                fRight + fRightSide;

            turn = leftSpace > rightSpace ? 1f : -1f;

            // Avoid continuously choosing an obstructed side.
            if (leftBlocked && !rightBlocked)
                turn = -1f;

            if (rightBlocked && !leftBlocked)
                turn = 1f;
        }

        // Smooth steering changes to reduce oscillation.
        turn = Mathf.Lerp(lastTurn, turn, 0.55f);
        turn = ClampTurn(turn);

        lastTurn = turn;

        // -------------------------------------------------
        // 7. Calculate safe forward speed
        // -------------------------------------------------

        float minDistance = Mathf.Min(
            fFront,
            Mathf.Min(fLeft, fRight)
        );

        float speed = 0.85f;

        // Slow down when approaching obstacles.
        if (minDistance < 1.0f)
        {
            speed = Mathf.Lerp(
                0.12f,
                0.85f,
                Mathf.Clamp01(
                    (minDistance - 0.25f) / 0.75f
                )
            );
        }

        // Additional reduction for close front obstacles.
        if (fFront < 0.75f)
        {
            speed = Mathf.Min(
                speed,
                Mathf.Clamp(
                    (fFront - 0.18f) / 0.75f,
                    0.05f,
                    0.45f
                )
            );
        }

        // Slow down while making sharp turns.
        float turnSlowdown = 1f -
            0.48f * Mathf.Abs(turn);

        speed *= turnSlowdown;

        // Wall-following mode uses conservative speed.
        if (wallFollowCounter > 0)
        {
            speed = Mathf.Min(speed, 0.38f);
        }

        // Very close obstacles require extra caution.
        if (fFront < 0.30f)
        {
            speed = Mathf.Min(speed, 0.10f);
        }

        // When surrounded, prioritize turning over moving.
        if (fFront < 0.25f &&
            fLeft < 0.30f &&
            fRight < 0.30f)
        {
            speed = 0f;
        }

        // Reduce speed for large goal-direction corrections.
        if (Mathf.Abs(goalAngle) > 100f &&
            wallFollowCounter <= 0)
        {
            speed = Mathf.Min(speed, 0.25f);
        }

        // Maintain a small minimum movement speed when safe.
        // No reversing is used.
        speed = Mathf.Clamp(speed, 0f, 0.85f);

        // -------------------------------------------------
        // 8. Return motor commands
        // -------------------------------------------------

        return (speed, turn);
    }
}
