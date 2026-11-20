
using System;
using UnityEngine;

public class LLM_ChatGPT_1_FB : IRoverController
{
    public string Name => "Adaptive Safe Navigation V2";

    private float front, left, right, leftSide, rightSide;

    private float[,] history = new float[5, 3];
    private int sampleCount;
    private int sampleIndex;

    private bool avoiding;
    private int avoidDirection;
    private int clearTicks;
    private int avoidTicks;

    private float previousGoalDistance;
    private int stuckTicks;
    private int recoveryTicks;
    private int recoveryDirection;
    private int totalTicks;

    private const float MAX_RANGE = 4.0f;

    private const float FRONT_SAFE = 0.95f;
    private const float DIAGONAL_SAFE = 0.65f;
    private const float SIDE_SAFE = 0.30f;

    public void ResetController()
    {
        front = MAX_RANGE;
        left = MAX_RANGE;
        right = MAX_RANGE;
        leftSide = MAX_RANGE;
        rightSide = MAX_RANGE;

        history = new float[5, 3];
        sampleCount = 0;
        sampleIndex = 0;

        avoiding = false;
        avoidDirection = 1;
        clearTicks = 0;
        avoidTicks = 0;

        previousGoalDistance = 0f;
        stuckTicks = 0;
        recoveryTicks = 0;
        recoveryDirection = 1;
        totalTicks = 0;
    }

    private float Clamp(float x, float min, float max)
    {
        return Mathf.Clamp(x, min, max);
    }

    private float Median(float a, float b, float c)
    {
        if (a > b)
        {
            float t = a;
            a = b;
            b = t;
        }

        if (b > c)
        {
            float t = b;
            b = c;
            c = t;
        }

        if (a > b)
        {
            float t = a;
            a = b;
            b = t;
        }

        return b;
    }

    private void UpdateSensors(Observation obs)
    {
        float[] readings = new float[5];

        readings[0] = Clamp(obs.front, 0.02f, MAX_RANGE);
        readings[1] = Clamp(obs.left, 0.02f, MAX_RANGE);
        readings[2] = Clamp(obs.right, 0.02f, MAX_RANGE);
        readings[3] = Clamp(obs.leftSide, 0.02f, MAX_RANGE);
        readings[4] = Clamp(obs.rightSide, 0.02f, MAX_RANGE);

        for (int i = 0; i < 5; i++)
        {
            history[i, sampleIndex] = readings[i];
        }

        sampleIndex = (sampleIndex + 1) % 3;

        if (sampleCount < 3)
            sampleCount++;

        if (sampleCount < 3)
        {
            front = readings[0];
            left = readings[1];
            right = readings[2];
            leftSide = readings[3];
            rightSide = readings[4];
            return;
        }

        float[] filtered = new float[5];

        for (int i = 0; i < 5; i++)
        {
            filtered[i] = Median(
                history[i, 0],
                history[i, 1],
                history[i, 2]
            );
        }

        front = front * 0.55f + filtered[0] * 0.45f;
        left = left * 0.55f + filtered[1] * 0.45f;
        right = right * 0.55f + filtered[2] * 0.45f;

        leftSide = leftSide * 0.55f + filtered[3] * 0.45f;
        rightSide = rightSide * 0.55f + filtered[4] * 0.45f;
    }

    private float NormalizeAngle(float angle)
    {
        while (angle > 180f) angle -= 360f;
        while (angle < -180f) angle += 360f;
        return angle;
    }

    private float LeftClearance()
    {
        return Mathf.Min(
            left * 0.85f + 0.15f,
            leftSide + 0.20f
        );
    }

    private float RightClearance()
    {
        return Mathf.Min(
            right * 0.85f + 0.15f,
            rightSide + 0.20f
        );
    }

    private int ChooseDirection(float goalAngle)
    {
        float lc = LeftClearance();
        float rc = RightClearance();

        if (leftSide < 0.28f && rightSide > 0.40f)
            return -1;

        if (rightSide < 0.28f && leftSide > 0.40f)
            return 1;

        if (lc > rc + 0.12f)
            return 1;

        if (rc > lc + 0.12f)
            return -1;

        return goalAngle >= 0f ? 1 : -1;
    }

    private bool TurnSideDanger(int direction)
    {
        if (direction > 0)
            return left < 0.48f || leftSide < 0.27f;

        return right < 0.48f || rightSide < 0.27f;
    }

    private float AvoidanceSteering()
    {
        float diagonal =
            avoidDirection > 0 ? left : right;

        float side =
            avoidDirection > 0 ? leftSide : rightSide;

        if (TurnSideDanger(avoidDirection))
        {
            int opposite = -avoidDirection;

            if (!TurnSideDanger(opposite))
            {
                if (LeftClearance() > RightClearance() &&
                    opposite > 0)
                {
                    avoidDirection = opposite;
                }
                else if (RightClearance() > LeftClearance() &&
                         opposite < 0)
                {
                    avoidDirection = opposite;
                }
            }
        }

        float result = avoidDirection * 0.75f;

        if (front < 0.75f)
            result = avoidDirection * 0.95f;

        if (front < 0.42f)
            result = avoidDirection;

        if (side < 0.65f)
        {
            float correction =
                Clamp((0.45f - side) * 1.2f, -0.35f, 0.35f);

            result += avoidDirection * correction;
        }

        if (diagonal < 0.35f || side < 0.22f)
            result = -avoidDirection;

        return Clamp(result, -1f, 1f);
    }

