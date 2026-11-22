
using System;
using UnityEngine;

public class LLM_ChatGPT_6_FB : IRoverController
{
    public string Name => "Bug Navigation Rover V3";

    // Navigation states
    private const int SEEK_GOAL = 0;
    private const int FOLLOW_WALL = 1;
    private const int ESCAPE = 2;

    private int state;
    private int followDirection;

    // Sensor memory
    private float front;
    private float left;
    private float right;
    private float leftSide;
    private float rightSide;

    private bool initialized;

    // Navigation timers (20 Hz)
    private int stateTimer;
    private int clearTimer;
    private int directionLock;
    private int progressTimer;
    private int escapeTimer;

    // Goal progress
    private float bestGoalDistance;
    private float previousGoalDistance;
    private float previousGoalAngle;

    // Steering memory
    private float previousTurn;

    // Recovery management
    private int recoveryAttempts;
    private int recoveryCooldown;

    // Constants
    private const float MAX_RANGE = 4.0f;
    private const float WALL_DISTANCE = 0.52f;
    private const float FRONT_TRIGGER = 0.68f;
    private const float CRITICAL_DISTANCE = 0.23f;

    public void ResetController()
    {
        state = SEEK_GOAL;
        followDirection = 1;

        front = MAX_RANGE;
        left = MAX_RANGE;
        right = MAX_RANGE;
        leftSide = MAX_RANGE;
        rightSide = MAX_RANGE;

        initialized = false;

        stateTimer = 0;
        clearTimer = 0;
        directionLock = 0;
        progressTimer = 0;
        escapeTimer = 0;

        bestGoalDistance = float.MaxValue;
        previousGoalDistance = float.MaxValue;
        previousGoalAngle = 0f;

        previousTurn = 0f;

        recoveryAttempts = 0;
        recoveryCooldown = 0;
    }

    // ------------------------------------------------
    // Sensor filtering
    // ------------------------------------------------

