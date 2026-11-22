
using System;
using UnityEngine;

public class LLM_ChatGPT_5_FB : IRoverController
{
    public string Name => "Rover Navigation V3";

    private enum State
    {
        Seek,
        TurnAroundObstacle,
        FollowWall,
        EscapeTurn
    }

    private State state;

    private const float DT = 0.05f;

    private const float SAFE_FRONT = 0.58f;
    private const float SAFE_DIAGONAL = 0.40f;
    private const float CRITICAL = 0.25f;
    private const float WALL_TARGET = 0.42f;

    private float front, left, right;
    private float leftSide, rightSide;

    private bool initialized;

    private int direction;
    private int turnDirection;

    private float stateTimer;
    private float clearTimer;
    private float stuckTimer;
    private float escapeTimer;

    private float bestDistance;
    private float previousDistance;

    private float previousTurn;
    private float filteredGoalAngle;

    private int frontClear;
    private int leftClear;
    private int rightClear;
    private int leftSideClear;
    private int rightSideClear;

    private int recoveryAttempts;

    public void ResetController()
    {
        state = State.Seek;

        front = left = right = 4f;
        leftSide = rightSide = 4f;

        initialized = false;

        direction = 1;
        turnDirection = 1;

        stateTimer = 0f;
        clearTimer = 0f;
        stuckTimer = 0f;
        escapeTimer = 0f;

        bestDistance = 999f;
        previousDistance = 999f;

        previousTurn = 0f;
        filteredGoalAngle = 0f;

        frontClear = 0;
        leftClear = 0;
        rightClear = 0;
        leftSideClear = 0;
        rightSideClear = 0;

        recoveryAttempts = 0;
    }

