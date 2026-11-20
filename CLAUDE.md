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
- Sensors (pilot revision 2026-11-20: 5 sensors, was 3): ultrasonic sensors at 0 deg (front),
  +45 deg (left), -45 deg (right), all at the middle of the front face, and +90 deg (left side),
  -90 deg (right side), at the middle of each side of the body.
  Each casts 5 rays in a +-15 deg cone, reading = shortest hit. Range 0.02-4.0 m
  (no hit = 4.0). Update 20 Hz. Noise: reading = true x (1 + e), e ~ Normal(0, noise^2),
  noise in {0, 0.02, 0.05, 0.10}. Dropout probability 0.02 (returns 4.0).
- Controllers decide every 0.05 s (20 Hz). Physics fixed timestep 0.02 s.
- A run ends on: goal reached (rover centre inside goal zone), any collision, or 60 s.
- All randomness from a seeded System.Random, never UnityEngine.Random.
- Trial seeds 1 to 30 (SimConfig firstTrialSeed, trialCount). The start pose depends only on the trial
  seed (same seed = same pose for every controller, noise level and map), made by TrialSpawner:
  position uniform in x and z within +-0.325 m of the start-zone centre (x, z in [-2.825, -2.175] m:
  zone half-width 0.5 m minus rover half-diagonal 0.125 m minus 0.05 m wall clearance, SimConfig
  startWallClearance, so the whole rover is inside the zone and never touches the arena wall);
  heading = facing the goal, turned by a uniform goal angle within +-45 deg (positive = goal to the LEFT).
  The trial seed goes through an integer hash before it seeds System.Random (plain consecutive seeds
  give a regular lattice, not random poses). The 30 poses are in results/start_poses.csv.
- Every controller gets the same inputs: front, left, right, leftSide, rightSide readings,
  goal distance, goal angle. (Bug2 additionally uses rover position - must be stated in the paper.)
  ENFORCED by ControllerHost.Restrict() (harness change 2026-11-20): for every controller except Bug2Controller the Observation fields
  roverPos, startPos, goalPos are set to (0,0) before Decide, so LLM controllers get exactly what docs/llm_task_spec.txt describes.
  Test: menu Rover > Test Observation Restriction. LLM files are audited (read-only) by Rover > Check LLM Files.

## Rules
- LLM-written controllers (folder Assets/Scripts/Controllers/LLM) are study subjects.
  NEVER edit, fix or improve them, except renaming the class to match the file name.
- BASELINES ARE FROZEN (pilot revision, 2026-11-20): RuleBasedController, Bug2Controller, RuleParams and
  Bug2Params must not be changed any more (docs/reports/pilot_revision.md lists their exact file versions).
- FEEDBACK PROTOCOL for LLM attempts: up to 3 feedback rounds per attempt IN TOTAL; a round that only sends back a compile error
  counts as one of the 3. Stop early as soon as the Quick Test shows 10/10. Each round sends only the exact one-line report from
  docs/feedback_template.txt (or the compiler error text). Log feedback_rounds and the final score in results/llm_generation_log.csv.
- FINAL FB RESULT = the code of the LAST round used, ALWAYS, never the best round. Every round's code is kept as evidence outside
  Assets in LLM_History/LLM_<Model>_<Attempt>_FB_r1.cs, _r2.cs, _r3.cs. If the last round does not compile, the FB result is
  "no_compile" (success=0), even if an earlier round compiled and scored higher.
- human_minutes (results/llm_generation_log.csv) = active human time per attempt, from opening the chatbot chat to the final code being saved,
  excluding time waiting for compile and Quick Test. Values entered afterwards are marked "estimated afterwards" in the notes.
  (Column definitions: results/llm_generation_log_columns.txt.)
- LLM files that do NOT compile are moved UNEDITED out of Assets to LLM_NoCompile/ (tools/quarantine_noncompiling.py does it and logs it).
  Their controllers stay in the study: the experiment runner must report them as success=0, outcome "no_compile" in every trial
  (ControllerRegistry.ExperimentEntries()). Never fix them, never drop them.
- All fixed numbers live in a ScriptableObject called SimConfig, never hard-coded.
- Build one step at a time. After each step, explain what you did in simple words;
  the user is a mechanical engineer, new to robotics software.
- Put a short comment at the top of every script saying what it does.
- Update docs/PROGRESS.md at the end of each phase.

## Folders
Assets/Scripts/{Rover,Sensors,Controllers,Controllers/LLM,Experiment}, Assets/Scenes,
Assets/Prefabs, Assets/Maps, results/, analysis/, docs/, docs/figures/
