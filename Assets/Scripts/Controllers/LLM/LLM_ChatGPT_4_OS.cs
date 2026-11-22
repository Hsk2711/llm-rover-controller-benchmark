
using UnityEngine;

public class LLM_ChatGPT_4_OS : IRoverController
{
    public string Name => "Hybrid Bug Navigation Controller";

    // Navigation states
    private bool wallFollowing;
    private int followSide; // -1 = left, +1 = right

    // Sensor history
    private float prevFront;
    private float prevLeft;
    private float prevRight;
    private float prevLeftSide;
    private float prevRightSide;

    // Progress monitoring
    private float bestGoalDistance;
    private int stuckTicks;
    private int wallTicks;
    private int totalTicks;
    private int lastSwitchTick;

    // Navigation parameters
    private const float MAX_RANGE = 4.0f;
    private const float FRONT_DANGER = 0.32f;
    private const float FRONT_TRIGGER = 0.58f;
    private const float SIDE_DANGER = 0.23f;
    private const float SIDE_TARGET = 0.42f;
    private const float DIAGONAL_TRIGGER = 0.55f;

    private const int STUCK_LIMIT = 60;
    private const int WALL_MIN_TIME = 12;

    public void ResetController()
    {
        wallFollowing = false;
        followSide = 1;

        prevFront = MAX_RANGE;
        prevLeft = MAX_RANGE;
        prevRight = MAX_RANGE;
        prevLeftSide = MAX_RANGE;
        prevRightSide = MAX_RANGE;

        bestGoalDistance = float.MaxValue;
        stuckTicks = 0;
        wallTicks = 0;
        totalTicks = 0;
        lastSwitchTick = -10000;
    }

    // Conservative filtering:
    // A sudden false maximum reading cannot immediately
    // make an obstacle disappear.
    private float Filter(float current, float previous)
    {
        current = Mathf.Clamp(current, 0.02f, MAX_RANGE);

        if (current >= MAX_RANGE - 0.001f)
            return previous;

        return Mathf.Min(current, previous * 0.65f +
                         current * 0.35f);
    }

    private float SmoothUpdate(float current, ref float previous)
    {
        current = Mathf.Clamp(current, 0.02f, MAX_RANGE);

        float result = Filter(current, previous);

        previous = Mathf.Clamp(
            previous * 0.55f + current * 0.45f,
            0.02f,
            MAX_RANGE
        );

        return result;
    }

    private float ClampTurn(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }

