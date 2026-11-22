
using System;
using UnityEngine;

public class LLM_ChatGPT_6_FB : IRoverController
{
    public string Name => "Adaptive Rover Navigation V2";

    // -------------------------------------------------
    // Sensor filtering
    // -------------------------------------------------

    private float front;
    private float left;
    private float right;
    private float leftSide;
    private float rightSide;

    private bool initialized;

    private const float MAX_RANGE = 4.0f;
    private const float SAFE_DISTANCE = 0.48f;
    private const float CRITICAL_DISTANCE = 0.22f;

    // -------------------------------------------------
    // Navigation state
    // -------------------------------------------------

    // +1 = prefer left, -1 = prefer right
    private int preferredDirection;

    // 0 = normal navigation
    // 1 = obstacle avoidance
    // 2 = recovery turn
    private int navigationMode;

    private int modeTimer;
    private int clearCounter;
    private int directionLock;

    // Progress monitoring
    private float bestGoalDistance;
    private float previousGoalDistance;
    private int noProgressCounter;
    private int recoveryCount;

    // Steering smoothing
    private float previousTurn;

    // -------------------------------------------------
    // Reset
    // -------------------------------------------------

    public void ResetController()
    {
        front = MAX_RANGE;
        left = MAX_RANGE;
        right = MAX_RANGE;
        leftSide = MAX_RANGE;
        rightSide = MAX_RANGE;

        initialized = false;

        preferredDirection = 1;

        navigationMode = 0;
        modeTimer = 0;
        clearCounter = 0;
        directionLock = 0;

        bestGoalDistance = float.MaxValue;
        previousGoalDistance = float.MaxValue;
        noProgressCounter = 0;
        recoveryCount = 0;

        previousTurn = 0f;
    }

    // -------------------------------------------------
    // Sensor filtering
    // -------------------------------------------------

    private float Filter(float raw, float oldValue)
    {
        raw = Mathf.Clamp(raw, 0.02f, MAX_RANGE);

        // React quickly to approaching obstacles.
        if (raw < oldValue)
        {
            return Mathf.Lerp(oldValue, raw, 0.85f);
        }

        // Reject sudden false-clear readings.
        // Gradually recover when the path becomes clear.
        return Mathf.Min(raw, oldValue + 0.14f);
    }

    private void UpdateSensors(Observation obs)
    {
        if (!initialized)
        {
            front = Mathf.Clamp(obs.front, 0.02f, MAX_RANGE);
            left = Mathf.Clamp(obs.left, 0.02f, MAX_RANGE);
            right = Mathf.Clamp(obs.right, 0.02f, MAX_RANGE);
            leftSide = Mathf.Clamp(obs.leftSide, 0.02f, MAX_RANGE);
            rightSide = Mathf.Clamp(obs.rightSide, 0.02f, MAX_RANGE);

            initialized = true;
            return;
        }

        front = Filter(obs.front, front);
        left = Filter(obs.left, left);
        right = Filter(obs.right, right);
        leftSide = Filter(obs.leftSide, leftSide);
        rightSide = Filter(obs.rightSide, rightSide);
    }

    // -------------------------------------------------
    // Utility functions
    // -------------------------------------------------

    private float Clamp(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }

    private float GoalSteering(float angle)
    {
        return Clamp(angle / 75f);
    }

    // -------------------------------------------------
    // Choose a persistent obstacle avoidance direction
    // -------------------------------------------------

    private int ChooseDirection()
    {
        float leftSpace =
            0.65f * left +
            0.35f * leftSide;

        float rightSpace =
            0.65f * right +
            0.35f * rightSide;

        float difference = leftSpace - rightSpace;

        // Avoid changing direction for small differences.
        if (Mathf.Abs(difference) < 0.30f)
        {
            return preferredDirection;
        }

        return difference > 0f ? 1 : -1;
    }

    // -------------------------------------------------
    // Obstacle detection
    // -------------------------------------------------

    private bool FrontBlocked()
    {
        return front < 0.62f;
    }

    private bool EmergencyObstacle()
    {
        return front < CRITICAL_DISTANCE ||
               (left < 0.19f && right < 0.19f);
    }

    private bool PathClear()
    {
        return front > 0.95f;
    }

    // -------------------------------------------------
    // Start obstacle avoidance
    // -------------------------------------------------

