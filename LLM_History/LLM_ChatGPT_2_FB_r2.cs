using System;
using UnityEngine;

public class LLM_ChatGPT_2_FB : IRoverController
{
    public string Name => "Adaptive Navigation V3";

    // Sensor indices:
    // 0 Front
    // 1 Left (+45)
    // 2 Right (-45)
    // 3 LeftSide (+90)
    // 4 RightSide (-90)

    private float[] sensors = new float[5];
    private int[] falseClear = new int[5];

    private bool initialized;

    // Progress monitoring
    private float bestGoalDistance;
    private int stuckTicks;
    private int recoveryTicks;
    private int recoveryCooldown;

    // Recovery direction
    private int recoveryDirection;
    private int preferredDirection;

    // Steering memory
    private float previousTurn;

    // Dead-end detection
    private int blockedTicks;

    private const float MAX_RANGE = 4f;

    public void ResetController()
    {
        for (int i = 0; i < 5; i++)
        {
            sensors[i] = MAX_RANGE;
            falseClear[i] = 0;
        }

        initialized = false;

        bestGoalDistance = 999f;
        stuckTicks = 0;
        recoveryTicks = 0;
        recoveryCooldown = 0;

        recoveryDirection = 1;
        preferredDirection = 1;

        previousTurn = 0f;
        blockedTicks = 0;
    }

    private float Clamp(float value, float min, float max)
    {
        return Mathf.Clamp(value, min, max);
    }

    // ---------------------------------------------
    // SENSOR FILTERING
    // ---------------------------------------------

    private float ReadSensor(float raw, int index)
    {
        raw = Clamp(raw, 0.02f, MAX_RANGE);

        if (!initialized)
        {
            sensors[index] = raw;
            return raw;
        }

        // Ignore occasional false-clear readings.
        if (raw >= 3.95f && sensors[index] < 3.5f)
        {
            falseClear[index]++;

            if (falseClear[index] < 3)
                return sensors[index];
        }
        else
        {
            falseClear[index] = 0;
        }

        // React quickly to approaching obstacles.
        if (raw < sensors[index])
        {
            sensors[index] =
                0.80f * raw + 0.20f * sensors[index];
        }
        else
        {
            sensors[index] =
                0.30f * raw + 0.70f * sensors[index];
        }

        return sensors[index];
    }

    private float Danger(float distance, float threshold)
    {
        return Clamp(
            (threshold - distance) / threshold,
            0f,
            1f
        );
    }

    // ---------------------------------------------
    // DIRECTION SELECTION
    // ---------------------------------------------

    private int ChooseDirection(
        float left,
        float right,
        float leftSide,
        float rightSide,
        float goalAngle)
    {
        // Score clearance in both directions.
        float leftScore =
            0.55f * left +
            0.45f * leftSide;

        float rightScore =
            0.55f * right +
            0.45f * rightSide;

        // Slight preference for the direction
        // that brings the rover towards the goal.
        if (goalAngle > 15f)
            leftScore += 0.20f;

        if (goalAngle < -15f)
            rightScore += 0.20f;

        // Avoid switching directions unnecessarily.
        if (Mathf.Abs(leftScore - rightScore) < 0.12f)
            return preferredDirection;

        int direction =
            leftScore > rightScore ? 1 : -1;

        preferredDirection = direction;

        return direction;
    }

    // ---------------------------------------------
    // RECOVERY ACTIVATION
    // ---------------------------------------------

    private void StartRecovery(int direction)
    {
        recoveryDirection = direction;

        // Approximately two seconds of turning.
        // Reassess after this period.
        recoveryTicks = 40;

        stuckTicks = 0;
        blockedTicks = 0;

        recoveryCooldown = 55;
    }

    // ---------------------------------------------
    // MAIN CONTROLLER
    // ---------------------------------------------