    private float Clean(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v))
            return 4f;

        return Mathf.Clamp(v, 0.02f, 4f);
    }

    private float Filter(
        float oldValue,
        float newValue,
        ref int clearCount)
    {
        newValue = Clean(newValue);

        // Reject isolated false maximum readings.
        if (newValue >= 3.95f && oldValue < 2.5f)
        {
            clearCount++;

            if (clearCount < 3)
                return oldValue;
        }
        else
        {
            clearCount = 0;
        }

        return Mathf.Lerp(oldValue, newValue, 0.35f);
    }

    private void UpdateSensors(Observation o)
    {
        if (!initialized)
        {
            front = Clean(o.front);
            left = Clean(o.left);
            right = Clean(o.right);
            leftSide = Clean(o.leftSide);
            rightSide = Clean(o.rightSide);

            filteredGoalAngle = o.goalAngleDeg;

            initialized = true;
            return;
        }

        front = Filter(front, o.front, ref frontClear);
        left = Filter(left, o.left, ref leftClear);
        right = Filter(right, o.right, ref rightClear);

        leftSide = Filter(
            leftSide, o.leftSide, ref leftSideClear);

        rightSide = Filter(
            rightSide, o.rightSide, ref rightSideClear);

        filteredGoalAngle = Mathf.LerpAngle(
            filteredGoalAngle,
            o.goalAngleDeg,
            0.35f
        );
    }

    private float Danger(float d)
    {
        return Mathf.Clamp01((0.65f - d) / 0.48f);
    }

    private float ClampTurn(float t)
    {
        return Mathf.Clamp(t, -1f, 1f);
    }

    // Choose a side only when starting a new obstacle encounter.
    private int ChooseDirection()
    {
        float leftSpace =
            0.55f * left + 0.45f * leftSide;

        float rightSpace =
            0.55f * right + 0.45f * rightSide;

        if (leftSpace > rightSpace + 0.10f)
            return 1;

        if (rightSpace > leftSpace + 0.10f)
            return -1;

        return leftSpace >= rightSpace ? 1 : -1;
    }

    private void BeginAvoidance(int dir)
    {
        direction = dir;

        // Turn away from the obstacle while establishing
        // a consistent wall-following side.
        turnDirection = -direction;

        state = State.TurnAroundObstacle;

        stateTimer = 0f;
        clearTimer = 0f;
    }

    private bool ObstacleAhead()
    {
        return front < SAFE_FRONT ||
               left < SAFE_DIAGONAL ||
               right < SAFE_DIAGONAL;
    }

    // Goal-seeking steering.
    private float SeekSteering(float angle)
    {
        float turn = Mathf.Clamp(angle / 65f, -1f, 1f);

        turn += 0.80f * Danger(left);
        turn -= 0.80f * Danger(right);

        turn -= 0.45f * Danger(leftSide);
        turn += 0.45f * Danger(rightSide);

        return ClampTurn(turn);
    }

    // Corrected wall-following controller.
    private float FollowSteering()
    {
        float turn;

        if (direction > 0)
        {
            // Keep obstacle on left.
            // Too far from wall -> turn left.
            // Too close -> turn right.

            turn = 1.7f * (leftSide - WALL_TARGET);

            // Front-left obstacle.
            turn -= 0.65f * Danger(left);

            // Obstacle directly ahead.
            turn -= 1.15f * Danger(front);

            // Maintain clearance on opposite side.
            turn += 0.30f * Danger(rightSide);
        }
        else
        {
            // Keep obstacle on right.
            // Too far from wall -> turn right.
            // Too close -> turn left.

            turn = -1.7f * (rightSide - WALL_TARGET);

            turn += 0.65f * Danger(right);

            turn += 1.15f * Danger(front);

            turn -= 0.30f * Danger(leftSide);
        }

        return ClampTurn(turn);
    }

    // A goal-facing direction must remain clear before
    // leaving obstacle-following mode.
    private bool GoalDirectionClear(float angle)
    {
        if (front < 0.85f)
            return false;

        if (Mathf.Abs(angle) < 25f)
            return front > 1.0f;

        if (angle > 0f)
            return left > 0.85f;

        return right > 0.85f;
    }

    private void UpdateProgress(float distance)
    {
        if (distance < bestDistance - 0.08f)
        {
            bestDistance = distance;
            stuckTimer = 0f;
            recoveryAttempts = 0;
        }
        else
        {
            stuckTimer += DT;
        }

        previousDistance = distance;
    }

    private void StartRecovery()
    {
        // Only switch direction after sustained lack of progress.
        direction = -direction;

        turnDirection = -direction;

        state = State.EscapeTurn;

        escapeTimer = 0f;
        stateTimer = 0f;
        stuckTimer = 0f;
        clearTimer = 0f;

        recoveryAttempts++;
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float distance = Mathf.Max(0f, obs.goalDistance);
        float angle = Mathf.Clamp(filteredGoalAngle, -180f, 180f);

        stateTimer += DT;

        UpdateProgress(distance);

        // Goal reached.
        if (distance < 0.17f)
            return (0f, 0f);

        // State 1: Seek the goal.
        if (state == State.Seek)
        {
            if (ObstacleAhead())
                BeginAvoidance(ChooseDirection());
        }

        // State 2: Initial controlled turn.
        if (state == State.TurnAroundObstacle)
        {
            if (stateTimer >= 0.85f)
            {
                state = State.FollowWall;
                stateTimer = 0f;
                clearTimer = 0f;
            }
        }

        // State 3: Persistent wall following.
        if (state == State.FollowWall)
        {
            if (GoalDirectionClear(angle))
                clearTimer += DT;
            else
                clearTimer = 0f;

            // Require sustained clearance.
            if (clearTimer >= 0.8f &&
                stateTimer >= 1.5f)
            {
                state = State.Seek;

                stateTimer = 0f;
                clearTimer = 0f;
                stuckTimer = 0f;

                bestDistance = distance;
            }
        }

        // State 4: Recovery turn.
        if (state == State.EscapeTurn)
        {
            escapeTimer += DT;

            if (escapeTimer >= 0.75f)
            {
                state = State.FollowWall;

                stateTimer = 0f;
                clearTimer = 0f;
            }
        }

        // Recovery only after sustained lack of progress.
        if (state != State.EscapeTurn &&
            state != State.TurnAroundObstacle &&
            stuckTimer >= 6f &&
            recoveryAttempts < 3)
        {
            StartRecovery();
        }

        float turn = 0f;
        float speed = 0.75f;

        switch (state)
        {
            case State.Seek:

                turn = SeekSteering(angle);
                speed = 0.75f;

                break;

            case State.TurnAroundObstacle:

                turn = turnDirection * 0.75f;
                speed = 0f;

                break;

            case State.FollowWall:

                turn = FollowSteering();
                speed = 0.38f;

                break;

            case State.EscapeTurn:

                turn = turnDirection * 0.85f;
                speed = 0f;

                break;
        }

        // Emergency collision avoidance.
        if (front < CRITICAL)
        {
            turn = left > right ? 1f : -1f;
            speed = 0.08f;
        }

        // Smooth ordinary steering, but do not smooth
        // fixed-direction turning states.
        if (state == State.Seek ||
            state == State.FollowWall)
        {
            turn = Mathf.Lerp(previousTurn, turn, 0.55f);
        }

        previousTurn = ClampTurn(turn);

        // Slow down near obstacles.
        float nearest = Mathf.Min(
            front, left, right, leftSide, rightSide
        );

        if (nearest < 0.8f)
        {
            float clearanceSpeed = Mathf.Lerp(
                0.12f,
                0.55f,
                Mathf.Clamp01((nearest - 0.20f) / 0.60f)
            );

            speed = Mathf.Min(speed, clearanceSpeed);
        }

        // Slow down on sharp turns.
        if (speed > 0f)
            speed *= 1f - 0.55f * Mathf.Abs(turn);

        // Never drive into an extremely close frontal obstacle.
        if (front < 0.16f)
            speed = 0f;

        return (
            Mathf.Clamp(speed, 0f, 1f),
            ClampTurn(turn)
        );
    }
}