    private void EnterAvoidance()
    {
        preferredDirection = ChooseDirection();

        navigationMode = 1;

        modeTimer = 0;
        clearCounter = 0;
        directionLock = 45;
    }

    // -------------------------------------------------
    // Obstacle avoidance steering
    // -------------------------------------------------

    private float AvoidanceSteering(float goalSteering)
    {
        float direction = preferredDirection;

        float sideDistance = direction > 0
            ? rightSide
            : leftSide;

        float diagonalDistance = direction > 0
            ? right
            : left;

        // Follow the obstacle on the opposite side
        // while moving toward the open direction.
        float wallCorrection = 0f;

        if (sideDistance < 1.15f)
        {
            float error = 0.52f - sideDistance;

            wallCorrection = direction *
                Mathf.Clamp(error * 1.7f, -0.65f, 0.65f);
        }

        // If a diagonal obstacle is close,
        // increase steering toward the open side.
        float diagonalCorrection = 0f;

        if (diagonalDistance < 0.65f)
        {
            diagonalCorrection = direction *
                Mathf.Clamp(
                    (0.65f - diagonalDistance) * 1.5f,
                    0f,
                    0.8f
                );
        }

        // Strong steering when an obstacle is directly ahead.
        float frontCorrection = 0f;

        if (front < 0.75f)
        {
            frontCorrection = direction *
                Mathf.Clamp(
                    (0.75f - front) * 1.8f,
                    0.25f,
                    1f
                );
        }

        // Blend with goal direction without allowing
        // the goal to reverse the selected avoidance side.
        float result =
            0.35f * goalSteering +
            0.45f * wallCorrection +
            0.65f * diagonalCorrection +
            frontCorrection;

        // Preserve a minimum turning commitment
        // while an obstacle is directly ahead.
        if (front < 0.65f)
        {
            result = Mathf.Max(
                result * direction,
                0.30f
            ) * direction;
        }

        return Clamp(result);
    }

    // -------------------------------------------------
    // Recovery when progress stops
    // -------------------------------------------------

    private void StartRecovery()
    {
        recoveryCount++;

        // Alternate the recovery direction rather than
        // repeatedly turning into the same dead end.
        preferredDirection = -preferredDirection;

        navigationMode = 2;

        // Commit to a turning manoeuvre.
        modeTimer = 30;
        directionLock = 60;
        clearCounter = 0;

        noProgressCounter = 0;

        previousTurn = preferredDirection * 0.9f;
    }

    // -------------------------------------------------
    // Monitor goal progress
    // -------------------------------------------------

    private void UpdateProgress(float distance)
    {
        if (distance < bestGoalDistance - 0.035f)
        {
            bestGoalDistance = distance;
            noProgressCounter = 0;
        }
        else
        {
            noProgressCounter++;
        }

        // Approximately 3 seconds without progress.
        if (noProgressCounter >= 60)
        {
            StartRecovery();

            // Reset reference to avoid repeatedly
            // triggering recovery immediately.
            bestGoalDistance = distance;
        }

        previousGoalDistance = distance;
    }

    // -------------------------------------------------
    // Main decision function
    // -------------------------------------------------

    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float goalDistance = Mathf.Max(0f, obs.goalDistance);
        float goalAngle = Mathf.Clamp(
            obs.goalAngleDeg,
            -180f,
            180f
        );

        // Goal reached.
        if (goalDistance < 0.13f)
        {
            return (0f, 0f);
        }

        // -------------------------------------------------
        // Progress monitoring
        // -------------------------------------------------

        UpdateProgress(goalDistance);

        // -------------------------------------------------
        // Update timers
        // -------------------------------------------------

        if (modeTimer > 0)
            modeTimer--;

        if (directionLock > 0)
            directionLock--;

        // -------------------------------------------------
        // Recovery mode
        // -------------------------------------------------

        if (navigationMode == 2)
        {
            // Complete the recovery turn before returning
            // to ordinary navigation.
            if (modeTimer <= 0)
            {
                navigationMode = 1;
                modeTimer = 0;
                clearCounter = 0;
            }
            else
            {
                float recoveryTurn = preferredDirection * 0.90f;

                previousTurn = recoveryTurn;

                // Rotate almost in place when possible.
                float recoverySpeed =
                    front < 0.38f ? 0f : 0.12f;

                return (recoverySpeed, recoveryTurn);
            }
        }

