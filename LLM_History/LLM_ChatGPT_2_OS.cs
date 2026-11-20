using System;
using UnityEngine;

public class LLM_ChatGPT_2_OS : IRoverController
{
    public string Name => "Adaptive Hybrid Navigation";

    // Sensor filtering
    private float[] filtered = new float[5];
    private bool initialized = false;
    private int[] falseClearCount = new int[5];

    // Progress tracking
    private float bestGoalDistance;
    private int stuckTicks;
    private int escapeTicks;
    private int totalTicks;

    // Escape direction: +1 = left, -1 = right
    private int escapeDirection;

    // Previous steering
    private float previousTurn;

    private const float MAX_SPEED = 0.3f;

    public void ResetController()
    {
        for (int i = 0; i < 5; i++)
        {
            filtered[i] = 4f;
            falseClearCount[i] = 0;
        }

        initialized = false;
        bestGoalDistance = 999f;
        stuckTicks = 0;
        escapeTicks = 0;
        totalTicks = 0;
        escapeDirection = 1;
        previousTurn = 0f;
    }

    // Clamp a value
    private float Clamp(float value, float min, float max)
    {
        return Mathf.Clamp(value, min, max);
    }

    // Normalize sensor readings
    private float FilterSensor(float raw, int index)
    {
        raw = Clamp(raw, 0.02f, 4f);

        if (!initialized)
        {
            filtered[index] = raw;
            return raw;
        }

        // Immediately react to closer obstacles
        if (raw < filtered[index])
        {
            filtered[index] =
                0.65f * raw + 0.35f * filtered[index];

            falseClearCount[index] = 0;
        }
        else
        {
            // Protect against occasional false 4m readings
            if (raw >= 3.95f && filtered[index] < 3.5f)
            {
                falseClearCount[index]++;

                if (falseClearCount[index] < 3)
                    return filtered[index];
            }
            else
            {
                falseClearCount[index] = 0;
            }

            // Gradually release the filtered reading
            filtered[index] =
                0.20f * raw + 0.80f * filtered[index];
        }

        return filtered[index];
    }

    // Convert obstacle distance into an avoidance strength
    private float Danger(float distance, float threshold)
    {
        if (distance >= threshold)
            return 0f;

        return Clamp(
            (threshold - distance) / threshold,
            0f,
            1f
        );
    }

