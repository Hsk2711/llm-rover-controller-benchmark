using System;
using UnityEngine;

public class LLM_ChatGPT_3_FB : IRoverController
{
    public string Name => "Persistent Adaptive Navigation V3";

    private const float MAX_RANGE = 4.0f;

    private const float FRONT_TRIGGER = 0.90f;
    private const float FRONT_CRITICAL = 0.30f;
    private const float SIDE_TARGET = 0.48f;

    private float frontF;
    private float leftF;
    private float rightF;
    private float leftSideF;
    private float rightSideF;

    private bool initialized;

    // Steering memory
    private float lastTurn;
    private int lastBypassDirection;

    // Obstacle navigation state
    private bool avoiding;
    private int bypassDirection;
    private int avoidTimer;
    private int clearTimer;

    // Progress monitoring
    private float progressDistance;
    private int progressTimer;
    private int stuckWindows;
    private int switchCooldown;

    // Sensor filtering
    private float Filter(float previous, float current)
    {
        if (float.IsNaN(current) || float.IsInfinity(current))
            return previous;

        current = Mathf.Clamp(current, 0.02f, MAX_RANGE);

        if (current < previous)
            return Mathf.Lerp(previous, current, 0.75f);

        return Mathf.Min(current, previous + 0.20f);
    }

    private float Clean(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return MAX_RANGE;

        return Mathf.Clamp(value, 0.02f, MAX_RANGE);
    }

    public void ResetController()
    {
        frontF = MAX_RANGE;
        leftF = MAX_RANGE;
        rightF = MAX_RANGE;
        leftSideF = MAX_RANGE;
        rightSideF = MAX_RANGE;

        initialized = false;

        lastTurn = 0f;
        lastBypassDirection = 1;

        avoiding = false;
        bypassDirection = 1;

        avoidTimer = 0;
        clearTimer = 0;

        progressDistance = float.MaxValue;
        progressTimer = 0;
        stuckWindows = 0;
        switchCooldown = 0;
    }

    private void UpdateSensors(Observation obs)
    {
        float f = Clean(obs.front);
        float l = Clean(obs.left);
        float r = Clean(obs.right);
        float ls = Clean(obs.leftSide);
        float rs = Clean(obs.rightSide);

        if (!initialized)
        {
            frontF = f;
            leftF = l;
            rightF = r;
            leftSideF = ls;
            rightSideF = rs;

            initialized = true;
            return;
        }

        frontF = Filter(frontF, f);
        leftF = Filter(leftF, l);
        rightF = Filter(rightF, r);
        leftSideF = Filter(leftSideF, ls);
        rightSideF = Filter(rightSideF, rs);
    }

    private float GoalSteering(float angle)
    {
        return Mathf.Clamp(angle / 75f, -1f, 1f);
    }

    private float LeftScore()
    {
        return leftF * 0.65f + leftSideF * 0.35f;
    }

    private float RightScore()
    {
        return rightF * 0.65f + rightSideF * 0.35f;
    }

    // Select an obstacle-bypass direction.
    // Once selected, it remains locked until the obstacle is cleared
    // or progress monitoring detects a persistent deadlock.
    private int ChooseDirection(float goalAngle)
    {
        float left = LeftScore();
        float right = RightScore();

        if (left > right + 0.12f)
            return 1;

        if (right > left + 0.12f)
            return -1;

        if (Mathf.Abs(goalAngle) > 15f)
            return goalAngle > 0f ? 1 : -1;

        return lastBypassDirection;
    }

    private bool FrontBlocked()
    {
        return frontF < FRONT_TRIGGER;
    }

    private bool GoalPathBlocked(float goalAngle)
    {
        if (Mathf.Abs(goalAngle) < 40f)
            return frontF < 0.95f;

        if (goalAngle > 0f)
            return leftF < 0.65f;

        return rightF < 0.65f;
    }

