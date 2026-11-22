
using UnityEngine;

public class LLM_ChatGPT_4_FB : IRoverController
{
    public string Name => "Adaptive Bug Navigation V4";

    // Navigation states
    private const int SEEK = 0;
    private const int FOLLOW = 1;
    private const int ESCAPE_TURN = 2;
    private const int ESCAPE_MOVE = 3;

    private int state;

    // Boundary memory
    private int wallSide;
    private float obstacleStartDistance;
    private int wallTicks;
    private int clearTicks;

    // Escape memory
    private int escapeDirection;
    private int lastEscapeDirection;
    private int escapeAttempts;
    private int escapeTicks;
    private int escapeCooldown;

    // Sensor filters
    private float front;
    private float left;
    private float right;
    private float leftSide;
    private float rightSide;

    private int frontClear;
    private int leftClear;
    private int rightClear;
    private int leftSideClear;
    private int rightSideClear;

    // Progress tracking
    private float bestDistance;
    private int stuckTicks;
    private int totalTicks;

    // Steering memory
    private float previousTurn;

    // Constants
    private const float MAX_RANGE = 4f;

    private const float FRONT_STOP = 0.23f;
    private const float FRONT_DANGER = 0.40f;
    private const float FRONT_TRIGGER = 0.68f;

    private const float SIDE_DANGER = 0.25f;
    private const float WALL_TARGET = 0.43f;

    private const int STUCK_LIMIT = 100;
    private const int ESCAPE_COOLDOWN = 45;

    private const int TURN_DURATION = 30;
    private const int MOVE_DURATION = 40;

    private const int MIN_WALL_TIME = 15;
    private const int CLEAR_REQUIRED = 8;

    // -------------------------------------------------
    // RESET
    // -------------------------------------------------

    public void ResetController()
    {
        state = SEEK;

        wallSide = 1;
        obstacleStartDistance = MAX_RANGE;
        wallTicks = 0;
        clearTicks = 0;

        escapeDirection = 1;
        lastEscapeDirection = 0;
        escapeAttempts = 0;
        escapeTicks = 0;
        escapeCooldown = 0;

        front = MAX_RANGE;
        left = MAX_RANGE;
        right = MAX_RANGE;
        leftSide = MAX_RANGE;
        rightSide = MAX_RANGE;

        frontClear = 0;
        leftClear = 0;
        rightClear = 0;
        leftSideClear = 0;
        rightSideClear = 0;

        bestDistance = float.MaxValue;
        stuckTicks = 0;
        totalTicks = 0;

        previousTurn = 0f;
    }

    // -------------------------------------------------
    // SENSOR FILTERING
    // -------------------------------------------------

    private float ReadSensor(
        float raw,
        ref float previous,
        ref int clearCount)
    {
        raw = Mathf.Clamp(raw, 0.02f, MAX_RANGE);

        // Protect against occasional false 4m readings.
        if (raw >= 3.95f)
        {
            clearCount++;

            if (clearCount < 3)
                return previous;

            // Gradually release old obstacle readings.
            previous = Mathf.Min(
                MAX_RANGE,
                previous + 0.30f
            );

            return previous;
        }

        clearCount = 0;

        // A newly detected obstacle must be accepted quickly.
        if (previous >= 3.95f)
        {
            previous = raw;
            return previous;
        }

        // Fast response to decreasing distance.
        if (raw < previous)
        {
            previous =
                previous * 0.25f + raw * 0.75f;
        }
        else
        {
            // Slower response to increasing distance.
            previous =
                previous * 0.70f + raw * 0.30f;
        }

        return previous;
    }

    // -------------------------------------------------
    // UTILITY
    // -------------------------------------------------

    private float ClampTurn(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }

    private float ClampSpeed(float value)
    {
        return Mathf.Clamp(value, 0f, 1f);
    }

    private float LeftSpace()
    {
        return Mathf.Min(left, leftSide);
    }

    private float RightSpace()
    {
        return Mathf.Min(right, rightSide);
    }

    private int BestOpenDirection()
    {
        float l = LeftSpace();
        float r = RightSpace();

        if (l > r + 0.12f)
            return 1;

        if (r > l + 0.12f)
            return -1;

        if (lastEscapeDirection != 0)
            return -lastEscapeDirection;

        return escapeAttempts % 2 == 0 ? 1 : -1;
    }

    // Limit sudden steering changes.
    private float SmoothTurn(float requested)
    {
        requested = ClampTurn(requested);

        float difference = requested - previousTurn;

        difference = Mathf.Clamp(
            difference,
            -0.14f,
            0.14f
        );

        previousTurn = ClampTurn(
            previousTurn + difference
        );

        return previousTurn;
    }

    // -------------------------------------------------
    // PROGRESS DETECTION
    // -------------------------------------------------

    private bool CheckProgress(float distance)
    {
        if (distance < bestDistance - 0.10f)
        {
            bestDistance = distance;
            stuckTicks = 0;
            return false;
        }

        stuckTicks++;

        return stuckTicks >= STUCK_LIMIT &&
               escapeCooldown <= 0;
    }

