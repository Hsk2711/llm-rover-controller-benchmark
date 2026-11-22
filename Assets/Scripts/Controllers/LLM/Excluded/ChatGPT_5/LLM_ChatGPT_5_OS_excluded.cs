using System;
using UnityEngine;

public class LLM_ChatGPT_5_OS_excluded : IRoverController
{
    public string Name => "Adaptive Rover Navigation";

    // Navigation states
    private enum NavState
    {
        GoalSeeking,
        AvoidLeft,
        AvoidRight
    }

    private NavState state;

    // Filtered sensor readings
    private float fFront, fLeft, fRight;
    private float fLeftSide, fRightSide;
    private bool initialized;

    // Navigation memory
    private float previousGoalDistance;
    private float bestGoalDistance;
    private float noProgressTime;
    private float stateTime;
    private float lastTurn;
    private int escapeCount;

    // Timing: controller updates at 20 Hz
    private const float DT = 0.05f;

    // Rover safety parameters
    private const float SAFE_DISTANCE = 0.29f;
    private const float CRITICAL_DISTANCE = 0.22f;
    private const float WALL_TARGET = 0.37f;

    private const float MAX_SPEED = 1.0f;
    private const float MIN_SPEED = 0.13f;

    private const float DEG2RAD = Mathf.Deg2Rad;

    public void ResetController()
    {
        state = NavState.GoalSeeking;

        fFront = 4f;
        fLeft = 4f;
        fRight = 4f;
        fLeftSide = 4f;
        fRightSide = 4f;

        initialized = false;

        previousGoalDistance = 0f;
        bestGoalDistance = 999f;

        noProgressTime = 0f;
        stateTime = 0f;
        lastTurn = 0f;

        escapeCount = 0;
    }

    // Keep sensor values within valid limits.
    private float Clean(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return 4f;

        return Mathf.Clamp(value, 0.02f, 4f);
    }

    // Exponential moving average with protection against
    // occasional false maximum-range readings.
    private float Filter(float oldValue, float newValue)
    {
        newValue = Clean(newValue);

        if (newValue >= 3.95f && oldValue < 2.5f)
            newValue = oldValue;

        return Mathf.Lerp(oldValue, newValue, 0.35f);
    }

