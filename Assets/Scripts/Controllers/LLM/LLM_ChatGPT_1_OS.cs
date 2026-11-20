using System;
using UnityEngine;

public class LLM_ChatGPT_1_OS : IRoverController
{
    public string Name => "Adaptive Wall-Aware Goal Navigation";

    // Sensor safety thresholds (metres)
    private const float SafeFront = 0.42f;
    private const float CriticalFront = 0.27f;
    private const float SafeSide = 0.25f;
    private const float MaxSensorRange = 3.95f;

    // Control parameters
    private const float MaxTurn = 1.0f;
    private const float MaxSpeed = 1.0f;
    private const float GoalTurnGain = 0.025f;

    // Memory
    private float previousTurn;
    private float previousFront;
    private float previousLeft;
    private float previousRight;

    private float stuckTimer;
    private float turnPreference;
    private float escapeTimer;
    private float recoveryDirection;

    private int lastTurnDirection;
    private int sameTurnCount;
    private int decisionCount;

    private bool wallFollowing;
    private bool escaping;

    public void ResetController()
    {
        previousTurn = 0f;
        previousFront = 4f;
        previousLeft = 4f;
        previousRight = 4f;

        stuckTimer = 0f;
        turnPreference = 1f;
        escapeTimer = 0f;
        recoveryDirection = 1f;

        lastTurnDirection = 0;
        sameTurnCount = 0;
        decisionCount = 0;

        wallFollowing = false;
        escaping = false;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        const float dt = 0.05f;

        decisionCount++;

        // Sanitize noisy sensor readings.
        float front = Sanitize(obs.front, previousFront);
        float left = Sanitize(obs.left, previousLeft);
        float right = Sanitize(obs.right, previousRight);
        float leftSide = Sanitize(obs.leftSide, previousLeft);
        float rightSide = Sanitize(obs.rightSide, previousRight);

        previousFront = front;
        previousLeft = leftSide;
        previousRight = rightSide;

        float goalAngle = Mathf.Clamp(obs.goalAngleDeg, -180f, 180f);
        float goalDistance = Mathf.Max(0f, obs.goalDistance);

        // Convert sensor readings into proximity strengths.
        float frontDanger = Danger(front, 0.65f);
        float leftDanger = Danger(left, 0.75f);
        float rightDanger = Danger(right, 0.75f);

        float leftSideDanger = Danger(leftSide, 0.45f);
        float rightSideDanger = Danger(rightSide, 0.45f);

        // Combined obstacle repulsion.
        float obstacleTurn =
            -frontDanger * Mathf.Sign(goalAngle == 0f ? 1f : goalAngle) * 0.15f
            -leftDanger * 0.75f
            +rightDanger * 0.75f
            -leftSideDanger * 0.55f
            +rightSideDanger * 0.55f;

        // Direct goal steering.
        float goalTurn = Mathf.Clamp(
            goalAngle * GoalTurnGain,
            -1f,
            1f
        );

        // Determine whether an obstacle is directly blocking the rover.
        bool frontBlocked = front < SafeFront;
        bool frontCritical = front < CriticalFront;

        bool leftBlocked = left < 0.38f || leftSide < SafeSide;
        bool rightBlocked = right < 0.38f || rightSide < SafeSide;

        // Detect possible trapping or dead ends.
        bool narrowPassage =
            leftSide < 0.45f &&
            rightSide < 0.45f;

        bool deadEnd =
            front < 0.45f &&
            left < 0.55f &&
            right < 0.55f;

        // Detect whether the rover is making little progress.
        if (front < previousFront - 0.02f)
            stuckTimer += dt;
        else if (!frontBlocked)
            stuckTimer = Mathf.Max(0f, stuckTimer - dt * 2f);

        // Choose a recovery direction when both sides are blocked.
        if (deadEnd && !escaping)
        {
            escaping = true;
            escapeTimer = 2.0f;

            if (leftSide > rightSide)
                recoveryDirection = 1f;
            else if (rightSide > leftSide)
                recoveryDirection = -1f;
            else
                recoveryDirection = goalAngle >= 0f ? 1f : -1f;
        }

        // Recovery mode: rotate away from a dead end.
        if (escaping)
        {
            escapeTimer -= dt;

            float turn = recoveryDirection;

            // If the chosen side becomes unsafe, reverse the escape direction.
            if (recoveryDirection > 0f && left < 0.28f)
                turn = -1f;

            if (recoveryDirection < 0f && right < 0.28f)
                turn = 1f;

            if (front > 0.75f && escapeTimer < 1.0f)
            {
                escaping = false;
            }
            else if (escapeTimer <= 0f)
            {
                escaping = false;
            }
            else
            {
                previousTurn = turn;

                return (0.08f, turn);
            }
        }

        // Wall-following mode.
        if (frontBlocked)
        {
            wallFollowing = true;

            if (leftBlocked && !rightBlocked)
                turnPreference = -1f;
            else if (rightBlocked && !leftBlocked)
                turnPreference = 1f;
            else if (left > right)
                turnPreference = 1f;
            else if (right > left)
                turnPreference = -1f;
            else
                turnPreference = goalAngle >= 0f ? 1f : -1f;
        }
        else if (front > 0.9f)
        {
            wallFollowing = false;
        }

        // Avoid oscillating between left and right turns.
        int currentDirection = previousTurn > 0.05f ? 1 :
                               previousTurn < -0.05f ? -1 : 0;

        if (currentDirection != 0 &&
            currentDirection == lastTurnDirection)
        {
            sameTurnCount++;
        }
        else
        {
            sameTurnCount = 0;
        }

        lastTurnDirection = currentDirection;

        float turnCommand;

        if (wallFollowing)
        {
            // Follow the side with more clearance.
            turnCommand = goalTurn * 0.25f +
                          obstacleTurn * 0.8f +
                          turnPreference * frontDanger * 0.8f;

            if (narrowPassage)
            {
                turnCommand *= 0.55f;
            }
        }
        else
        {
            turnCommand = goalTurn + obstacleTurn;
        }

        // Additional emergency steering.
        if (frontCritical)
        {
            float avoidanceDirection;

            if (left > right)
                avoidanceDirection = 1f;
            else if (right > left)
                avoidanceDirection = -1f;
            else
                avoidanceDirection = turnPreference;

            turnCommand = avoidanceDirection;
        }

        // Keep the steering smooth.
        float steeringLimit = frontCritical ? 1f : 0.65f;

        turnCommand = Mathf.Clamp(
            turnCommand,
            -steeringLimit,
            steeringLimit
        );

        float smoothing = frontBlocked ? 0.55f : 0.35f;

        turnCommand = Mathf.Lerp(
            previousTurn,
            turnCommand,
            smoothing
        );

        // Speed adaptation based on obstacles and steering angle.
        float speed = 1f;

        speed *= Mathf.Clamp01(front / 0.85f);
        speed *= Mathf.Clamp01(1f - Mathf.Abs(turnCommand) * 0.55f);

        if (frontBlocked)
            speed = Mathf.Min(speed, 0.22f);

        if (frontCritical)
            speed = Mathf.Min(speed, 0.08f);

        if (leftBlocked && rightBlocked)
            speed = Mathf.Min(speed, 0.12f);

        if (narrowPassage)
            speed = Mathf.Min(speed, 0.35f);

        // Reduce speed when the goal is very close.
        if (goalDistance < 0.35f)
            speed = Mathf.Min(speed, 0.25f);

        // Never command reverse motion.
        speed = Mathf.Clamp(speed, 0f, MaxSpeed);
        turnCommand = Mathf.Clamp(turnCommand, -MaxTurn, MaxTurn);

        previousTurn = turnCommand;

        return (speed, turnCommand);
    }

    private float Sanitize(float reading, float previous)
    {
        if (float.IsNaN(reading) || float.IsInfinity(reading))
            return previous;

        reading = Mathf.Clamp(reading, 0.02f, 4f);

        // Reject occasional false maximum-range readings.
        if (reading >= 3.99f && previous < 2.5f)
            return previous;

        return reading;
    }

    private float Danger(float distance, float threshold)
    {
        if (distance >= threshold)
            return 0f;

        return Mathf.Clamp01(
            (threshold - distance) / threshold
        );
    }
}
