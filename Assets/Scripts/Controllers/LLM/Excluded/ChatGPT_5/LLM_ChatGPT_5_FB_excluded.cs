
using System;
using UnityEngine;

public class LLM_ChatGPT_5_FB_excluded : IRoverController
{
    public string Name => "Rover V6 Adaptive Navigation";

    private enum NavigationMode
    {
        GoalSeeking,
        ObstacleAvoidance,
        Recovery
    }

    private NavigationMode mode;

    private const float DT = 0.05f;
    private const float MAX_SENSOR = 4f;

    private const float FRONT_DANGER = 0.65f;
    private const float DIAGONAL_DANGER = 0.42f;
    private const float CRITICAL_DISTANCE = 0.24f;

    private const float DESIRED_WALL_DISTANCE = 0.40f;

    private float front;
    private float left;
    private float right;
    private float leftSide;
    private float rightSide;

    private bool initialized;

    private float filteredGoalAngle;

    // Navigation direction:
    // +1 = pass obstacle on its left side.
    // -1 = pass obstacle on its right side.
    private int avoidanceDirection;

    private int escapeDirection;

    // Steering memory
    private float previousTurn;
    private int steeringDirection;
    private int pendingDirection;
    private float reversalTimer;

    // Timers
    private float modeTimer;
    private float clearTimer;
    private float stuckTimer;
    private float recoveryTimer;

    // Progress tracking
    private float bestGoalDistance;
    private float progressReference;

    private int recoveryCount;

    // Sensor filtering counters
    private int fc, lc, rc, lsc, rsc;

    public void ResetController()
    {
        mode = NavigationMode.GoalSeeking;

        front = left = right = 4f;
        leftSide = rightSide = 4f;

        initialized = false;

        filteredGoalAngle = 0f;

        avoidanceDirection = 1;
        escapeDirection = 1;

        previousTurn = 0f;
        steeringDirection = 0;
        pendingDirection = 0;
        reversalTimer = 0f;

        modeTimer = 0f;
        clearTimer = 0f;
        stuckTimer = 0f;
        recoveryTimer = 0f;

        bestGoalDistance = 999f;
        progressReference = 999f;

        recoveryCount = 0;

        fc = lc = rc = lsc = rsc = 0;
    }

    private float Clean(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return MAX_SENSOR;

        return Mathf.Clamp(value, 0.02f, MAX_SENSOR);
    }

    // Fast response to closer obstacles.
    // Slower response when obstacles disappear.
    // Reject isolated false maximum readings.
    private float Filter(
        float previous,
        float current,
        ref int clearCount)
    {
        current = Clean(current);

        if (current >= 3.95f && previous < 2.5f)
        {
            clearCount++;

            if (clearCount < 3)
                return previous;
        }
        else
        {
            clearCount = 0;
        }

        float alpha = current < previous ? 0.65f : 0.25f;

        return Mathf.Lerp(previous, current, alpha);
    }

    private void UpdateSensors(Observation obs)
    {
        if (!initialized)
        {
            front = Clean(obs.front);
            left = Clean(obs.left);
            right = Clean(obs.right);

            leftSide = Clean(obs.leftSide);
            rightSide = Clean(obs.rightSide);

            filteredGoalAngle = obs.goalAngleDeg;

            initialized = true;
            return;
        }

        front = Filter(front, obs.front, ref fc);
        left = Filter(left, obs.left, ref lc);
        right = Filter(right, obs.right, ref rc);

        leftSide = Filter(leftSide, obs.leftSide, ref lsc);
        rightSide = Filter(rightSide, obs.rightSide, ref rsc);

        filteredGoalAngle = Mathf.LerpAngle(
            filteredGoalAngle,
            obs.goalAngleDeg,
            0.35f
        );
    }

    private float ClampTurn(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }

    private float Danger(float distance)
    {
        return Mathf.Clamp01(
            (0.75f - distance) / 0.55f
        );
    }

    private float GoalSteering(float angle)
    {
        // Small deadband reduces unnecessary corrections.
        if (Mathf.Abs(angle) < 7f)
            return 0f;

        return Mathf.Clamp(angle / 60f, -1f, 1f);
    }