    private void UpdateSensors(Observation obs)
    {
        if (!initialized)
        {
            fFront = Clean(obs.front);
            fLeft = Clean(obs.left);
            fRight = Clean(obs.right);
            fLeftSide = Clean(obs.leftSide);
            fRightSide = Clean(obs.rightSide);

            initialized = true;
            return;
        }

        fFront = Filter(fFront, obs.front);
        fLeft = Filter(fLeft, obs.left);
        fRight = Filter(fRight, obs.right);

        fLeftSide = Filter(fLeftSide, obs.leftSide);
        fRightSide = Filter(fRightSide, obs.rightSide);
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

    // Convert goal bearing into a normalized steering command.
    private float GoalSteering(float angle)
    {
        angle = NormalizeAngle(angle);

        return Mathf.Clamp(angle / 65f, -1f, 1f);
    }

    // Estimate obstacle danger from a sensor.
    private float Danger(float distance)
    {
        if (distance >= SAFE_DISTANCE)
            return 0f;

        return Mathf.Clamp01(
            (SAFE_DISTANCE - distance) /
            (SAFE_DISTANCE - 0.12f)
        );
    }

    // Decide which side has more available clearance.
    private bool PreferLeft(float goalAngle)
    {
        float leftClearance =
            0.55f * fLeft +
            0.45f * fLeftSide;

        float rightClearance =
            0.55f * fRight +
            0.45f * fRightSide;

        // Slightly favor the direction of the goal.
        leftClearance += Mathf.Clamp(goalAngle / 180f, 0f, 0.3f);
        rightClearance += Mathf.Clamp(-goalAngle / 180f, 0f, 0.3f);

        return leftClearance >= rightClearance;
    }

    // Reactive obstacle avoidance while seeking the goal.
    private float AvoidanceSteering(float goalAngle)
    {
        float steering = GoalSteering(goalAngle);

        float frontDanger = Danger(fFront);
        float leftDanger = Danger(fLeft);
        float rightDanger = Danger(fRight);

        float leftSideDanger = Danger(fLeftSide);
        float rightSideDanger = Danger(fRightSide);

        // Repulsion from forward obstacles.
        steering += 0.95f * leftDanger;
        steering -= 0.95f * rightDanger;

        // Side obstacle repulsion.
        steering -= 0.55f * leftSideDanger;
        steering += 0.55f * rightSideDanger;

        // A close frontal obstacle requires a stronger turn.
        if (frontDanger > 0.15f)
        {
            float direction = PreferLeft(goalAngle) ? 1f : -1f;

            steering += direction *
                        (0.65f + 0.45f * frontDanger);
        }

        // Increase steering as the front gets closer.
        steering *= 1f + 0.35f * frontDanger;

        return ClampTurn(steering);
    }

    // Follow a wall to navigate around obstacles and U-shaped traps.
    private float WallFollowingSteering()
    {
        float steering;

        if (state == NavState.AvoidLeft)
        {
            // Left-hand wall following.
            // Too close: steer right.
            // Too far: steer left.
            float error = WALL_TARGET - fLeftSide;

            steering = -1.5f * error;

            // Front-left obstacle: steer right.
            steering -= 0.9f * Danger(fLeft);

            // Front obstacle: steer right more strongly.
            steering -= 1.1f * Danger(fFront);

            // Right-side clearance provides additional correction.
            if (fRightSide < 0.25f)
                steering -= 0.25f;
        }
        else
        {
            // Right-hand wall following.
            // Too close: steer left.
            // Too far: steer right.
            float error = WALL_TARGET - fRightSide;

            steering = 1.5f * error;

            // Front-right obstacle: steer left.
            steering += 0.9f * Danger(fRight);

            // Front obstacle: steer left more strongly.
            steering += 1.1f * Danger(fFront);

            if (fLeftSide < 0.25f)
                steering += 0.25f;
        }

        return ClampTurn(steering);
    }

    // Detect when it is safe to leave wall following.
    private bool CanReturnToGoal(float goalAngle)
    {
        bool frontClear = fFront > 0.65f;

        bool directionClear;

        if (goalAngle >= 0f)
            directionClear = fLeft > 0.55f;
        else
            directionClear = fRight > 0.55f;

        bool goalAligned = Mathf.Abs(goalAngle) < 35f;

        bool enoughTime = stateTime > 1.0f;

        return frontClear &&
               directionClear &&
               goalAligned &&
               enoughTime;
    }

    // Update progress monitoring.
    private void UpdateProgress(float goalDistance)
    {
        if (goalDistance < bestGoalDistance - 0.035f)
        {
            bestGoalDistance = goalDistance;
            noProgressTime = 0f;
        }
        else
        {
            noProgressTime += DT;
        }

        if (goalDistance < previousGoalDistance - 0.02f)
            noProgressTime = Mathf.Max(0f, noProgressTime - DT);

        previousGoalDistance = goalDistance;
    }

    // Escape behavior when navigation is not making progress.
    private void HandleStuck(float goalAngle)
    {
        if (noProgressTime < 3.0f)
            return;

        escapeCount++;

        // Change wall-following direction when stuck.
        if (state == NavState.AvoidLeft)
            state = NavState.AvoidRight;
        else if (state == NavState.AvoidRight)
            state = NavState.AvoidLeft;
        else
            state = PreferLeft(goalAngle)
                ? NavState.AvoidLeft
                : NavState.AvoidRight;

        stateTime = 0f;
        noProgressTime = 0f;
    }

    // Main controller.
    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float goalDistance = Mathf.Max(0f, obs.goalDistance);
        float goalAngle = NormalizeAngle(obs.goalAngleDeg);

        stateTime += DT;

        UpdateProgress(goalDistance);

        // Goal reached or sufficiently close.
        if (goalDistance < 0.18f)
        {
            return (0f, 0f);
        }

        // Detect a nearby frontal obstacle.
        bool obstacleAhead =
            fFront < 0.48f ||
            fLeft < 0.32f ||
            fRight < 0.32f;

        // Enter obstacle avoidance mode.
        if (state == NavState.GoalSeeking && obstacleAhead)
        {
            state = PreferLeft(goalAngle)
                ? NavState.AvoidLeft
                : NavState.AvoidRight;

            stateTime = 0f;
        }

        // Check whether the rover can safely return to goal seeking.
        if (state != NavState.GoalSeeking &&
            CanReturnToGoal(goalAngle))
        {
            state = NavState.GoalSeeking;
            stateTime = 0f;
            noProgressTime = 0f;
        }

        // Stuck detection and recovery.
        HandleStuck(goalAngle);

        float turn;

        if (state == NavState.GoalSeeking)
        {
            turn = AvoidanceSteering(goalAngle);
        }
        else
        {
            turn = WallFollowingSteering();
        }

        // Emergency steering for extremely close obstacles.
        if (fFront < CRITICAL_DISTANCE)
        {
            if (fLeft > fRight)
                turn = 1f;
            else
                turn = -1f;
        }

        // Smooth steering to reduce oscillations.
        turn = Mathf.Lerp(lastTurn, turn, 0.65f);
        lastTurn = turn;

        turn = ClampTurn(turn);

        // Adaptive speed based on obstacle clearance.
        float minimumDistance = Mathf.Min(
            fFront,
            fLeft,
            fRight,
            fLeftSide,
            fRightSide
        );

        float speed = MAX_SPEED;

        if (minimumDistance < 0.8f)
        {
            speed = Mathf.Lerp(
                0.18f,
                0.65f,
                Mathf.Clamp01((minimumDistance - 0.20f) / 0.60f)
            );
        }

        // Reduce speed when making sharp turns.
        speed *= 1f - 0.55f * Mathf.Abs(turn);

        // Reduce speed when the goal is directly behind.
        if (Mathf.Abs(goalAngle) > 110f)
            speed *= 0.65f;

        // Slow down further in wall-following mode.
        if (state != NavState.GoalSeeking)
            speed = Mathf.Min(speed, 0.55f);

        // Avoid moving forward into a very close obstacle.
        if (fFront < 0.17f)
            speed = 0f;

        speed = Mathf.Clamp(speed, 0f, 1f);

        return (speed, turn);
    }
}
