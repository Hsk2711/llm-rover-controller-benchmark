using System;
using UnityEngine;

public class LLM_ChatGPT_2_FB : IRoverController
{
    public string Name => "Adaptive Navigation V2";

    private const float MAX_RANGE = 4.0f;

    // Sensor order:
    // Front, Left, Right, LeftSide, RightSide
    private float[] sensor = new float[5];
    private int[] clearCount = new int[5];

    private bool initialized;

    // Navigation memory
    private float bestGoalDistance;
    private int stuckTicks;
    private int escapeTicks;
    private int directionHold;

    private int preferredDirection;
    private float previousTurn;

    // Obstacle avoidance state
    private bool obstacleMode;
    private int obstacleDirection;

    private const float FRONT_ALERT = 1.40f;
    private const float FRONT_CRITICAL = 0.45f;

    public void ResetController()
    {
        for (int i = 0; i < 5; i++)
        {
            sensor[i] = MAX_RANGE;
            clearCount[i] = 0;
        }

        initialized = false;

        bestGoalDistance = 999f;
        stuckTicks = 0;
        escapeTicks = 0;
        directionHold = 0;

        preferredDirection = 1;
        obstacleDirection = 1;

        previousTurn = 0f;
        obstacleMode = false;
    }

    private float Clamp(float v, float min, float max)
    {
        return Mathf.Clamp(v, min, max);
    }

    // -------------------------------------------------
    // SENSOR FILTERING
    // -------------------------------------------------

    private float ReadSensor(float raw, int index)
    {
        raw = Clamp(raw, 0.02f, MAX_RANGE);

        if (!initialized)
        {
            sensor[index] = raw;
            return raw;
        }

        // Protect against occasional false-clear readings.
        if (raw >= 3.95f && sensor[index] < 3.5f)
        {
            clearCount[index]++;

            if (clearCount[index] < 4)
                return sensor[index];
        }
        else
        {
            clearCount[index] = 0;
        }

        // React quickly when an obstacle gets closer.
        if (raw < sensor[index])
        {
            sensor[index] =
                0.80f * raw + 0.20f * sensor[index];
        }
        else
        {
            // Gradual recovery when the obstacle moves away.
            sensor[index] =
                0.25f * raw + 0.75f * sensor[index];
        }

        return sensor[index];
    }

    private float Danger(float distance, float threshold)
    {
        return Clamp(
            (threshold - distance) / threshold,
            0f,
            1f
        );
    }

    // -------------------------------------------------
    // SELECT THE SAFER TURNING DIRECTION
    // -------------------------------------------------

    private int SelectDirection(
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        // Score available clearance on both sides.
        float leftScore =
            0.70f * left +
            0.30f * leftSide;

        float rightScore =
            0.70f * right +
            0.30f * rightSide;

        // Add hysteresis to prevent direction oscillation.
        if (directionHold > 0)
        {
            directionHold--;

            if (Mathf.Abs(leftScore - rightScore) < 0.65f)
                return preferredDirection;
        }

        int direction;

        if (leftScore > rightScore)
            direction = 1;
        else if (rightScore > leftScore)
            direction = -1;
        else
            direction = preferredDirection;

        preferredDirection = direction;
        directionHold = 12;

        return direction;
    }

    // -------------------------------------------------
    // MAIN DECISION FUNCTION
    // -------------------------------------------------

