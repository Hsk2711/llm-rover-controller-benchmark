
using UnityEngine;

public class LLM_ChatGPT_4_FB : IRoverController
{
    public string Name => "Adaptive Dead-End Recovery Rover";

    // Navigation states
    private bool wallFollowing;
    private bool recovering;

    // -1 = left wall, +1 = right wall
    private int followSide;

    // Recovery state
    private int recoveryTicks;
    private int recoveryDirection;
    private int recoveryCount;
    private int lastRecoveryTick;

    // Sensor memory
    private float frontFiltered;
    private float leftFiltered;
    private float rightFiltered;
    private float leftSideFiltered;
    private float rightSideFiltered;

    private int frontClearCount;
    private int leftClearCount;
    private int rightClearCount;
    private int leftSideClearCount;
    private int rightSideClearCount;

    // Progress monitoring
    private float bestGoalDistance;
    private int stuckTicks;
    private int wallTicks;
    private int totalTicks;

    // Constants
    private const float MAX_RANGE = 4f;

    private const float FRONT_TRIGGER = 0.65f;
    private const float FRONT_DANGER = 0.38f;
    private const float FRONT_STOP = 0.22f;

    private const float DIAGONAL_DANGER = 0.48f;
    private const float SIDE_DANGER = 0.25f;

    private const float WALL_TARGET = 0.40f;

    private const int STUCK_LIMIT = 70;
    private const int RECOVERY_DURATION = 26;
    private const int RECOVERY_COOLDOWN = 80;
    private const int MIN_WALL_TIME = 15;

    public void ResetController()
    {
        wallFollowing = false;
        recovering = false;

        followSide = 1;

        recoveryTicks = 0;
        recoveryDirection = 1;
        recoveryCount = 0;
        lastRecoveryTick = -10000;

        frontFiltered = MAX_RANGE;
        leftFiltered = MAX_RANGE;
        rightFiltered = MAX_RANGE;
        leftSideFiltered = MAX_RANGE;
        rightSideFiltered = MAX_RANGE;

        frontClearCount = 0;
        leftClearCount = 0;
        rightClearCount = 0;
        leftSideClearCount = 0;
        rightSideClearCount = 0;

        bestGoalDistance = float.MaxValue;
        stuckTicks = 0;
        wallTicks = 0;
        totalTicks = 0;
    }

    // -------------------------------------------------
    // SENSOR FILTERING
    // -------------------------------------------------

    private float FilterSensor(
        float raw,
        ref float previous,
        ref int clearCount)
    {
        raw = Mathf.Clamp(raw, 0.02f, MAX_RANGE);

        // Detect occasional false maximum readings.
        if (raw >= 3.95f)
        {
            clearCount++;

            // Ignore the first two isolated maximum readings.
            if (clearCount <= 2)
                return previous;

            // Gradually release the old obstacle reading.
            previous = Mathf.Min(
                MAX_RANGE,
                previous + 0.25f
            );

            return previous;
        }

        clearCount = 0;

        // React quickly to approaching obstacles.
        if (raw < previous)
        {
            previous = Mathf.Min(
                raw,
                previous * 0.35f + raw * 0.65f
            );
        }
        else
        {
            // Smooth increases to reduce noise.
            previous =
                previous * 0.75f + raw * 0.25f;
        }

        return previous;
    }

    private float ClampTurn(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }

    private float ClampSpeed(float value)
    {
        return Mathf.Clamp(value, 0f, 1f);
    }

    // -------------------------------------------------
    // OPEN-SPACE EVALUATION
    // -------------------------------------------------

    private float GetLeftClearance()
    {
        return Mathf.Min(
            leftFiltered,
            leftSideFiltered
        );
    }

    private float GetRightClearance()
    {
        return Mathf.Min(
            rightFiltered,
            rightSideFiltered
        );
    }

    // Positive = turn left.
    // Negative = turn right.
    private int GetBestTurnDirection(float goalAngle)
    {
        float leftSpace = GetLeftClearance();
        float rightSpace = GetRightClearance();

        if (leftSpace > rightSpace + 0.10f)
            return 1;

        if (rightSpace > leftSpace + 0.10f)
            return -1;

        // If both sides look similar, use goal direction.
        if (Mathf.Abs(goalAngle) > 15f)
            return goalAngle > 0f ? 1 : -1;

        // Alternate direction in ambiguous situations.
        return recoveryCount % 2 == 0 ? 1 : -1;
    }