    // Choose the direction with greater available clearance.
    private int ChooseAvoidanceDirection()
    {
        float leftClearance =
            0.65f * left +
            0.35f * leftSide;

        float rightClearance =
            0.65f * right +
            0.35f * rightSide;

        if (leftClearance > rightClearance + 0.12f)
            return 1;

        if (rightClearance > leftClearance + 0.12f)
            return -1;

        return leftClearance >= rightClearance ? 1 : -1;
    }

    private void EnterAvoidance(int direction)
    {
        avoidanceDirection = direction;

        mode = NavigationMode.ObstacleAvoidance;

        modeTimer = 0f;
        clearTimer = 0f;
        stuckTimer = 0f;

        steeringDirection = 0;
        pendingDirection = 0;
        reversalTimer = 0f;
    }

    // Combined goal attraction and obstacle repulsion.
    private float CalculateGoalSteering(float angle)
    {
        float turn = GoalSteering(angle);

        turn += 0.95f * Danger(left);
        turn -= 0.95f * Danger(right);

        turn -= 0.40f * Danger(leftSide);
        turn += 0.40f * Danger(rightSide);

        return ClampTurn(turn);
    }

    // Avoidance with persistent passing direction.
    private float CalculateAvoidanceSteering(float angle)
    {
        float goalTurn = GoalSteering(angle);

        float turn = goalTurn * 0.35f;

        if (avoidanceDirection > 0)
        {
            // Prefer passing the obstacle on the left.
            // Turn away from close frontal-left obstacles.
            turn += 0.45f;
            turn -= 0.95f * Danger(left);
            turn -= 0.65f * Danger(front);

            // Maintain clearance from the left wall.
            if (leftSide < 0.85f)
            {
                turn += Mathf.Clamp(
                    (leftSide - DESIRED_WALL_DISTANCE) * 0.65f,
                    -0.35f,
                    0.35f
                );
            }
        }
        else
        {
            // Prefer passing the obstacle on the right.
            turn -= 0.45f;
            turn += 0.95f * Danger(right);
            turn += 0.65f * Danger(front);

            if (rightSide < 0.85f)
            {
                turn -= Mathf.Clamp(
                    (rightSide - DESIRED_WALL_DISTANCE) * 0.65f,
                    -0.35f,
                    0.35f
                );
            }
        }

        // Additional clearance protection.
        turn += 0.30f * Danger(leftSide);
        turn -= 0.30f * Danger(rightSide);

        return ClampTurn(turn);
    }

    // Prevent rapid left-right steering reversals.
    private float StabilizeSteering(float desired)
    {
        int sign = desired > 0.06f ? 1 :
                   desired < -0.06f ? -1 : 0;

        if (sign == 0)
        {
            pendingDirection = 0;
            reversalTimer = 0f;

            return Mathf.Lerp(previousTurn, 0f, 0.35f);
        }

        if (steeringDirection == 0)
            steeringDirection = sign;

        if (sign != steeringDirection)
        {
            if (pendingDirection != sign)
            {
                pendingDirection = sign;
                reversalTimer = 0f;
            }

            reversalTimer += DT;

            // Allow emergency steering immediately.
            if (Mathf.Abs(desired) > 0.90f)
            {
                steeringDirection = sign;
                pendingDirection = 0;
                reversalTimer = 0f;
            }
            else if (reversalTimer >= 0.25f)
            {
                steeringDirection = sign;
                pendingDirection = 0;
                reversalTimer = 0f;
            }
            else
            {
                desired = steeringDirection *
                    Mathf.Min(Mathf.Abs(previousTurn), 0.35f);
            }
        }
        else
        {
            pendingDirection = 0;
            reversalTimer = 0f;
        }

        return Mathf.Lerp(previousTurn, desired, 0.45f);
    }

    // Check whether the goal direction is sufficiently clear.
    private bool GoalPathIsClear(float angle)
    {
        if (front < 0.90f)
            return false;

        if (Mathf.Abs(angle) < 25f)
            return true;

        if (angle > 0f)
            return left > 0.75f;

        return right > 0.75f;
    }

    private void UpdateProgress(float distance)
    {
        if (distance < bestGoalDistance - 0.10f)
        {
            bestGoalDistance = distance;
            stuckTimer = 0f;
            recoveryCount = 0;
        }
        else
        {
            stuckTimer += DT;
        }

        if (distance < progressReference - 0.12f)
        {
            progressReference = distance;
        }
    }