    public (float speed01, float turn) Decide(Observation obs)
    {
        // Read sensors
        float front = ReadSensor(obs.front, 0);
        float left = ReadSensor(obs.left, 1);
        float right = ReadSensor(obs.right, 2);
        float leftSide = ReadSensor(obs.leftSide, 3);
        float rightSide = ReadSensor(obs.rightSide, 4);

        initialized = true;

        float goalDistance = Mathf.Max(0f, obs.goalDistance);

        float goalAngle = Clamp(
            obs.goalAngleDeg,
            -180f,
            180f
        );

        // -------------------------------------------------
        // 1. GOAL CHECK
        // -------------------------------------------------

        if (goalDistance < 0.18f)
        {
            previousTurn = 0f;
            return (0f, 0f);
        }

        // -------------------------------------------------
        // 2. PROGRESS MONITORING
        // -------------------------------------------------

        if (goalDistance < bestGoalDistance - 0.04f)
        {
            bestGoalDistance = goalDistance;
            stuckTicks = 0;
        }
        else
        {
            stuckTicks++;
        }

        // Detect a possible dead end.
        if (stuckTicks > 60 && escapeTicks == 0)
        {
            escapeDirection = SelectDirection(
                left, right, leftSide, rightSide
            );

            escapeTicks = 65;
            stuckTicks = 0;
        }

        // -------------------------------------------------
        // 3. OBSTACLE DETECTION
        // -------------------------------------------------

        float frontDanger = Danger(front, FRONT_ALERT);

        float leftDanger = Danger(left, 0.95f);
        float rightDanger = Danger(right, 0.95f);

        float leftSideDanger = Danger(leftSide, 0.50f);
        float rightSideDanger = Danger(rightSide, 0.50f);

        bool frontBlocked = front < FRONT_ALERT;

        bool diagonalBlocked =
            left < 0.65f || right < 0.65f;

        bool sideBlocked =
            leftSide < 0.30f || rightSide < 0.30f;

        // Enter obstacle avoidance mode.
        if (frontBlocked || diagonalBlocked || sideBlocked)
        {
            if (!obstacleMode)
            {
                obstacleDirection = SelectDirection(
                    left, right, leftSide, rightSide
                );
            }

            obstacleMode = true;
        }

        // Leave obstacle mode only when sufficient clearance exists.
        if (obstacleMode &&
            front > 1.65f &&
            left > 0.85f &&
            right > 0.85f)
        {
            obstacleMode = false;
        }

        // -------------------------------------------------
        // 4. GOAL SEEKING
        // -------------------------------------------------

        float goalTurn = Clamp(
            goalAngle / 65f,
            -1f,
            1f
        );

        // -------------------------------------------------
        // 5. OBSTACLE AVOIDANCE
        // -------------------------------------------------

        float openDirection = obstacleMode
            ? obstacleDirection
            : SelectDirection(
                left, right, leftSide, rightSide
            );

        // Front obstacle avoidance.
        float frontAvoidance =
            openDirection * frontDanger * 1.35f;

        // Diagonal obstacles.
        // Obstacle on left -> turn right.
        // Obstacle on right -> turn left.
        float diagonalAvoidance =
            0.95f * (rightDanger - leftDanger);

        // Side-wall protection.
        float sideAvoidance =
            0.65f * (rightSideDanger - leftSideDanger);

        float avoidance =
            frontAvoidance +
            diagonalAvoidance +
            sideAvoidance;

        // -------------------------------------------------
        // 6. COMBINE GOAL AND OBSTACLE STEERING
        // -------------------------------------------------

        float goalWeight = 1.0f;

        if (front < 1.40f)
            goalWeight = 0.20f;

        if (front < 0.85f)
            goalWeight = 0.08f;

        if (front < 0.50f)
            goalWeight = 0f;

        float turn =
            goalTurn * goalWeight +
            avoidance;

        // Strong turning when directly facing an obstacle.
        if (front < 0.85f)
        {
            turn =
                openDirection *
                Mathf.Max(0.75f, frontDanger);
        }

        // Escape dead ends.
        if (escapeTicks > 0)
        {
            turn =
                0.30f * turn +
                0.85f * escapeDirection;

            escapeTicks--;
        }

        // -------------------------------------------------
        // 7. EMERGENCY PROTECTION
        // -------------------------------------------------

        if (front < FRONT_CRITICAL)
        {
            turn = openDirection;
        }

        turn = Clamp(turn, -1f, 1f);

        // Smooth steering without excessive delay.
        turn =
            0.85f * turn +
            0.15f * previousTurn;

        turn = Clamp(turn, -1f, 1f);

        previousTurn = turn;

        // -------------------------------------------------
        // 8. ADAPTIVE SPEED
        // -------------------------------------------------

        float speed = 0.90f;

        // Front clearance determines maximum safe speed.
        if (front < 1.40f)
        {
            float frontSpeed = Clamp(
                (front - 0.20f) / 1.20f,
                0f,
                1f
            );

            speed = Mathf.Min(
                speed,
                0.65f * frontSpeed
            );
        }

        // Additional slowdown near diagonal obstacles.
        if (left < 0.70f || right < 0.70f)
            speed = Mathf.Min(speed, 0.25f);

        // Side clearance.
        if (leftSide < 0.30f || rightSide < 0.30f)
            speed = Mathf.Min(speed, 0.20f);

        // Slow down during sharp turns.
        float turnMagnitude = Mathf.Abs(turn);

        if (turnMagnitude > 0.45f)
            speed = Mathf.Min(speed, 0.35f);

        if (turnMagnitude > 0.75f)
            speed = Mathf.Min(speed, 0.20f);

        // Obstacle navigation.
        if (obstacleMode)
            speed = Mathf.Min(speed, 0.35f);

        // Dead-end recovery.
        if (escapeTicks > 0)
            speed = Mathf.Min(speed, 0.25f);

        // Very close front obstacle.
        if (front < 0.25f)
            speed = 0f;

        // Approach goal carefully.
        if (goalDistance < 0.45f)
            speed = Mathf.Min(speed, 0.25f);

        speed = Clamp(speed, 0f, 1f);

        return (speed, turn);
    }

    // Escape direction storage.
    private int escapeDirection = 1;
}