    // -------------------------------------------------
    // DEAD-END RECOVERY
    // -------------------------------------------------

    private void StartRecovery(float goalAngle)
    {
        recovering = true;
        wallFollowing = false;

        recoveryDirection =
            GetBestTurnDirection(goalAngle);

        recoveryTicks = RECOVERY_DURATION;

        recoveryCount++;

        lastRecoveryTick = totalTicks;

        stuckTicks = 0;
        wallTicks = 0;
    }

    private (float speed01, float turn) Recover()
    {
        recoveryTicks--;

        // Rotate in place to find an alternative opening.
        float turn = recoveryDirection * 1f;

        if (recoveryTicks <= 0)
        {
            recovering = false;

            // After turning towards open space,
            // keep the obstacle on the opposite side.
            followSide = -recoveryDirection;

            wallFollowing = true;
            wallTicks = 0;
        }

        return (0f, turn);
    }

    // -------------------------------------------------
    // PROGRESS MONITORING
    // -------------------------------------------------

    private bool CheckStuck(float goalDistance)
    {
        if (goalDistance < bestGoalDistance - 0.04f)
        {
            bestGoalDistance = goalDistance;
            stuckTicks = 0;

            return false;
        }

        stuckTicks++;

        if (stuckTicks >= STUCK_LIMIT &&
            totalTicks - lastRecoveryTick >= RECOVERY_COOLDOWN)
        {
            return true;
        }

        return false;
    }

    // -------------------------------------------------
    // DIRECT GOAL NAVIGATION
    // -------------------------------------------------

    private (float speed01, float turn) NavigateToGoal(
        float goalAngle,
        float front,
        float left,
        float right)
    {
        float absAngle = Mathf.Abs(goalAngle);

        // Proportional goal steering.
        float turn = goalAngle * 0.018f;

        // Obstacle repulsion.
        float leftRepulsion =
            Mathf.Clamp01((0.85f - left) / 0.85f);

        float rightRepulsion =
            Mathf.Clamp01((0.85f - right) / 0.85f);

        // An obstacle on the left requires steering right.
        turn += (rightRepulsion - leftRepulsion) * 0.65f;

        // Speed control based on steering demand.
        float speed = 0.90f;

        speed *=
            1f - Mathf.Clamp01(absAngle / 110f) * 0.65f;

        // Front obstacle protection.
        if (front < FRONT_DANGER)
        {
            speed = 0f;

            // Select the more open turning direction.
            float leftSpace = GetLeftClearance();
            float rightSpace = GetRightClearance();

            turn = leftSpace > rightSpace ? 1f : -1f;
        }
        else if (front < FRONT_TRIGGER)
        {
            speed = Mathf.Min(speed, 0.20f);
        }

        // Additional protection against frontal collision.
        if (front < FRONT_STOP)
            speed = 0f;

        // Reduce speed for sharp steering.
        speed *=
            1f - Mathf.Abs(ClampTurn(turn)) * 0.35f;

        return (
            ClampSpeed(speed),
            ClampTurn(turn)
        );
    }

    // -------------------------------------------------
    // WALL FOLLOWING
    // -------------------------------------------------