    private float SafeSpeed(float steeringValue)
    {
        float resultSpeed = 0.55f;

        float turnAmount = Mathf.Abs(steeringValue);

        if (turnAmount > 0.75f)
            resultSpeed = Mathf.Min(resultSpeed, 0.08f);
        else if (turnAmount > 0.45f)
            resultSpeed = Mathf.Min(resultSpeed, 0.15f);
        else if (turnAmount > 0.25f)
            resultSpeed = Mathf.Min(resultSpeed, 0.25f);

        if (front < 1.20f)
            resultSpeed = Mathf.Min(resultSpeed, front * 0.22f);

        float nearestDiagonal = Mathf.Min(left, right);

        if (nearestDiagonal < 0.80f)
        {
            resultSpeed = Mathf.Min(
                resultSpeed,
                nearestDiagonal * 0.22f
            );
        }

        float nearestSide = Mathf.Min(leftSide, rightSide);

        if (nearestSide < 0.45f)
        {
            resultSpeed = Mathf.Min(
                resultSpeed,
                nearestSide * 0.20f
            );
        }

        if (front < 0.35f ||
            left < 0.25f ||
            right < 0.25f ||
            leftSide < 0.16f ||
            rightSide < 0.16f)
        {
            resultSpeed = 0f;
        }

        return Clamp(resultSpeed, 0f, 0.55f);
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        totalTicks++;

        UpdateSensors(obs);

        float goalAngle = NormalizeAngle(obs.goalAngleDeg);
        float goalDistance = obs.goalDistance;

        if (goalDistance < 0.15f)
            return (0f, 0f);

        // 1. Progress detection

        if (totalTicks > 1)
        {
            if (goalDistance < previousGoalDistance - 0.012f)
                stuckTicks = 0;
            else
                stuckTicks++;
        }

        previousGoalDistance = goalDistance;

        // 2. Recovery

        if (stuckTicks > 100 && recoveryTicks == 0)
        {
            recoveryTicks = 35;

            recoveryDirection = -recoveryDirection;

            stuckTicks = 0;
            avoiding = true;
            avoidDirection = recoveryDirection;
            avoidTicks = 0;
            clearTicks = 0;
        }

        if (recoveryTicks > 0)
        {
            recoveryTicks--;

            float recoveryTurn = recoveryDirection;

            if (TurnSideDanger(recoveryDirection))
                recoveryTurn = -recoveryDirection;

            return (0f, recoveryTurn);
        }

        // 3. Obstacle detection

        bool frontBlocked = front < FRONT_SAFE;
        bool leftBlocked = left < DIAGONAL_SAFE;
        bool rightBlocked = right < DIAGONAL_SAFE;

        bool leftSideDanger = leftSide < SIDE_SAFE;
        bool rightSideDanger = rightSide < SIDE_SAFE;

        bool obstacleDetected =
            frontBlocked ||
            leftBlocked ||
            rightBlocked ||
            leftSideDanger ||
            rightSideDanger;

        // 4. Enter avoidance mode

        if (!avoiding && obstacleDetected)
        {
            avoiding = true;

            avoidDirection = ChooseDirection(goalAngle);

            avoidTicks = 0;
            clearTicks = 0;
        }

        // 5. Obstacle avoidance

        if (avoiding)
        {
            avoidTicks++;

            float avoidanceSteering = AvoidanceSteering();

            bool pathClear =
                front > 1.20f &&
                left > 0.90f &&
                right > 0.90f &&
                leftSide > 0.35f &&
                rightSide > 0.35f;

            if (pathClear && avoidTicks > 25)
                clearTicks++;
            else
                clearTicks = 0;

            if (clearTicks >= 15)
            {
                avoiding = false;
                clearTicks = 0;
                avoidTicks = 0;
            }
            else
            {
                float avoidanceSpeed =
                    SafeSpeed(avoidanceSteering);

                if (Mathf.Abs(avoidanceSteering) > 0.70f)
                {
                    avoidanceSpeed = Mathf.Min(
                        avoidanceSpeed, 0.08f
                    );
                }

                return (
                    avoidanceSpeed,
                    avoidanceSteering
                );
            }
        }

        // 6. Goal-directed steering

        float steering = Clamp(
            goalAngle / 70f,
            -1f,
            1f
        );

        // 7. Predictive side collision avoidance

        if (steering > 0f)
        {
            if (left < 0.75f || leftSide < 0.35f)
                steering = -0.65f;
        }
        else if (steering < 0f)
        {
            if (right < 0.75f || rightSide < 0.35f)
                steering = 0.65f;
        }

        if (front < 0.95f)
        {
            steering = ChooseDirection(goalAngle) * 0.85f;
        }

        // 8. Speed control

        float speed = SafeSpeed(steering);

        if (Mathf.Abs(goalAngle) > 100f)
            speed = Mathf.Min(speed, 0.10f);

        if (Mathf.Abs(goalAngle) > 145f)
            speed = 0f;

        if (goalDistance < 0.70f)
            speed = Mathf.Min(speed, 0.20f);

        // 9. Final safety check

        if (front < 0.35f ||
            (steering > 0f &&
             (left < 0.25f || leftSide < 0.16f)) ||
            (steering < 0f &&
             (right < 0.25f || rightSide < 0.16f)))
        {
            speed = 0f;
        }

        return (
            Clamp(speed, 0f, 1f),
            Clamp(steering, -1f, 1f)
        );
    }
}
