
using UnityEngine;

public class LLM_ChatGPT_4_FB : IRoverController
{
    public string Name => "Adaptive Rover Navigation V3";

    // Navigation states
    private const int GOAL_SEEKING = 0;
    private const int WALL_FOLLOWING = 1;
    private const int ESCAPE = 2;

    private int state;

    // Wall side: -1 = left wall, +1 = right wall
    private int wallSide;

    // Escape memory
    private int escapeDirection;
    private int lastEscapeDirection;
    private int escapeAttempts;
    private float escapeAngle;
    private int escapeTicks;
    private int escapeCooldown;

    // Filtered sensor readings
    private float fFront;
    private float fLeft;
    private float fRight;
    private float fLeftSide;
    private float fRightSide;

    // False-clear reading protection
    private int cFront;
    private int cLeft;
    private int cRight;
    private int cLeftSide;
    private int cRightSide;

    // Progress monitoring
    private float bestDistance;
    private int stuckTicks;
    private int wallTicks;
    private int clearTicks;
    private int totalTicks;

    // Steering memory
    private float previousTurn;

    // Constants
    private const float MAX_RANGE = 4.0f;

    private const float FRONT_STOP = 0.25f;
    private const float FRONT_DANGER = 0.42f;
    private const float FRONT_TRIGGER = 0.70f;

    private const float SIDE_DANGER = 0.24f;
    private const float SIDE_TARGET = 0.42f;

    private const int STUCK_LIMIT = 100;
    private const int ESCAPE_COOLDOWN = 70;

    private const float ESCAPE_MAX_ANGLE = 165f;

    private const int MIN_WALL_TICKS = 20;
    private const int CLEAR_REQUIRED = 5;

    // ------------------------------------------------
    // RESET
    // ------------------------------------------------

    public void ResetController()
    {
        state = GOAL_SEEKING;

        wallSide = 1;

        escapeDirection = 1;
        lastEscapeDirection = 0;
        escapeAttempts = 0;
        escapeAngle = 0f;
        escapeTicks = 0;
        escapeCooldown = 0;

        fFront = MAX_RANGE;
        fLeft = MAX_RANGE;
        fRight = MAX_RANGE;
        fLeftSide = MAX_RANGE;
        fRightSide = MAX_RANGE;

        cFront = 0;
        cLeft = 0;
        cRight = 0;
        cLeftSide = 0;
        cRightSide = 0;

        bestDistance = float.MaxValue;
        stuckTicks = 0;
        wallTicks = 0;
        clearTicks = 0;
        totalTicks = 0;

        previousTurn = 0f;
    }

    // ------------------------------------------------
    // SENSOR FILTERING
    // ------------------------------------------------

    private float ReadSensor(
        float raw,
        ref float previous,
        ref int clearCount)
    {
        raw = Mathf.Clamp(raw, 0.02f, MAX_RANGE);

        // An occasional 4m reading may be false.
        if (raw >= 3.95f)
        {
            clearCount++;

            if (clearCount < 3)
                return previous;

            // Gradually release old obstacle readings.
            previous = Mathf.Min(
                MAX_RANGE,
                previous + 0.35f
            );

            return previous;
        }

        clearCount = 0;

        // React quickly to approaching obstacles.
        if (raw < previous)
        {
            previous =
                previous * 0.30f + raw * 0.70f;
        }
        else
        {
            // Gradually accept increasing distance.
            previous =
                previous * 0.72f + raw * 0.28f;
        }

        return previous;
    }

    // ------------------------------------------------
    // UTILITY FUNCTIONS
    // ------------------------------------------------

    private float ClampTurn(float value)
    {
        return Mathf.Clamp(value, -1f, 1f);
    }

    private float ClampSpeed(float value)
    {
        return Mathf.Clamp(value, 0f, 1f);
    }

    private float GetLeftSpace()
    {
        return Mathf.Min(fLeft, fLeftSide);
    }

    private float GetRightSpace()
    {
        return Mathf.Min(fRight, fRightSide);
    }

    private int BestOpenDirection()
    {
        float leftSpace = GetLeftSpace();
        float rightSpace = GetRightSpace();

        if (leftSpace > rightSpace + 0.12f)
            return 1;

        if (rightSpace > leftSpace + 0.12f)
            return -1;

        // If the previous escape direction failed,
        // try the opposite direction.
        if (lastEscapeDirection != 0)
            return -lastEscapeDirection;

        return escapeAttempts % 2 == 0 ? 1 : -1;
    }

    // Limit abrupt steering changes during normal driving.
    private float SmoothTurn(float requested)
    {
        requested = ClampTurn(requested);

        float difference = requested - previousTurn;

        difference = Mathf.Clamp(
            difference,
            -0.18f,
            0.18f
        );

        previousTurn = ClampTurn(
            previousTurn + difference
        );

        return previousTurn;
    }