    private (float speed01, float turn) FollowWall(
        float goalAngle,
        float front,
        float left,
        float right,
        float leftSide,
        float rightSide)
    {
        wallTicks++;

        float sideDistance =
            followSide == -1 ? leftSide : rightSide;

        float diagonalDistance =
            followSide == -1 ? left : right;

        // Correct wall-following steering:
        // Left wall: too far = turn left.
        // Right wall: too far = turn right.
        float distanceError =
            sideDistance - WALL_TARGET;

        float wallTurn =
            followSide == -1
                ? distanceError * 2.0f
                : -distanceError * 2.0f;

        float turn = wallTurn * 0.75f;

        // Allow the goal direction to influence navigation.
        turn += Mathf.Clamp(goalAngle / 180f, -1f, 1f)
                * 0.25f;

        // Diagonal obstacle avoidance.
        float diagonalRepulsion =
            Mathf.Clamp01(
                (0.65f - diagonalDistance) / 0.65f
            );

        if (followSide == -1)
            turn -= diagonalRepulsion * 0.50f;
        else
            turn += diagonalRepulsion * 0.50f;

        float speed = 0.55f;

        // Handle frontal obstacles.
        if (front < FRONT_DANGER)
        {
            speed = 0f;

            // Turn away from the wall.
            turn = followSide == -1 ? -1f : 1f;
        }
        else if (front < FRONT_TRIGGER)
        {
            speed = 0.15f;

            turn += followSide == -1
                ? -0.20f : 0.20f;
        }
        else if (front < 0.90f)
        {
            speed = 0.30f;
        }

        // Side collision protection.
        if (leftSide < SIDE_DANGER)
        {
            turn -= 0.40f;
            speed = Mathf.Min(speed, 0.18f);
        }

        if (rightSide < SIDE_DANGER)
        {
            turn += 0.40f;
            speed = Mathf.Min(speed, 0.18f);
        }

        // Prevent forward movement when extremely close.
        if (front < FRONT_STOP)
            speed = 0f;

        // Slow down during large steering corrections.
        speed *=
            1f - Mathf.Abs(ClampTurn(turn)) * 0.40f;

        // -------------------------------------------------
        // WALL-FOLLOWING EXIT CONDITIONS
        // -------------------------------------------------

        bool frontClear = front > 0.85f;

        bool goalAligned =
            Mathf.Abs(goalAngle) < 35f;

        bool wallLost =
            sideDistance > 1.10f &&
            diagonalDistance > 1.00f;

        bool goalPathClear =
            goalAligned && frontClear;

        bool alternativePathFound =
            wallLost &&
            front > 0.75f &&
            Mathf.Abs(goalAngle) < 80f;

        if (wallTicks > MIN_WALL_TIME &&
            (goalPathClear || alternativePathFound))
        {
            wallFollowing = false;

            wallTicks = 0;
            stuckTicks = 0;

            return NavigateToGoal(
                goalAngle,
                front,
                left,
                right
            );
        }

        return (
            ClampSpeed(speed),
            ClampTurn(turn)
        );
    }

    // -------------------------------------------------
    // MAIN DECISION FUNCTION
    // -------------------------------------------------

    public (float speed01, float turn) Decide(Observation obs)
    {
        totalTicks++;

        // 1. Filter all five sensors.
        float front = FilterSensor(
            obs.front,
            ref frontFiltered,
            ref frontClearCount
        );

        float left = FilterSensor(
            obs.left,
            ref leftFiltered,
            ref leftClearCount
        );

        float right = FilterSensor(
            obs.right,
            ref rightFiltered,
            ref rightClearCount
        );

        float leftSide = FilterSensor(
            obs.leftSide,
            ref leftSideFiltered,
            ref leftSideClearCount
        );

        float rightSide = FilterSensor(
            obs.rightSide,
            ref rightSideFiltered,
            ref rightSideClearCount
        );

        float goalDistance =
            Mathf.Max(0f, obs.goalDistance);

        float goalAngle =
            Mathf.Clamp(obs.goalAngleDeg, -180f, 180f);

        // 2. Check progress.
        if (CheckStuck(goalDistance) && !recovering)
        {
            StartRecovery(goalAngle);
        }

        // 3. Recovery takes priority over normal navigation.
        if (recovering)
        {
            return Recover();
        }

        // 4. Detect obstacles.
        bool frontBlocked =
            front < FRONT_TRIGGER;

        bool leftBlocked =
            left < DIAGONAL_DANGER ||
            leftSide < SIDE_DANGER;

        bool rightBlocked =
            right < DIAGONAL_DANGER ||
            rightSide < SIDE_DANGER;

        // 5. Enter wall-following mode.
        if (!wallFollowing &&
            (frontBlocked || leftBlocked || rightBlocked))
        {
            wallFollowing = true;
            wallTicks = 0;

            // Select the direction with more available space.
            float leftSpace = GetLeftClearance();
            float rightSpace = GetRightClearance();

            if (leftSpace > rightSpace + 0.08f)
            {
                // Turn left, follow the right wall.
                followSide = 1;
            }
            else if (rightSpace > leftSpace + 0.08f)
            {
                // Turn right, follow the left wall.
                followSide = -1;
            }
            else
            {
                // Prefer the direction of the goal.
                followSide = goalAngle >= 0f ? -1 : 1;
            }
        }

        // 6. Execute the appropriate navigation mode.
        if (wallFollowing)
        {
            return FollowWall(
                goalAngle,
                front,
                left,
                right,
                leftSide,
                rightSide
            );
        }

        return NavigateToGoal(
            goalAngle,
            front,
            left,
            right
        );
    }
}