    private float ClampSpeed(float value)
    {
        return Mathf.Clamp(value, 0f, 1f);
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        totalTicks++;

        // Read and filter sensors
        float front = SmoothUpdate(obs.front, ref prevFront);
        float left = SmoothUpdate(obs.left, ref prevLeft);
        float right = SmoothUpdate(obs.right, ref prevRight);

        float leftSide = SmoothUpdate(
            obs.leftSide, ref prevLeftSide);

        float rightSide = SmoothUpdate(
            obs.rightSide, ref prevRightSide);

        float goalDistance = Mathf.Max(0f, obs.goalDistance);

        float goalAngle = Mathf.Clamp(
            obs.goalAngleDeg, -180f, 180f);

        float absGoalAngle = Mathf.Abs(goalAngle);

        // -------------------------------------------------
        // 1. Monitor progress towards the goal
        // -------------------------------------------------

        if (goalDistance < bestGoalDistance - 0.025f)
        {
            bestGoalDistance = goalDistance;
            stuckTicks = 0;
        }
        else
        {
            stuckTicks++;
        }

        // Detect prolonged lack of progress.
        // Change wall-following direction to escape
        // possible U-shaped dead ends.
        if (stuckTicks >= STUCK_LIMIT &&
            totalTicks - lastSwitchTick > STUCK_LIMIT)
        {
            followSide = -followSide;

            wallFollowing = true;
            stuckTicks = 0;
            wallTicks = 0;
            lastSwitchTick = totalTicks;

            bestGoalDistance = goalDistance;
        }

        // -------------------------------------------------
        // 2. Detect nearby obstacles
        // -------------------------------------------------

        bool frontBlocked = front < FRONT_TRIGGER;

        bool leftBlocked =
            left < DIAGONAL_TRIGGER ||
            leftSide < SIDE_DANGER + 0.12f;

        bool rightBlocked =
            right < DIAGONAL_TRIGGER ||
            rightSide < SIDE_DANGER + 0.12f;

        // Enter wall-following mode when necessary.
        if (!wallFollowing &&
            (frontBlocked || leftBlocked || rightBlocked))
        {
            wallFollowing = true;
            wallTicks = 0;

            // Select the side with more available space.
            float leftClearance =
                Mathf.Min(left, leftSide);

            float rightClearance =
                Mathf.Min(right, rightSide);

            if (leftClearance > rightClearance + 0.08f)
                followSide = -1;
            else if (rightClearance > leftClearance + 0.08f)
                followSide = 1;
            else
                followSide = goalAngle >= 0f ? -1 : 1;
        }

        // -------------------------------------------------
        // 3. Normal goal-seeking navigation
        // -------------------------------------------------

        if (!wallFollowing)
        {
            float angleError = goalAngle / 90f;

            // Proportional steering towards the goal.
            float turn = ClampTurn(angleError * 0.85f);

            // Reduce speed while turning.
            float speed = 1f -
                Mathf.Clamp01(absGoalAngle / 100f) * 0.65f;

            // Additional obstacle avoidance using
            // diagonal sensors.
            float leftRepulsion =
                Mathf.Clamp01((0.9f - left) / 0.9f);

            float rightRepulsion =
                Mathf.Clamp01((0.9f - right) / 0.9f);

            float avoidance =
                rightRepulsion - leftRepulsion;

            turn += avoidance * 0.65f;

            // Front obstacle protection.
            if (front < 0.45f)
            {
                speed = 0f;

                turn = goalAngle >= 0f ? 1f : -1f;
            }
            else if (front < 0.75f)
            {
                speed *= 0.4f;
            }

            speed = Mathf.Min(
                speed,
                Mathf.Clamp01((front - 0.18f) / 0.65f)
            );

            return (ClampSpeed(speed), ClampTurn(turn));
        }

        // -------------------------------------------------
        // 4. Wall-following navigation
        // -------------------------------------------------

        wallTicks++;

        float selectedSide =
            followSide == -1 ? leftSide : rightSide;

        float selectedDiagonal =
            followSide == -1 ? left : right;

        // Determine distance error from the wall.
        float distanceError = selectedSide - SIDE_TARGET;

        // Positive steering means left.
        // If following the left wall, steer away
        // when too close and towards it when too far.
        float wallTurn;

        if (followSide == -1)
        {
            wallTurn = -distanceError * 2.0f;
        }
        else
        {
            wallTurn = distanceError * 2.0f;
        }

        // Maintain forward movement along the obstacle.
        float goalInfluence = Mathf.Clamp(
            goalAngle / 180f, -1f, 1f
        );

        float turnCommand =
            wallTurn * 0.75f +
            goalInfluence * 0.20f;

        // React to the diagonal obstacle.
        float diagonalRepulsion =
            Mathf.Clamp01((0.65f - selectedDiagonal) / 0.65f);

        if (followSide == -1)
            turnCommand -= diagonalRepulsion * 0.45f;
        else
            turnCommand += diagonalRepulsion * 0.45f;

        // If directly blocked, turn away from the wall
        // rather than continuing forward.
        float speedCommand = 0.55f;

        if (front < FRONT_DANGER)
        {
            speedCommand = 0f;

            turnCommand = followSide == -1 ? -1f : 1f;
        }
        else if (front < FRONT_TRIGGER)
        {
            speedCommand = 0.12f;

            turnCommand += followSide == -1
                ? -0.25f : 0.25f;
        }
        else if (front < 0.85f)
        {
            speedCommand = 0.30f;
        }

        // Avoid getting too close to either side.
        if (leftSide < SIDE_DANGER)
        {
            turnCommand -= 0.35f;
            speedCommand = Mathf.Min(speedCommand, 0.25f);
        }

        if (rightSide < SIDE_DANGER)
        {
            turnCommand += 0.35f;
            speedCommand = Mathf.Min(speedCommand, 0.25f);
        }

        // Reduce speed during large steering corrections.
        speedCommand *=
            1f - Mathf.Clamp01(Mathf.Abs(turnCommand)) * 0.45f;

        // -------------------------------------------------
        // 5. Decide when to leave wall-following mode
        // -------------------------------------------------

        bool pathClear =
            front > 0.90f &&
            left > 0.75f &&
            right > 0.75f;

        bool goalAligned = absGoalAngle < 22f;

        if (wallTicks > WALL_MIN_TIME &&
            pathClear &&
            goalAligned)
        {
            wallFollowing = false;
            wallTicks = 0;
            stuckTicks = 0;

            bestGoalDistance = goalDistance;

            // Resume direct goal navigation.
            return (0.45f, ClampTurn(goalAngle / 90f));
        }

        // -------------------------------------------------
        // 6. Final safety and output
        // -------------------------------------------------

        // Never intentionally drive forward into
        // an extremely close frontal obstacle.
        if (front < 0.20f)
            speedCommand = 0f;

        return (
            ClampSpeed(speedCommand),
            ClampTurn(turnCommand)
        );
    }
}