    private void StartAvoidance(float goalAngle)
    {
        avoiding = true;

        bypassDirection = ChooseDirection(goalAngle);

        lastBypassDirection = bypassDirection;

        avoidTimer = 0;
        clearTimer = 0;
    }

    private void UpdateAvoidance(float goalAngle)
    {
        if (!avoiding)
            return;

        avoidTimer++;

        // A minimum commitment prevents rapid left-right switching.
        if (avoidTimer < 20)
        {
            clearTimer = 0;
            return;
        }

        bool clear =
            frontF > 1.25f &&
            leftF > 0.90f &&
            rightF > 0.90f &&
            Mathf.Abs(goalAngle) < 38f;

        if (clear)
            clearTimer++;
        else
            clearTimer = 0;

        // Require sustained clearance before returning to normal mode.
        if (clearTimer >= 14)
        {
            avoiding = false;
            clearTimer = 0;
            avoidTimer = 0;
            stuckWindows = 0;
        }
    }

    // Track goal-distance improvement.
    private void UpdateProgress(float distance, float goalAngle)
    {
        progressTimer++;

        if (progressDistance == float.MaxValue)
            progressDistance = distance;

        if (switchCooldown > 0)
            switchCooldown--;

        // Evaluate progress every 1.5 seconds.
        if (progressTimer < 30)
            return;

        progressTimer = 0;

        if (distance < progressDistance - 0.07f)
        {
            progressDistance = distance;
            stuckWindows = 0;
            return;
        }

        stuckWindows++;

        if (stuckWindows >= 2 && switchCooldown == 0)
        {
            if (!avoiding)
            {
                StartAvoidance(goalAngle);
            }
            else
            {
                // Change direction only after sustained lack of progress.
                int alternative = -bypassDirection;

                float currentScore =
                    alternative > 0 ? LeftScore() : RightScore();

                float oldScore =
                    bypassDirection > 0 ? LeftScore() : RightScore();

                // Switch if the alternative is sufficiently clearer,
                // or after repeated failed progress windows.
                if (currentScore > oldScore + 0.10f ||
                    stuckWindows >= 3)
                {
                    bypassDirection = alternative;

                    lastBypassDirection = alternative;

                    avoidTimer = 0;
                    clearTimer = 0;
                }
            }

            stuckWindows = 0;
            switchCooldown = 50;
        }

        progressDistance = distance;
    }

    // Normal reactive obstacle avoidance.
    private float CalculateReactiveTurn()
    {
        float turn = 0f;

        // Diagonal obstacles.
        if (leftF < 0.85f)
        {
            turn -= Mathf.Clamp(
                (0.85f - leftF) * 1.5f,
                0f,
                0.75f
            );
        }

        if (rightF < 0.85f)
        {
            turn += Mathf.Clamp(
                (0.85f - rightF) * 1.5f,
                0f,
                0.75f
            );
        }

        // Side clearance.
        if (leftSideF < 0.45f)
            turn -= 0.35f;

        if (rightSideF < 0.45f)
            turn += 0.35f;

        // Strong frontal avoidance.
        if (frontF < FRONT_TRIGGER)
        {
            turn = ChooseDirection(0f) *
                   Mathf.Clamp(
                       (FRONT_TRIGGER - frontF) * 1.8f,
                       0.45f,
                       1f
                   );
        }

        return Mathf.Clamp(turn, -1f, 1f);
    }