    private float Filter(float raw, float oldValue)
    {
        raw = Mathf.Clamp(raw, 0.02f, MAX_RANGE);

        if (raw < oldValue)
        {
            return Mathf.Lerp(oldValue, raw, 0.82f);
        }

        // Slowly recover from occasional false 4m readings.
        return Mathf.Min(raw, oldValue + 0.10f);
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

    private float Clamp(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }

    // ------------------------------------------------
    // Direction selection
    // ------------------------------------------------

    private int SelectDirection()
    {
        float leftOpening =
            0.65f * left + 0.35f * leftSide;

        float rightOpening =
            0.65f * right + 0.35f * rightSide;

        if (Mathf.Abs(leftOpening - rightOpening) < 0.25f)
        {
            // Deterministic tie-breaking.
            return followDirection == 0 ? 1 : followDirection;
        }

        return leftOpening > rightOpening ? 1 : -1;
    }

    // ------------------------------------------------
    // Goal steering
    // ------------------------------------------------

    private float GetGoalSteering(float angle)
    {
        return Clamp(angle / 65f);
    }

    // ------------------------------------------------
    // Obstacle assessment
    // ------------------------------------------------

    private bool IsFrontBlocked()
    {
        return front < FRONT_TRIGGER;
    }

    private bool IsEmergency()
    {
        return front < CRITICAL_DISTANCE;
    }

    private bool IsPathClear()
    {
        return front > 1.15f &&
               left > 0.38f &&
               right > 0.38f;
    }

    // ------------------------------------------------
    // Enter wall-following mode
    // ------------------------------------------------

    private void StartWallFollowing()
    {
        state = FOLLOW_WALL;

        followDirection = SelectDirection();

        stateTimer = 0;
        clearTimer = 0;

        directionLock = 60;
    }

    // ------------------------------------------------
    // Wall-following controller
    // ------------------------------------------------

    private float GetWallFollowingTurn(float goalAngle)
    {
        float direction = followDirection;

        // The side sensor facing the obstacle.
        // Following on the left means the left wall
        // should remain at approximately WALL_DISTANCE.
        float wallSensor = direction > 0
            ? leftSide
            : rightSide;

        float diagonalSensor = direction > 0
            ? left
            : right;

        // Keep the wall on the selected side.
        float wallError = WALL_DISTANCE - wallSensor;

        float wallTurn = -direction *
            Mathf.Clamp(wallError * 1.9f, -0.75f, 0.75f);

        // A nearby diagonal obstacle requires turning
        // toward the open side.
        float diagonalTurn = 0f;

        if (diagonalSensor < 0.75f)
        {
            diagonalTurn = direction *
                Mathf.Clamp(
                    (0.75f - diagonalSensor) * 1.5f,
                    0f,
                    0.8f
                );
        }

        // Front obstacle: commit to turning around it.
        float frontTurn = 0f;

        if (front < 0.90f)
        {
            frontTurn = direction *
                Mathf.Clamp(
                    (0.90f - front) * 1.8f,
                    0.35f,
                    1f
                );
        }

        // Small goal correction, never enough to reverse
        // the obstacle-following direction near a blockage.
        float goalCorrection =
            GetGoalSteering(goalAngle) * 0.15f;

        float result =
            wallTurn + diagonalTurn + frontTurn +
            goalCorrection;

        if (front < 0.70f)
        {
            result = direction *
                Mathf.Max(direction * result, 0.35f);
        }

        return Clamp(result);
    }

    // ------------------------------------------------
    // Escape manoeuvre
    // ------------------------------------------------

    private void StartEscape()
    {
        state = ESCAPE;

        recoveryAttempts++;

        // Alternate the selected direction.
        followDirection = -followDirection;

        escapeTimer = 35;
        stateTimer = 0;
        clearTimer = 0;
        progressTimer = 0;

        directionLock = 60;

        previousTurn = followDirection * 0.95f;
    }

    // ------------------------------------------------
    // Goal progress tracking
    // ------------------------------------------------

    private void UpdateProgress(float distance)
    {
        if (distance < bestGoalDistance - 0.04f)
        {
            bestGoalDistance = distance;
            progressTimer = 0;
            recoveryAttempts = 0;
        }
        else
        {
            progressTimer++;
        }

        // About 4 seconds without meaningful improvement.
        if (progressTimer >= 80 &&
            recoveryCooldown <= 0)
        {
            StartEscape();
            bestGoalDistance = distance;
            recoveryCooldown = 100;
        }
    }

    // ------------------------------------------------
    // Main controller
    // ------------------------------------------------

    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float goalDistance = Mathf.Max(0f, obs.goalDistance);
        float goalAngle = Mathf.Clamp(
            obs.goalAngleDeg,
            -180f,
            180f
        );

        if (goalDistance < 0.13f)
        {
            return (0f, 0f);
        }

        if (recoveryCooldown > 0)
            recoveryCooldown--;

        if (directionLock > 0)
            directionLock--;

        // Progress monitoring.
        UpdateProgress(goalDistance);

        float goalTurn = GetGoalSteering(goalAngle);

        stateTimer++;

        float desiredTurn = 0f;
        float speed = 0.65f;

        // ================================================
        // STATE 1: ESCAPE
        // ================================================

        if (state == ESCAPE)
        {
            escapeTimer--;

            // Rotate toward the newly selected direction.
            desiredTurn = followDirection * 0.95f;

            // Do not drive into a close obstacle.
            speed = front > 0.55f ? 0.10f : 0f;

            if (escapeTimer <= 0)
            {
                state = FOLLOW_WALL;
                stateTimer = 0;
                clearTimer = 0;
                bestGoalDistance = goalDistance;
            }
        }

        // ================================================
        // STATE 2: SEEK GOAL
        // ================================================

        else if (state == SEEK_GOAL)
        {
            desiredTurn = goalTurn;

            // Gentle correction for nearby obstacles.
            if (left < 0.65f)
            {
                desiredTurn -=
                    0.35f * Mathf.Clamp01(
                        (0.65f - left) / 0.65f
                    );
            }

            if (right < 0.65f)
            {
                desiredTurn +=
                    0.35f * Mathf.Clamp01(
                        (0.65f - right) / 0.65f
                    );
            }

            if (leftSide < 0.30f)
                desiredTurn -= 0.18f;

            if (rightSide < 0.30f)
                desiredTurn += 0.18f;

            // Enter wall-following only when the forward
            // path becomes sufficiently obstructed.
            if (IsFrontBlocked())
            {
                StartWallFollowing();
                desiredTurn = followDirection;
            }
        }

        // ================================================
        // STATE 3: FOLLOW WALL
        // ================================================

        else if (state == FOLLOW_WALL)
        {
            desiredTurn = GetWallFollowingTurn(goalAngle);

            // Avoid switching direction due to small noisy
            // changes in sensor readings.
            if (directionLock <= 0)
            {
                if (followDirection > 0 &&
                    left < 0.20f &&
                    right > 0.65f)
                {
                    followDirection = -1;
                    directionLock = 50;
                }
                else if (followDirection < 0 &&
                         right < 0.20f &&
                         left > 0.65f)
                {
                    followDirection = 1;
                    directionLock = 50;
                }
            }

            // Leave the obstacle only after the front path
            // is consistently clear and the goal is ahead.
            if (IsPathClear() && Mathf.Abs(goalAngle) < 65f)
            {
                clearTimer++;
            }
            else
            {
                clearTimer = 0;
            }

            if (clearTimer >= 15 && stateTimer >= 25)
            {
                state = SEEK_GOAL;

                stateTimer = 0;
                clearTimer = 0;
                progressTimer = 0;

                bestGoalDistance = goalDistance;

                desiredTurn = goalTurn;
            }
        }

        // ================================================
        // EMERGENCY HANDLING
        // ================================================

        if (IsEmergency() && state != ESCAPE)
        {
            if (state != FOLLOW_WALL)
            {
                StartWallFollowing();
            }

            desiredTurn = followDirection;
            speed = 0f;
        }

        // ================================================
        // STEERING OUTPUT
        // ================================================

        desiredTurn = Clamp(desiredTurn);

        float smoothing = state == FOLLOW_WALL
            ? 0.70f
            : 0.48f;

        float turn = Mathf.Lerp(
            previousTurn,
            desiredTurn,
            smoothing
        );

        turn = Clamp(turn);

        previousTurn = turn;

        // ================================================
        // SPEED CONTROL
        // ================================================

        float nearestFront = Mathf.Min(front, 1.4f);

        speed = Mathf.Lerp(
            0.18f,
            0.72f,
            Mathf.Clamp01(
                (nearestFront - 0.20f) / 1.0f
            )
        );

        // Reduce speed when turning.
        speed *= 1f - 0.38f * Mathf.Abs(turn);

        if (state == FOLLOW_WALL)
        {
            speed = Mathf.Min(speed, 0.36f);
        }

        if (state == ESCAPE)
        {
            speed = front > 0.55f ? 0.10f : 0f;
        }

        // Very close front obstacle.
        if (front < 0.30f)
        {
            speed = Mathf.Min(speed, 0.05f);
        }

        // If the goal is behind, turn before advancing.
        if (Mathf.Abs(goalAngle) > 120f &&
            state == SEEK_GOAL)
        {
            speed = Mathf.Min(speed, 0.12f);
        }

        speed = Mathf.Clamp(speed, 0f, 0.80f);

        previousGoalDistance = goalDistance;
        previousGoalAngle = goalAngle;

        return (speed, turn);
    }
}