    public (float speed01, float turn) Decide(Observation obs)
    {
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

        // -----------------------------------------
        // 1. GOAL REACHED
        // -----------------------------------------

        if (goalDistance < 0.18f)
        {
            previousTurn = 0f;
            return (0f, 0f);
        }

        // -----------------------------------------
        // 2. PROGRESS MONITORING
        // -----------------------------------------

        if (goalDistance < bestGoalDistance - 0.025f)
        {
            bestGoalDistance = goalDistance;
            stuckTicks = 0;
        }
        else
        {
            stuckTicks++;
        }

        if (recoveryCooldown > 0)
            recoveryCooldown--;

        // -----------------------------------------
        // 3. DEAD-END DETECTION
        // -----------------------------------------

        bool frontBlocked = front < 0.65f;

        bool leftBlocked = left < 0.70f;
        bool rightBlocked = right < 0.70f;

        bool bothSidesBlocked =
            leftBlocked && rightBlocked;

        if (frontBlocked && bothSidesBlocked)
        {
            blockedTicks++;
        }
        else
        {
            blockedTicks = Mathf.Max(
                0, blockedTicks - 1
            );
        }

        // Detect dead ends even if the goal distance
        // temporarily increases during navigation.
        bool deadEnd =
            blockedTicks >= 5;

        // General lack of progress.
        bool noProgress =
            stuckTicks >= 50;

        if (recoveryTicks <= 0 &&
            recoveryCooldown <= 0 &&
            (deadEnd || noProgress))
        {
            int direction = ChooseDirection(
                left,
                right,
                leftSide,
                rightSide,
                goalAngle
            );

            StartRecovery(direction);
        }

        // -----------------------------------------
        // 4. RECOVERY MODE
        // -----------------------------------------

        if (recoveryTicks > 0)
        {
            recoveryTicks--;

            // Turn consistently instead of repeatedly
            // switching between left and right.

            float recoveryTurn =
                recoveryDirection * 1.0f;

            // Slow forward arc to turn around safely.
            float recoverySpeed = 0.22f;

            // If the front is still blocked, maintain
            // the turning direction and reduce speed.
            if (front < 0.45f)
            {
                recoverySpeed = 0.10f;
            }

            // If the rover has turned towards an open
            // direction, it can begin moving faster.
            if (front > 1.0f &&
                recoveryTicks < 20)
            {
                recoverySpeed = 0.30f;
            }

            previousTurn = recoveryTurn;

            return (
                recoverySpeed,
                recoveryTurn
            );
        }

        // -----------------------------------------
        // 5. GOAL SEEKING
        // -----------------------------------------

        float goalTurn = Clamp(
            goalAngle / 65f,
            -1f,
            1f
        );

        // -----------------------------------------
        // 6. OBSTACLE AVOIDANCE
        // -----------------------------------------

        float frontDanger =
            Danger(front, 1.10f);

        float leftDanger =
            Danger(left, 0.90f);

        float rightDanger =
            Danger(right, 0.90f);

        float leftSideDanger =
            Danger(leftSide, 0.48f);

        float rightSideDanger =
            Danger(rightSide, 0.48f);

        // Prefer the direction with more clearance.
        int openDirection = ChooseDirection(
            left,
            right,
            leftSide,
            rightSide,
            goalAngle
        );

        // Front avoidance.
        float frontAvoidance =
            openDirection * frontDanger * 1.45f;

        // Diagonal avoidance.
        float diagonalAvoidance =
            1.05f * (rightDanger - leftDanger);

        // Side protection.
        float sideAvoidance =
            0.65f * (rightSideDanger - leftSideDanger);

        float avoidance =
            frontAvoidance +
            diagonalAvoidance +
            sideAvoidance;

        // -----------------------------------------
        // 7. DYNAMIC STEERING PRIORITY
        // -----------------------------------------

        float goalWeight = 1.0f;

        if (front < 1.20f)
            goalWeight = 0.35f;

        if (front < 0.75f)
            goalWeight = 0.10f;

        if (front < 0.45f)
            goalWeight = 0f;

        float turn =
            goalTurn * goalWeight +
            avoidance;

        // Strong emergency turning.
        if (front < 0.45f)
        {
            turn = openDirection;
        }

        // Prevent excessive steering.
        turn = Clamp(turn, -1f, 1f);

        // Moderate smoothing.
        turn =
            0.85f * turn +
            0.15f * previousTurn;

        turn = Clamp(turn, -1f, 1f);

        previousTurn = turn;

        // -----------------------------------------
        // 8. ADAPTIVE SPEED CONTROL
        // -----------------------------------------

        float speed = 0.85f;

        // Gradual speed reduction near front obstacles.
        if (front < 1.50f)
        {
            float safeSpeed = Clamp(
                (front - 0.22f) / 1.28f,
                0f,
                1f
            );

            speed = Mathf.Min(
                speed,
                0.70f * safeSpeed
            );
        }

        // Diagonal clearance.
        if (left < 0.65f || right < 0.65f)
        {
            speed = Mathf.Min(speed, 0.30f);
        }

        // Side clearance.
        if (leftSide < 0.27f ||
            rightSide < 0.27f)
        {
            speed = Mathf.Min(speed, 0.25f);
        }

        // Turning speed.
        float turnMagnitude = Mathf.Abs(turn);

        if (turnMagnitude > 0.50f)
            speed = Mathf.Min(speed, 0.40f);

        if (turnMagnitude > 0.80f)
            speed = Mathf.Min(speed, 0.25f);

        // Extremely close obstacle.
        if (front < 0.22f)
            speed = 0f;

        // Approach the goal carefully.
        if (goalDistance < 0.40f)
            speed = Mathf.Min(speed, 0.25f);

        speed = Clamp(speed, 0f, 1f);

        return (speed, turn);
    }
}