    // -------------------------------------------------
    // START ESCAPE
    // -------------------------------------------------

    private void StartEscape()
    {
        state = ESCAPE_TURN;

        escapeDirection = BestOpenDirection();

        // Avoid repeating the last failed direction
        // unless the alternative is substantially blocked.
        if (lastEscapeDirection != 0)
        {
            int alternative = -lastEscapeDirection;

            float alternativeSpace =
                alternative == 1
                ? LeftSpace()
                : RightSpace();

            float currentSpace =
                escapeDirection == 1
                ? LeftSpace()
                : RightSpace();

            if (alternativeSpace > 0.35f)
                escapeDirection = alternative;
            else if (currentSpace < 0.25f)
                escapeDirection = alternative;
        }

        lastEscapeDirection = escapeDirection;

        escapeAttempts++;
        escapeTicks = 0;

        escapeCooldown = ESCAPE_COOLDOWN;

        stuckTicks = 0;
        wallTicks = 0;
        clearTicks = 0;

        previousTurn = 0f;
    }

    // -------------------------------------------------
    // ESCAPE PHASE 1: TURN
    // -------------------------------------------------

    private (float speed01, float turn) EscapeTurn()
    {
        escapeTicks++;

        // Turn consistently towards the selected opening.
        float turn = escapeDirection * 1f;

        if (escapeTicks >= TURN_DURATION)
        {
            state = ESCAPE_MOVE;
            escapeTicks = 0;
            previousTurn = 0f;

            return (0f, 0f);
        }

        return (0f, turn);
    }

    // -------------------------------------------------
    // ESCAPE PHASE 2: MOVE
    // -------------------------------------------------

    private (float speed01, float turn) EscapeMove(
        float goalAngle)
    {
        escapeTicks++;

        // If the new direction is blocked, do not advance.
        if (front < 0.48f)
        {
            state = FOLLOW;

            wallSide = escapeDirection;

            wallTicks = 0;
            clearTicks = 0;
            stuckTicks = 0;

            bestDistance = MAX_RANGE;
            previousTurn = 0f;

            return (0f, 0f);
        }

        // Small heading adjustment while moving.
        float turn =
            Mathf.Clamp(goalAngle / 180f, -0.30f, 0.30f);

        float speed = 0.35f;

        if (front < 0.80f)
            speed = 0.15f;

        if (leftSide < SIDE_DANGER)
            turn += 0.25f;

        if (rightSide < SIDE_DANGER)
            turn -= 0.25f;

        if (escapeTicks >= MOVE_DURATION)
        {
            state = FOLLOW;

            // Maintain the selected boundary.
            wallSide = escapeDirection;

            wallTicks = 0;
            clearTicks = 0;
            stuckTicks = 0;

            bestDistance = MAX_RANGE;
            previousTurn = 0f;

            return (0f, 0f);
        }

        return (
            ClampSpeed(speed),
            ClampTurn(turn)
        );
    }

    // -------------------------------------------------
    // GOAL SEEKING
    // -------------------------------------------------

    private (float speed01, float turn) SeekGoal(
        float goalAngle)
    {
        float absAngle = Mathf.Abs(goalAngle);

        // Proportional steering towards goal.
        float turn = goalAngle * 0.015f;

        // Diagonal obstacle avoidance.
        float leftRepulsion =
            Mathf.Clamp01((0.90f - left) / 0.90f);

        float rightRepulsion =
            Mathf.Clamp01((0.90f - right) / 0.90f);

        turn +=
            (rightRepulsion - leftRepulsion) * 0.45f;

        float speed = 0.85f;

        // Reduce speed for large heading errors.
        speed *=
            1f - Mathf.Clamp01(absAngle / 100f) * 0.65f;

        // Front obstacle protection.
        if (front < FRONT_DANGER)
        {
            speed = 0f;

            turn = LeftSpace() > RightSpace()
                ? 0.85f
                : -0.85f;
        }
        else if (front < FRONT_TRIGGER)
        {
            speed = Mathf.Min(speed, 0.18f);
        }

        // Side protection.
        if (leftSide < SIDE_DANGER)
        {
            turn -= 0.35f;
            speed = Mathf.Min(speed, 0.15f);
        }

        if (rightSide < SIDE_DANGER)
        {
            turn += 0.35f;
            speed = Mathf.Min(speed, 0.15f);
        }

        if (front < FRONT_STOP)
            speed = 0f;

        speed *=
            1f - Mathf.Abs(ClampTurn(turn)) * 0.35f;

        return (
            ClampSpeed(speed),
            SmoothTurn(turn)
        );
    }

    // -------------------------------------------------
    // BOUNDARY FOLLOWING
    // -------------------------------------------------