    // Select the more open direction
    private int ChooseOpenDirection(
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        float leftSpace =
            0.65f * left + 0.35f * leftSide;

        float rightSpace =
            0.65f * right + 0.35f * rightSide;

        if (Mathf.Abs(leftSpace - rightSpace) < 0.08f)
            return escapeDirection;

        return leftSpace > rightSpace ? 1 : -1;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        totalTicks++;

        // Read and filter sensors
        float front = FilterSensor(obs.front, 0);
        float left = FilterSensor(obs.left, 1);
        float right = FilterSensor(obs.right, 2);
        float leftSide = FilterSensor(obs.leftSide, 3);
        float rightSide = FilterSensor(obs.rightSide, 4);

        initialized = true;

        float goalDistance = Mathf.Max(0f, obs.goalDistance);

        float goalAngle = Clamp(
            obs.goalAngleDeg,
            -180f,
            180f
        );

        // -------------------------------------------------
        // 1. Goal reached
        // -------------------------------------------------

        if (goalDistance < 0.18f)
        {
            previousTurn = 0f;
            return (0f, 0f);
        }

        // -------------------------------------------------
        // 2. Detect lack of progress
        // -------------------------------------------------

        if (goalDistance < bestGoalDistance - 0.035f)
        {
            bestGoalDistance = goalDistance;
            stuckTicks = 0;
        }
        else
        {
            stuckTicks++;
        }

        // Trigger an escape manoeuvre if progress stops
        if (stuckTicks > 35 && escapeTicks <= 0)
        {
            escapeDirection = ChooseOpenDirection(
                left, right, leftSide, rightSide
            );

            escapeTicks = 50;
            stuckTicks = 0;
        }

        // -------------------------------------------------
        // 3. Goal-seeking steering
        // -------------------------------------------------

        float goalTurn = Clamp(
            goalAngle / 75f,
            -1f,
            1f
        );

        // Reduce goal attraction during escape
        if (escapeTicks > 0)
        {
            goalTurn *= 0.30f;
            escapeTicks--;
        }

        // -------------------------------------------------
        // 4. Obstacle avoidance
        // -------------------------------------------------

        float frontDanger = Danger(front, 0.85f);

        float leftDanger = Danger(left, 0.75f);
        float rightDanger = Danger(right, 0.75f);

        float leftSideDanger = Danger(leftSide, 0.42f);
        float rightSideDanger = Danger(rightSide, 0.42f);

        // Determine which side has more available space
        float leftSpace =
            0.65f * left + 0.35f * leftSide;

        float rightSpace =
            0.65f * right + 0.35f * rightSide;

        float openDirection =
            leftSpace >= rightSpace ? 1f : -1f;

        // Front obstacle: steer towards the more open side
        float frontAvoidance =
            openDirection * frontDanger * 1.15f;

        // Diagonal obstacle avoidance
        float diagonalAvoidance =
            rightDanger * 0.85f -
            leftDanger * 0.85f;

        // Side obstacle avoidance
        float sideAvoidance =
            rightSideDanger * 0.55f -
            leftSideDanger * 0.55f;

        // Combine all steering influences
        float avoidance =
            frontAvoidance +
            diagonalAvoidance +
            sideAvoidance;

        // Give obstacle avoidance priority
        float avoidanceWeight =
            frontDanger > 0.5f ? 1.5f : 1.0f;

        float turn =
            goalTurn +
            avoidance * avoidanceWeight;

        // Escape mode: encourage turning away from dead ends
        if (escapeTicks > 0)
        {
            turn += escapeDirection * 0.45f;
        }

        // -------------------------------------------------
        // 5. Emergency obstacle response
        // -------------------------------------------------

        if (front < 0.30f)
        {
            turn = openDirection;

            // Do not move rapidly when extremely close
        }

        // Prevent excessive steering oscillation
        turn = Clamp(turn, -1f, 1f);

        // Light steering smoothing
        turn = 0.75f * turn + 0.25f * previousTurn;

        turn = Clamp(turn, -1f, 1f);

        previousTurn = turn;

        // -------------------------------------------------
        // 6. Adaptive speed control
        // -------------------------------------------------

        float speed = 0.85f;

        // Reduce speed near front obstacles
        if (front < 1.0f)
        {
            speed = Mathf.Min(
                speed,
                0.25f + 0.60f * (front - 0.20f)
            );
        }

        // Slow down when diagonal sensors detect obstacles
        if (left < 0.45f || right < 0.45f)
            speed = Mathf.Min(speed, 0.35f);

        // Slow down when very close to a side wall
        if (leftSide < 0.24f || rightSide < 0.24f)
            speed = Mathf.Min(speed, 0.30f);

        // Slow down during sharp turns
        float turnMagnitude = Mathf.Abs(turn);

        if (turnMagnitude > 0.65f)
            speed = Mathf.Min(speed, 0.30f);

        if (turnMagnitude > 0.85f)
            speed = Mathf.Min(speed, 0.18f);

        // Escape manoeuvres use lower speed
        if (escapeTicks > 0)
            speed = Mathf.Min(speed, 0.30f);

        // Stop forward movement if an obstacle is extremely close
        if (front < 0.20f)
            speed = 0f;

        // Goal alignment: approach cautiously when close
        if (goalDistance < 0.45f)
            speed = Mathf.Min(speed, 0.30f);

        speed = Clamp(speed, 0f, 1f);

        return (speed, turn);
    }
}