    private void StartRecovery()
    {
        escapeDirection = -avoidanceDirection;

        if (mode == NavigationMode.GoalSeeking)
            escapeDirection = -ChooseAvoidanceDirection();

        mode = NavigationMode.Recovery;

        recoveryTimer = 0f;
        modeTimer = 0f;
        stuckTimer = 0f;
        clearTimer = 0f;

        recoveryCount++;

        steeringDirection = escapeDirection;
        pendingDirection = 0;
        reversalTimer = 0f;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float goalDistance = Mathf.Max(0f, obs.goalDistance);

        float goalAngle = Mathf.Clamp(
            filteredGoalAngle,
            -180f,
            180f
        );

        modeTimer += DT;

        UpdateProgress(goalDistance);

        // Goal reached.
        if (goalDistance < 0.16f)
            return (0f, 0f);

        // Use raw sensor readings for immediate safety.
        float rawFront = Clean(obs.front);
        float rawLeft = Clean(obs.left);
        float rawRight = Clean(obs.right);

        bool obstacleAhead =
            front < FRONT_DANGER ||
            left < DIAGONAL_DANGER ||
            right < DIAGONAL_DANGER;

        // GOAL SEEKING
        if (mode == NavigationMode.GoalSeeking &&
            obstacleAhead)
        {
            EnterAvoidance(ChooseAvoidanceDirection());
        }

        // OBSTACLE AVOIDANCE
        if (mode == NavigationMode.ObstacleAvoidance)
        {
            if (GoalPathIsClear(goalAngle))
                clearTimer += DT;
            else
                clearTimer = 0f;

            if (clearTimer >= 0.65f && modeTimer >= 1.0f)
            {
                mode = NavigationMode.GoalSeeking;

                modeTimer = 0f;
                clearTimer = 0f;
                stuckTimer = 0f;

                steeringDirection = 0;
                pendingDirection = 0;
                reversalTimer = 0f;

                bestGoalDistance = goalDistance;
            }
        }

        // RECOVERY
        if (mode == NavigationMode.Recovery)
        {
            recoveryTimer += DT;

            if (recoveryTimer >= 0.50f)
            {
                EnterAvoidance(escapeDirection);
            }
        }

        // Detect lack of meaningful progress.
        if (mode != NavigationMode.Recovery &&
            stuckTimer >= 5f &&
            recoveryCount < 4)
        {
            StartRecovery();
        }

        float desiredTurn;
        float speed;

        switch (mode)
        {
            case NavigationMode.GoalSeeking:

                desiredTurn = CalculateGoalSteering(goalAngle);
                speed = 0.75f;

                break;

            case NavigationMode.ObstacleAvoidance:

                desiredTurn = CalculateAvoidanceSteering(goalAngle);
                speed = 0.45f;

                break;

            default:

                desiredTurn = escapeDirection * 0.85f;
                speed = 0.10f;

                break;
        }

        // Emergency response overrides steering hysteresis.
        bool emergency =
            rawFront < CRITICAL_DISTANCE ||
            rawLeft < 0.15f ||
            rawRight < 0.15f;

        if (emergency)
        {
            if (rawFront < CRITICAL_DISTANCE)
                desiredTurn = left > right ? 1f : -1f;
            else if (rawLeft < 0.15f)
                desiredTurn = -1f;
            else
                desiredTurn = 1f;

            speed = 0f;

            steeringDirection = desiredTurn > 0f ? 1 : -1;
            pendingDirection = 0;
            reversalTimer = 0f;
        }
        else
        {
            desiredTurn = StabilizeSteering(desiredTurn);
        }

        previousTurn = ClampTurn(desiredTurn);

        // Adaptive speed based on minimum clearance.
        float nearest = Mathf.Min(
            front,
            left,
            right,
            leftSide,
            rightSide
        );

        if (nearest < 0.90f)
        {
            float safeSpeed = Mathf.Lerp(
                0.10f,
                0.65f,
                Mathf.Clamp01((nearest - 0.20f) / 0.70f)
            );

            speed = Mathf.Min(speed, safeSpeed);
        }

        // Slow down for sharp turns.
        speed *= 1f - 0.45f * Mathf.Abs(previousTurn);

        if (rawFront < 0.16f)
            speed = 0f;

        return (
            Mathf.Clamp(speed, 0f, 1f),
            ClampTurn(previousTurn)
        );
    }
}