    private (float speed01, float turn) FollowBoundary(
        float goalAngle,
        float goalDistance)
    {
        wallTicks++;

        float sideDistance =
            wallSide == -1 ? leftSide : rightSide;

        float diagonalDistance =
            wallSide == -1 ? left : right;

        // Maintain a constant boundary distance.
        float error = sideDistance - WALL_TARGET;

        // Left wall: positive error -> turn left.
        // Right wall: positive error -> turn right.
        float turn =
            -wallSide * error * 1.50f;

        // Modest goal influence.
        turn +=
            Mathf.Clamp(goalAngle / 180f, -1f, 1f)
            * 0.12f;

        // Diagonal obstacle avoidance.
        float diagonalRepulsion =
            Mathf.Clamp01(
                (0.70f - diagonalDistance) / 0.70f
            );

        turn +=
            wallSide * diagonalRepulsion * 0.45f;

        float speed = 0.48f;

        // Frontal obstacle handling.
        if (front < FRONT_DANGER)
        {
            speed = 0f;

            turn = wallSide * 0.85f;
        }
        else if (front < FRONT_TRIGGER)
        {
            speed = 0.12f;
            turn += wallSide * 0.18f;
        }
        else if (front < 0.95f)
        {
            speed = 0.25f;
        }

        // Protect the sides.
        if (leftSide < SIDE_DANGER)
        {
            turn -= 0.40f;
            speed = Mathf.Min(speed, 0.12f);
        }

        if (rightSide < SIDE_DANGER)
        {
            turn += 0.40f;
            speed = Mathf.Min(speed, 0.12f);
        }

        if (front < FRONT_STOP)
            speed = 0f;

        speed *=
            1f - Mathf.Abs(ClampTurn(turn)) * 0.30f;

        // -------------------------------------------------
        // BUG-STYLE LEAVE CONDITION
        // -------------------------------------------------

        bool goalAligned =
            Mathf.Abs(goalAngle) < 25f;

        bool frontClear =
            front > 0.95f;

        bool goalProgress =
            goalDistance <
            obstacleStartDistance - 0.12f;

        // The path towards the goal must be clear
        // over multiple consecutive observations.
        if (goalAligned && frontClear && goalProgress)
            clearTicks++;
        else
            clearTicks = 0;

        // Also detect when the boundary ends naturally.
        bool boundaryEnded =
            sideDistance > 1.20f &&
            diagonalDistance > 1.00f &&
            front > 1.00f &&
            goalAligned;

        if (wallTicks >= MIN_WALL_TIME &&
            (clearTicks >= CLEAR_REQUIRED ||
             boundaryEnded))
        {
            state = SEEK;

            wallTicks = 0;
            clearTicks = 0;
            stuckTicks = 0;

            bestDistance = goalDistance;

            previousTurn = 0f;

            return SeekGoal(goalAngle);
        }

        return (
            ClampSpeed(speed),
            SmoothTurn(turn)
        );
    }

    // -------------------------------------------------
    // MAIN DECISION
    // -------------------------------------------------

    public (float speed01, float turn) Decide(Observation obs)
    {
        totalTicks++;

        if (escapeCooldown > 0)
            escapeCooldown--;

        // 1. Read sensors.
        front = ReadSensor(
            obs.front,
            ref front,
            ref frontClear
        );

        left = ReadSensor(
            obs.left,
            ref left,
            ref leftClear
        );

        right = ReadSensor(
            obs.right,
            ref right,
            ref rightClear
        );

        leftSide = ReadSensor(
            obs.leftSide,
            ref leftSide,
            ref leftSideClear
        );

        rightSide = ReadSensor(
            obs.rightSide,
            ref rightSide,
            ref rightSideClear
        );

        float goalDistance =
            Mathf.Max(0f, obs.goalDistance);

        float goalAngle =
            Mathf.Clamp(
                obs.goalAngleDeg,
                -180f,
                180f
            );

        // 2. Goal reached.
        if (goalDistance < 0.15f)
            return (0f, 0f);

        // 3. Monitor progress.
        if (state != ESCAPE_TURN &&
            state != ESCAPE_MOVE &&
            CheckProgress(goalDistance))
        {
            StartEscape();
        }

        // 4. Escape manoeuvre.
        if (state == ESCAPE_TURN)
            return EscapeTurn();

        if (state == ESCAPE_MOVE)
            return EscapeMove(goalAngle);

        // 5. Detect nearby obstacles.
        bool frontBlocked =
            front < FRONT_TRIGGER;

        bool leftBlocked =
            left < 0.55f ||
            leftSide < 0.32f;

        bool rightBlocked =
            right < 0.55f ||
            rightSide < 0.32f;

        // 6. Enter boundary following.
        if (state == SEEK &&
            (frontBlocked || leftBlocked || rightBlocked))
        {
            state = FOLLOW;

            wallTicks = 0;
            clearTicks = 0;

            obstacleStartDistance = goalDistance;

            // Select the boundary opposite the open side.
            if (LeftSpace() > RightSpace() + 0.10f)
            {
                wallSide = 1;
            }
            else if (RightSpace() > LeftSpace() + 0.10f)
            {
                wallSide = -1;
            }
            else
            {
                wallSide = goalAngle >= 0f ? -1 : 1;
            }

            previousTurn = 0f;
        }

        // 7. Run current navigation state.
        if (state == FOLLOW)
        {
            return FollowBoundary(
                goalAngle,
                goalDistance
            );
        }

        return SeekGoal(goalAngle);
    }
}
