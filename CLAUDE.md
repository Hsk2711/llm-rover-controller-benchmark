# RoverNavStudy - research project brief
Purpose: simulation study for an IEEE ICARA 2027 paper. Question: does robot control
code written by LLMs (chatbots) survive realistic, noisy, low-cost sensing? We compare
30 LLM-written obstacle-avoidance controllers with a rule-based baseline and the Bug2
algorithm on the same simulated rover. Results must be real, reproducible and fair.

## Fixed parameters (never change without asking me)
- Unity 6, URP. No ML-Agents in this project.
- Arena 6 m x 6 m, walls 0.5 m high. Start zone and goal zone 1 m x 1 m.
- Rover: box 0.20 m long x 0.15 m wide x 0.08 m high, Rigidbody mass 1 kg,
  velocity control (no wheel physics). Max speed 0.3 m/s, max turn rate 90 deg/s.
  No reversing.
- Sign conventions: positive turn = turn LEFT; positive goal angle = goal is to the LEFT.
- Sensors: 3 ultrasonic sensors at 0 deg (front), +45 deg (left), -45 deg (right).
  Each casts 5 rays in a +-15 deg cone, reading = shortest hit. Range 0.02-4.0 m
  (no hit = 4.0). Update 20 Hz. Noise: reading = true x (1 + e), e ~ Normal(0, noise^2),
  noise in {0, 0.02, 0.05, 0.10}. Dropout probability 0.02 (returns 4.0).
- Controllers decide every 0.05 s (20 Hz). Physics fixed timestep 0.02 s.
- A run ends on: goal reached (rover centre inside goal zone), any collision, or 60 s.
- All randomness from a seeded System.Random, never UnityEngine.Random.
- Every controller gets the same inputs: front, left, right readings, goal distance,
  goal angle. (Bug2 additionally uses rover position - must be stated in the paper.)

## Rules
- LLM-written controllers (folder Assets/Scripts/Controllers/LLM) are study subjects.
  NEVER edit, fix or improve them, except renaming the class to match the file name.
- All fixed numbers live in a ScriptableObject called SimConfig, never hard-coded.
- Build one step at a time. After each step, explain what you did in simple words;
  the user is a mechanical engineer, new to robotics software.
- Put a short comment at the top of every script saying what it does.
- Update docs/PROGRESS.md at the end of each phase.

## Folders
Assets/Scripts/{Rover,Sensors,Controllers,Controllers/LLM,Experiment}, Assets/Scenes,
Assets/Prefabs, Assets/Maps, results/, analysis/, docs/, docs/figures/