    // ------------------------------------------------
    // PROGRESS MONITOR
    // ------------------------------------------------

    private bool IsStuck(float distance)
    {
        if (distance < bestDistance - 0.12f)
        {
            bestDistance = distance;
            stuckTicks = 0;

            return false;
        }

        stuckTicks++;

        if (stuckTicks >= STUCK_LIMIT)
        {
            if (escapeCooldown <= 0)
                return true;
        }

        return false;
    }

    // ------------------------------------------------
    // ENTER ESCAPE STATE
    // ------------------------------------------------

    private void StartEscape()
    {
        state = ESCAPE;

        escapeDirection = BestOpenDirection();

        // Do not immediately repeat a failed direction.
        if (lastEscapeDirection != 0 &&
            escapeAttempts > 0)
        {
            escapeDirection = -lastEscapeDirection;
        }

        lastEscapeDirection = escapeDirection;

        escapeAttempts++;

        escapeAngle = 0f;
        escapeTicks = 0;

        escapeCooldown = ESCAPE_COOLDOWN;

        stuckTicks = 0;
        wallTicks = 0;
        clearTicks = 0;

        previousTurn = 0f;
    }

    // ------------------------------------------------
    // ESCAPE MANOEUVRE
    // ------------------------------------------------

    private (float speed01, float turn) Escape()
    {
        escapeTicks++;

        // Turn in one fixed direction.
        // Do not alternate steering every frame.
        float turn = escapeDirection;

        // Accumulate commanded rotation.
        escapeAngle += 4.5f;

        bool frontClear = fFront > 0.95f;

        bool goalApproaching =
            Mathf.Abs(0f) < 0.001f;

        // Once sufficient rotation has occurred,
        // inspect the new direction.
        bool escapeFound =
            escapeAngle >= 75f &&
            frontClear &&
            Mathf.Min(fLeft, fRight) > 0.45f;

        bool rotationComplete =
            escapeAngle >= ESCAPE_MAX_ANGLE;

        if (rotationComplete ||
            (escapeFound && escapeTicks >= 18))
        {
            state = WALL_FOLLOWING;

            // Turned left: follow right boundary.
            // Turned right: follow left boundary.
            wallSide = escapeDirection;

            wallTicks = 0;
            clearTicks = 0;
            stuckTicks = 0;

            previousTurn = 0f;

            return (0f, 0f);
        }

        return (0f, turn);
    }

    // ------------------------------------------------
    // GOAL SEEKING
    // ------------------------------------------------

    private (float speed01, float turn) SeekGoal(
        float goalAngle)
    {
        float absAngle = Mathf.Abs(goalAngle);

        // Smooth proportional goal steering.
        float turn = goalAngle * 0.012f;

        // Diagonal obstacle repulsion.
        float leftRepulsion =
            Mathf.Clamp01((0.90f - fLeft) / 0.90f);

        float rightRepulsion =
            Mathf.Clamp01((0.90f - fRight) / 0.90f);

        turn +=
            (rightRepulsion - leftRepulsion) * 0.45f;

        float speed = 0.85f;

        // Slow down for large heading errors.
        speed *=
            1f - Mathf.Clamp01(absAngle / 100f) * 0.65f;

        // Front obstacle handling.
        if (fFront < FRONT_DANGER)
        {
            speed = 0f;

            // Turn towards the more open direction.
            turn = GetLeftSpace() > GetRightSpace()
                ? 0.85f : -0.85f;
        }
        else if (fFront < FRONT_TRIGGER)
        {
            speed = Mathf.Min(speed, 0.18f);
        }

        // Side collision protection.
        if (fLeftSide < SIDE_DANGER)
        {
            turn -= 0.35f;
            speed = Mathf.Min(speed, 0.15f);
        }

        if (fRightSide < SIDE_DANGER)
        {
            turn += 0.35f;
            speed = Mathf.Min(speed, 0.15f);
        }

        // Stop when an obstacle is extremely close.
        if (fFront < FRONT_STOP)
            speed = 0f;

        speed *=
            1f - Mathf.Abs(ClampTurn(turn)) * 0.40f;

        return (
            ClampSpeed(speed),
            SmoothTurn(turn)
        );
    }

    // ------------------------------------------------
    // WALL FOLLOWING
    // ------------------------------------------------