    // Persistent wall-following / obstacle bypass.
    private float CalculateBypassTurn(float goalAngle)
    {
        int direction = bypassDirection;

        // Obstacle should generally remain on the opposite side
        // of the selected bypass direction.
        float obstacleSideDistance =
            direction > 0 ? rightSideF : leftSideF;

        float turn;

        if (frontF < 0.65f)
        {
            // Keep turning toward the selected open side.
            turn = direction * 0.90f;
        }
        else if (obstacleSideDistance < 1.10f)
        {
            // Maintain a relatively stable distance from the obstacle.
            float correction = Mathf.Clamp(
                (SIDE_TARGET - obstacleSideDistance) * 1.6f,
                -0.55f,
                0.55f
            );

            turn = direction * correction;

            // Gradually incorporate the goal direction.
            turn += GoalSteering(goalAngle) * 0.15f;
        }
        else
        {
            // Obstacle is not yet alongside the rover.
            // Continue around it without abruptly reversing direction.
            turn = direction * 0.30f +
                   GoalSteering(goalAngle) * 0.20f;
        }

        // Do not steer toward a very close obstacle.
        if (direction > 0 && leftF < 0.35f)
            turn = -0.35f;

        if (direction < 0 && rightF < 0.35f)
            turn = 0.35f;

        return Mathf.Clamp(turn, -1f, 1f);
    }

    private float CalculateSpeed(float turn)
    {
        float speed = 0.65f;

        // Reduce speed near frontal obstacles.
        if (frontF < 1.0f)
        {
            speed = Mathf.Min(
                speed,
                Mathf.Lerp(
                    0.45f,
                    0.18f,
                    Mathf.Clamp01((1.0f - frontF) / 0.70f)
                )
            );
        }

        // Turning speed management.
        float absTurn = Mathf.Abs(turn);

        if (absTurn > 0.35f)
            speed = Mathf.Min(speed, 0.38f);

        if (absTurn > 0.65f)
            speed = Mathf.Min(speed, 0.24f);

        if (absTurn > 0.85f)
            speed = Mathf.Min(speed, 0.16f);

        // Critical frontal clearance.
        if (frontF < 0.30f)
            speed = 0f;

        // Avoid moving forward if a side is extremely close.
        if (leftSideF < 0.19f ||
            rightSideF < 0.19f)
        {
            speed = 0f;
        }

        // Avoid driving into a close obstacle.
        if (frontF < 0.22f)
            speed = 0f;

        return Mathf.Clamp(speed, 0f, 1f);
    }

    public (float speed01, float turn) Decide(Observation obs)
    {
        UpdateSensors(obs);

        float goalDistance = Mathf.Max(
            0f,
            obs.goalDistance
        );

        float goalAngle = Mathf.Clamp(
            obs.goalAngleDeg,
            -180f,
            180f
        );

        // Goal reached.
        if (goalDistance < 0.15f)
            return (0f, 0f);

        UpdateProgress(goalDistance, goalAngle);

        // Begin obstacle bypass only when necessary.
        if (!avoiding && GoalPathBlocked(goalAngle))
        {
            StartAvoidance(goalAngle);
        }

        UpdateAvoidance(goalAngle);

        float turn;

        if (avoiding)
        {
            turn = CalculateBypassTurn(goalAngle);
        }
        else
        {
            float goalTurn = GoalSteering(goalAngle);

            float obstacleTurn = CalculateReactiveTurn();

            float obstacleInfluence =
                Mathf.Clamp01(
                    (1.0f - frontF) / 0.75f
                );

            turn =
                goalTurn * (1f - obstacleInfluence) +
                obstacleTurn * obstacleInfluence;
        }

        // Steering smoothing.
        // Use stronger responsiveness during emergency avoidance.
        if (frontF < 0.55f)
        {
            turn = Mathf.Lerp(lastTurn, turn, 0.85f);
        }
        else
        {
            turn = Mathf.Lerp(lastTurn, turn, 0.55f);
        }

        turn = Mathf.Clamp(turn, -1f, 1f);

        lastTurn = turn;

        float speed = CalculateSpeed(turn);

        // Rotate toward a goal behind the rover before advancing.
        if (Mathf.Abs(goalAngle) > 125f)
            speed = 0f;

        // Keep moving slowly during normal obstacle bypass.
        if (avoiding && frontF > FRONT_CRITICAL)
            speed = Mathf.Min(speed, 0.30f);

        return (speed, turn);
    }
}