        // -------------------------------------------------
        // Enter avoidance mode
        // -------------------------------------------------

        if (navigationMode == 0 && FrontBlocked())
        {
            EnterAvoidance();
        }

        // -------------------------------------------------
        // Avoidance mode
        // -------------------------------------------------

        if (navigationMode == 1)
        {
            modeTimer++;

            // Keep the same direction during avoidance.
            // Only change if the selected direction becomes
            // critically obstructed and the other is open.
            if (directionLock <= 0)
            {
                if (preferredDirection > 0 &&
                    left < 0.22f &&
                    right > 0.55f)
                {
                    preferredDirection = -1;
                    directionLock = 40;
                }
                else if (preferredDirection < 0 &&
                         right < 0.22f &&
                         left > 0.55f)
                {
                    preferredDirection = 1;
                    directionLock = 40;
                }
            }

            if (PathClear())
            {
                clearCounter++;
            }
            else
            {
                clearCounter = 0;
            }

            // Exit avoidance only after a sustained clear path.
            if (clearCounter >= 12 && modeTimer >= 20)
            {
                navigationMode = 0;
                modeTimer = 0;
                clearCounter = 0;
                noProgressCounter = 0;
            }
        }

        // -------------------------------------------------
        // Calculate steering
        // -------------------------------------------------

        float goalSteering = GoalSteering(goalAngle);

        float desiredTurn;

        if (navigationMode == 1)
        {
            desiredTurn = AvoidanceSteering(goalSteering);
        }
        else
        {
            // Normal navigation: follow the goal.
            desiredTurn = goalSteering;

            // Gentle obstacle repulsion.
            if (left < 0.65f)
            {
                desiredTurn -=
                    0.30f * Mathf.Clamp01(
                        (0.65f - left) / 0.65f
                    );
            }

            if (right < 0.65f)
            {
                desiredTurn +=
                    0.30f * Mathf.Clamp01(
                        (0.65f - right) / 0.65f
                    );
            }

            if (leftSide < 0.32f)
                desiredTurn -= 0.20f;

            if (rightSide < 0.32f)
                desiredTurn += 0.20f;
        }

        // Emergency steering.
        if (EmergencyObstacle())
        {
            float leftOpen = left + leftSide;
            float rightOpen = right + rightSide;

            if (navigationMode == 0)
            {
                preferredDirection =
                    leftOpen >= rightOpen ? 1 : -1;

                navigationMode = 1;
                modeTimer = 0;
                directionLock = 45;
            }

            desiredTurn = preferredDirection;
        }

        // -------------------------------------------------
        // Steering smoothing
        // -------------------------------------------------

        // Faster response to major changes.
        float smoothing = navigationMode == 1
            ? 0.65f
            : 0.45f;

        float turn = Mathf.Lerp(
            previousTurn,
            desiredTurn,
            smoothing
        );

        turn = Clamp(turn);

        previousTurn = turn;

        // -------------------------------------------------
        // Speed control
        // -------------------------------------------------

        float speed = 0.72f;

        // Reduce speed near obstacles.
        float nearestFront = Mathf.Min(front, 1.5f);

        if (nearestFront < 1.1f)
        {
            speed = Mathf.Lerp(
                0.12f,
                0.72f,
                Mathf.Clamp01(
                    (nearestFront - 0.22f) / 0.88f
                )
            );
        }

        // Sharp turns require slower movement.
        speed *= 1f - 0.40f * Mathf.Abs(turn);

        // Obstacle mode.
        if (navigationMode == 1)
        {
            speed = Mathf.Min(speed, 0.40f);
        }

        // Critical front obstacle.
        if (front < 0.30f)
        {
            speed = Mathf.Min(speed, 0.08f);
        }

        // Avoid moving into a narrow blocked opening.
        if (front < 0.24f &&
            left < 0.30f &&
            right < 0.30f)
        {
            speed = 0f;
        }

        // Turn toward the goal before driving when
        // the goal is substantially behind the rover.
        if (Mathf.Abs(goalAngle) > 115f &&
            navigationMode == 0)
        {
            speed = Mathf.Min(speed, 0.12f);
        }

        // Maintain forward movement whenever safe.
        speed = Mathf.Clamp(speed, 0f, 0.80f);

        return (speed, turn);
    }
}