    private (float speed01, float turn) FollowWall(
        float goalAngle)
    {
        wallTicks++;

        float sideDistance =
            wallSide == -1 ? fLeftSide : fRightSide;

        float diagonalDistance =
            wallSide == -1 ? fLeft : fRight;

        // Maintain a stable distance from the wall.
        float error = sideDistance - SIDE_TARGET;

        // Left wall: steer left if too far away.
        // Right wall: steer right if too far away.
        float wallTurn =
            -wallSide * error * 1.65f;

        float turn = wallTurn * 0.75f;

        // Mild goal influence.
        turn += Mathf.Clamp(
            goalAngle / 180f,
            -1f,
            1f
        ) * 0.15f;

        // Detect an obstacle ahead on the followed side.
        float diagonalRepulsion =
            Mathf.Clamp01(
                (0.75f - diagonalDistance) / 0.75f
            );

        turn += wallSide * diagonalRepulsion * 0.45f;

        float speed = 0.55f;

        // Frontal obstacle handling.
        if (fFront < FRONT_DANGER)
        {
            speed = 0f;

            // Turn away from the followed wall.
            turn = wallSide * 0.85f;
        }
        else if (fFront < FRONT_TRIGGER)
        {
            speed = 0.14f;
            turn += wallSide * 0.20f;
        }
        else if (fFront < 1.0f)
        {
            speed = 0.30f;
        }

        // Protect both sides of the rover.
        if (fLeftSide < SIDE_DANGER)
        {
            turn -= 0.35f;
            speed = Mathf.Min(speed, 0.12f);
        }

        if (fRightSide < SIDE_DANGER)
        {
            turn += 0.35f;
            speed = Mathf.Min(speed, 0.12f);
        }

        if (fFront < FRONT_STOP)
            speed = 0f;

        speed *=
            1f - Mathf.Abs(ClampTurn(turn)) * 0.35f;

        // ------------------------------------------------
        // WALL FOLLOWING EXIT
        // ------------------------------------------------

        bool goalAligned =
            Mathf.Abs(goalAngle) < 28f;

        bool frontClear =
            fFront > 0.95f;

        bool pathClear =
            frontClear &&
            Mathf.Min(fLeft, fRight) > 0.80f;

        bool wallDisappeared =
            sideDistance > 1.15f &&
            diagonalDistance > 0.95f;

        if (goalAligned && pathClear)
            clearTicks++;
        else
            clearTicks = 0;

        // Require several consecutive clear observations.
        if (wallTicks >= MIN_WALL_TICKS &&
            (clearTicks >= CLEAR_REQUIRED ||
             (wallDisappeared && goalAligned)))
        {
            state = GOAL_SEEKING;

            wallTicks = 0;
            clearTicks = 0;
            stuckTicks = 0;

            previousTurn = 0f;

            return SeekGoal(goalAngle);
        }

        return (
            ClampSpeed(speed),
            SmoothTurn(turn)
        );
    }

    // ------------------------------------------------
    // MAIN CONTROLLER
    // ------------------------------------------------

    public (float speed01, float turn) Decide(Observation obs)
    {
        totalTicks++;

        if (escapeCooldown > 0)
            escapeCooldown--;

        // 1. Read all sensors.
        fFront = ReadSensor(
            obs.front,
            ref fFront,
            ref cFront
        );

        fLeft = ReadSensor(
            obs.left,
            ref fLeft,
            ref cLeft
        );

        fRight = ReadSensor(
            obs.right,
            ref fRight,
            ref cRight
        );

        fLeftSide = ReadSensor(
            obs.leftSide,
            ref fLeftSide,
            ref cLeftSide
        );

        fRightSide = ReadSensor(
            obs.rightSide,
            ref fRightSide,
            ref cRightSide
        );

        float goalDistance =
            Mathf.Max(0f, obs.goalDistance);

        float goalAngle =
            Mathf.Clamp(
                obs.goalAngleDeg,
                -180f,
                180f
            );

        // 2. Stop once inside the goal area.
        if (goalDistance < 0.18f)
            return (0f, 0f);

        // 3. Progress monitoring.
        if (state != ESCAPE &&
            IsStuck(goalDistance))
        {
            StartEscape();
        }

        // 4. Escape state.
        if (state == ESCAPE)
        {
            return Escape();
        }

        // 5. Detect obstacles.
        bool frontBlocked =
            fFront < FRONT_TRIGGER;

        bool leftBlocked =
            fLeft < 0.52f ||
            fLeftSide < 0.32f;

        bool rightBlocked =
            fRight < 0.52f ||
            fRightSide < 0.32f;

        // 6. Enter wall-following only once.
        if (state == GOAL_SEEKING &&
            (frontBlocked || leftBlocked || rightBlocked))
        {
            state = WALL_FOLLOWING;

            wallTicks = 0;
            clearTicks = 0;

            // Choose the wall opposite the open direction.
            if (GetLeftSpace() > GetRightSpace() + 0.10f)
                wallSide = 1;
            else if (GetRightSpace() > GetLeftSpace() + 0.10f)
                wallSide = -1;
            else
                wallSide = goalAngle >= 0f ? -1 : 1;

            previousTurn = 0f;
        }

        // 7. Execute current navigation state.
        if (state == WALL_FOLLOWING)
        {
            return FollowWall(goalAngle);
        }

        return SeekGoal(goalAngle);
    }
}
