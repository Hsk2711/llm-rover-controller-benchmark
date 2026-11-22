# Progress

Checklist of project phases. Update at the end of each phase.

- [x] Phase 0 - Setup check and project brief (Unity project, CLAUDE.md, git, folders, SimConfig, Python 3.12 venv)
- [x] Phase 1 - Arena and rover
- [x] Phase 2 - Ultrasonic sensor model and accuracy test (UltrasonicSensor, SensorRig, SensorTest scene, results/sensor_validation.csv)
- [x] Phase 3 - Four test maps and repeatable starts (MapDefinition, MapLoader, 4 maps, PracticeMapGenerator, TrialSpawner, results/start_poses.csv, Figure 2 pictures)
- [x] Phase 4 - Controller plug and rule-based baseline (Observation, IRoverController, SafeController, ControllerHost, dropdown, RuleBasedController + RuleParams)
- [x] Phase 5 - Bug2 controller (Bug2Controller + Bug2Params, wall-lost rule, Scene-view drawing)
- [ ] Phase 6 - LLM-written controllers (30)
- [ ] Phase 7 - Experiment runner and overnight run
- [ ] Phase 8 - Statistics and figures
- [ ] Phase 9 - Writing the paper
- [ ] Phase 10 - Final checks and submission

## Sensor model decisions (for the paper's Methods)

- Three sensors mounted at the middle of the rover's front face, mid-height, at 0, +45 (left) and -45 (right) degrees.
- Each sensor casts 5 rays in a horizontal fan (+-15 deg, 7.5 deg apart) and takes the shortest hit.
- Reading = true x (1 + e), e ~ Normal(0, noise^2), clipped to [0.02, 4.0] m. Dropout (lost echo) probability 0.02 -> 4.0.
- Open space (no ray hits anything within range) reads exactly 4.0 m: noise and dropout apply only to real echoes.
- Updates at 20 Hz on a 20 ms physics step, so measurements are 2 or 3 steps apart (average 50 ms).
- One seeded System.Random per rover feeds all three sensors in a fixed order; every measurement draws the same three
  random numbers, so one seed gives the same dropouts at every noise level.
- Validation (seed 12345, 100 readings per point): mean matches the true distance and sd matches noise x distance
  (sd/expected averages 0.995); 2.00% dropouts on real echoes.

## Map decisions (for the paper's Methods)

- Maps are `MapDefinition` assets in `Assets/Maps`, generated and checked by `analysis/make_maps.py` (run it again to
  rebuild them and the pictures in `docs/figures/`). Coordinates: x = east, z = north, arena centre (0,0); box rotation
  positive = counter-clockwise from above (= LEFT). Start zone centre (-2.5,-2.5), goal zone centre (2.5,2.5) on all maps.
- M1_Open: 5 boxes (0.4-0.6 m), every gap at least 0.98 m. M2_Cluttered: 18 boxes (0.3-0.6 m), every gap (box-box,
  box-wall, box-zone) at least 0.52 m, random placement with a fixed seed. M3_Corridor: 12 thin walls (0.1 m) forming a
  right-angle staircase corridor 0.5 m wide and 5.5 m long between two sealed 1.75 m x 1.75 m corner rooms (the corridor is
  the only way through). M4_Trap: U-shaped dead end, 1.5 m wide x 1.2 m deep CLEAR inside (walls 0.1 m), centred on the
  start-goal line with the opening towards the start, plus 3 boxes.
- Shortest collision-free path start centre -> goal centre (0.10 m clearance, grid + straightening): M1 7.18 m,
  M2 7.17 m, M3 7.56 m, M4 7.39 m (straight line 7.07 m). All under 8 m, so a controller at half speed (0.15 m/s) can
  still finish within 60 s (about 50 s).
- Repeatable starts (`TrialSpawner`): trial seeds 1-30. The start pose depends ONLY on the trial seed, never on the
  controller, noise level or map. Position: uniform in x and z within +-0.325 m of the start-zone centre, i.e. x, z in
  [-2.825, -2.175] m (zone half-width 0.5 m minus the rover's half-diagonal 0.125 m minus a 0.05 m wall clearance,
  SimConfig startWallClearance). The clearance is needed: with +-0.375 m the rover corner could sit 6 mm from the arena wall
  and the physics engine reports a contact within its 1 cm margin as a collision, ending the run at t = 0 (seed 2 did this).
  Heading: the rover faces the goal, turned by a uniform goal angle in [-45, +45] deg (positive = goal to the left;
  SimConfig startHeadingRangeDeg). Random numbers: `System.Random` seeded with an integer HASH of the trial seed. Plain
  `System.Random(seed)` with seeds 1, 2, 3, ... was tested and gives a perfect lattice (every draw grows by a constant step
  per seed, so poses are evenly spaced, not random; chi-square 24 against about 124 for random data); with the hash the
  steps are irregular and chi-square is 129. Checked on 1000 seeds: repeatable, rover always fully inside the zone, goal angle
  sign correct; on 4 maps x 1000 seeds no wall or obstacle is within 2 cm of the rover at the start.
  `TrialSpawner.Spawn(seed)` places and stops the rover and restarts its records.
- `results/start_poses.csv` (menu Rover > Export Start Poses): columns seed, x, z, heading_deg, goal_angle_deg for seeds
  1-30. heading_deg = rover heading in the map frame, counter-clockwise from east (+x), 0-360 (Unity yaw = 90 - heading);
  goal_angle_deg = angle to the goal as the rover sees it, positive = goal to the left.
- Figure 2 pictures: `docs/figures/maps/M1_Open.png` ... `M4_Trap.png`, 1920 x 1920 px, rendered by Unity from the arena's
  top-down orthographic camera (north up, x to the right), rover and HUD hidden, white background outside the arena.
  Orange = arena walls, grey = obstacles, green = start zone, blue = goal zone.
- The practice-map generator (`PracticeMapGenerator`, 5-15 boxes, sometimes a U-shape, free path guaranteed) is only for
  showing chatbots an example in Phase 6; it is not one of the test maps.

## Controller plug decisions (for the paper's Methods)

- Every controller implements `IRoverController` (Name, ResetController, Decide) and gets the same `Observation`: front,
  left, right readings, goal distance, goal angle (-180..180 deg, positive = goal to the LEFT); Bug2 alone also uses
  rover, start and goal position.
- EVERY controller runs through `SafeController`: an exception or a NaN output becomes (0, 0) and is counted; speed is clamped
  to 0..1 (no reversing) and turn to -1..1 (+-Infinity is clamped, clamped outputs are counted). Counters are per run
  (`ControllerHost.Safe.ExceptionCount`, `NanOutputCount`, `ClampedOutputCount`) and are reset at the start of each run.
- `ControllerHost` (on the Rover prefab) decides every 0.05 s, counted in 0.02 s physics steps like the sensors (so decisions are
  2 or 3 steps apart, exactly 20.00 Hz on average, measured), holds the command in between, and after a collision or reaching
  the goal it stops the rover and stops calling the controller. It runs after SensorRig, so a decision sees the newest reading.
- The Inspector dropdown lists every non-abstract class implementing `IRoverController`; a new controller file appears by itself.
  `ManualController` (keyboard) replaced the old `KeyboardController` component; `StopController` does nothing.

- Rule-based baseline (`RuleBasedController`, numbers in `Assets/Resources/RuleParams.asset`, standard round values, NOT tuned):
  (1) front < 0.30 m: stop and turn on the spot at full turn rate towards the side (left/right sensor) with more space (a tie
  turns left), and keep turning until front > 0.45 m; (2) else left or right < 0.20 m: 50% speed, full turn away from that side
  (if both are close, away from the nearer one); (3) else full speed, turn = clamp(goalAngle / 45 deg, -1, 1). The only memory
  is "am I in the middle of a front-obstacle turn". Tested with 20 table cases (thresholds, ties, order of the rules, memory).
  Known limit, by design of the sensors: they sit on the front face, so a corner beside the body is not seen (seed 1 on M1
  ended in a side graze at t = 13.9 s with the left sensor reading 0.21 m).
- Bug2 (`Bug2Controller`, numbers in `Assets/Resources/Bug2Params.asset`, standard values, NOT tuned). It also uses rover, start
  and goal position (state this in the paper). Each sensor is smoothed with a 3-reading median (one lost echo or one spike is
  ignored). GO_TO_GOAL: full speed, turn = clamp(goalAngle / 45, -1, 1) (keeps the rover on the M-line start -> goal); front < 0.30 m
  -> remember hit point and its distance to the goal -> FOLLOW_WALL. FOLLOW_WALL: obstacle on the RIGHT, turn = clamp(4 x (0.25 -
  right reading), -1, 1) at half speed; front < 0.30 m -> stop and turn left; leave when within 0.05 m of the M-line AND at least
  0.10 m closer to the goal than the hit point AND front > 0.45 m. Back within 0.15 m of the hit point after driving more than 1 m
  -> UNREACHABLE: stop. Numbers the brief left open (my standard choices): speeds 1.0 / 0.5, wall gain 4, front clear 0.45 m,
  return radius 0.15 m, stop-and-turn-in-place at the front threshold. The left sensor is smoothed but not used.
  Scene view (viewing aid, off in experiments): M-line (magenta), hit point (red), state label and marker on the rover.
  Tested: 25 table cases of the state machine (median, each leave condition alone, loop detection, reset).
  FINDING (exploratory, practice maps and two synthetic maps; made BEFORE the baselines were ever run on the test maps): an empty arena is solved 10/10, but
  10 random practice maps gave 9 collisions, and a plain straight wall or a 1 m box on the M-line gave 20/20 collisions. Cause:
  at the END of a wall the right sensor reads 4.0 m (nothing), the proportional rule saturates to a full right turn, and the
  front-right corner of the 0.2 m rover clips the corner (sensors on the front face cannot see a corner beside the body).
  A temporary experiment limiting the right turn to 0.2 whenever the wall is lost solved 20/20 on both synthetic maps, 0.4 failed
  20/20. NOT applied: it is a tuning decision for the author (see the open question in the chat).
  Afterwards both baselines were run on the four test maps (trial seeds 1-5, noise 0.05, exploratory): 0 of 40 reached the goal.
  Rule-based: collisions on M1, M2, M3 and a 60 s timeout on M4 (trapped inside the U). Bug2: collisions on all four; on M4 it followed
  the U's inside wall to the mouth and hit the tip of the left arm. Details: docs/reports/phases_3-5.md. Any later change to a
  baseline must be justified without using these results.
  WALL-LOST RULE (added at the author's request, values in Bug2Params, because the textbook algorithm assumes continuous wall
  contact): in FOLLOW_WALL, right reading above 0.60 m -> 50% speed and turn right at 60% until the right reading is back under
  0.60 m, then normal wall following resumes. Front < 0.30 m still has priority. Tested on the 4 test maps, trial seeds 1-3,
  noise 0.05: 0 of 12 reached the goal (all collisions, times within 0.1 s of the runs without the rule). A live trace of M1
  seed 1 shows the rule firing (speed 0.5, turn -0.60) but the 60% turn (arc radius about 0.16 m) swings the front onto the next
  face of the box (right reading 0.13-0.16 m a moment later) and the rover collides at 9.0 s. An earlier temporary experiment on
  simple maps suggested a much gentler turn (cap 0.2) works and 0.4 does not; not applied. Open design decision for the author.

## Pilot revision (2026-11-20, before Phase 6) - full details in docs/reports/pilot_revision.md

- Platform: 5 ultrasonic sensors instead of 3 (new: left side +90 deg and right side -90 deg, mid-body, same model and noise);
  `Observation` has `leftSide` and `rightSide`; SimConfig.sensorAnglesDeg = {0, 45, -45, 90, -90}; HUD and beams updated; CLAUDE.md updated.
  This applies to every controller, including the 30 chatbot ones (the Phase 6 prompt must describe five sensors).
- Phase 2 checks rerun for the two new sensors (Rover > Validate Side Sensors, results/sensor_validation_side.csv): all criteria pass
  (sd/(noise x mean) 0.994, dropout 0.020, mean = true at 0% noise).
- Bug2: wall-following distance (0.25 m) from the right SIDE sensor; wall-lost rule replaced: right side reading > 0.60 m -> drive straight
  50% speed for 0.20 m, then turn right 60% until the wall is found again; front < 0.30 m keeps priority. Rule-based: side rule uses the
  smaller of the 45 deg and 90 deg reading on each side.
- Validated ONLY on practice maps (seeds 3000-3049) and two synthetic maps, trial seeds 1-3, noise 0.05, nothing on the test maps.
  Practice goal rate before -> after: rule-based 52.0% -> 93.3%, Bug2 14.7% -> 32.0% (150 runs each).
- Provenance for the paper: the revision followed the baselines' failures on the test maps, so it is NOT blind to them; disclose it.
- BASELINES FROZEN from this point (RuleBasedController, Bug2Controller, RuleParams, Bug2Params); blob ids in the report.
  The earlier notes above (3 sensors, old wall-lost rule) describe the platform before this revision.

## Phase 6 step 1 (2026-11-20): project prepared for chatbot controllers (no controller written yet)

- `Assets/Scripts/Controllers/LLM/` (empty, `.gitkeep`): files named `LLM_<Model>_<Attempt>_<OS|FB>.cs`, class name = file name.
- `ControllerRegistry` (was `ControllerFactory`, renamed with git mv): finds all IRoverController classes by itself; kinds
  Baseline / Llm (name starts `LLM_`) / Utility; `ExperimentNames()` = the 2 baselines + all LLM controllers. The ControllerHost
  dropdown uses it. Misnamed LLM classes are reported (never fixed by us).
- Quick Test: menu Rover > Quick Test Controller (`QuickTestWindow`, `QuickTestRunner`). 10 practice maps (seeds 2000-2009,
  noise 0.05, trial seeds 1-10), one-line report in the wording of `docs/feedback_template.txt` (also copied to the clipboard,
  details in `results/quicktest/<controller>.txt`, not committed). Failure labels come from simple documented rules.
  Checked on the existing controllers: RuleBased 10/10, Bug2 5/10 (head-on collisions), Stop 0/10 (timeouts). Nothing run on test maps.
- `results/llm_generation_log.csv`: header only (model, model_version, date, attempt, one_shot_compiled, feedback_rounds,
  final_compiled, final_quicktest_score, human_minutes, notes).
- `docs/llm_task_spec.txt` saved unchanged (see the open issue below).

- TASK SPEC FINAL AND FROZEN (2026-11-20): `docs/llm_task_spec.txt` is now the five-sensor version supplied by the user (it replaces the
  3-sensor draft; same text for every one of the 30 attempts, never edited again). Checked against the code:
  * Observation fields named in the spec (front, left, right, leftSide, rightSide, goalDistance, goalAngleDeg) all exist as floats;
    Observation also has roverPos/startPos/goalPos (Vector2) which the spec tells the chatbot to ignore (only Bug2 uses them).
    Note for the paper: a chatbot controller COULD read them; the spec forbids it implicitly ("no position"). Since the harness now zeroes them (see next section), this is closed; the file check still reports any use.
  * Signs: turn positive = left (RoverDrive: yaw rate = -turn x rate, Unity +Y is right); goalAngleDeg positive = goal on the left
    (Vector2.SignedAngle, counter-clockwise in x/z seen from above; e.g. facing north with the goal north-east gives -45 = right);
    sensor angles positive = left (leftSide reads the west wall when facing north, measured 0.425 m at the arena start).
  * Numbers: speed 0.3 m/s, turn 90 deg/s, rover 0.20 x 0.15 m, range 0.02-4.0 m, +-15 deg cone, 20 Hz, 60 s, any contact ends the run,
    dropout returns 4.0, noise proportional to distance (study levels 0/2/5/10%, spec says "up to 10%"): all match SimConfig/CLAUDE.md.
  * Class name: the chatbot writes `LLM_Controller`; we rename it to the file name LLM_<Model>_<Attempt>_<OS|FB> (the only allowed edit).

## Harness change: position fields hidden from non-Bug2 controllers (2026-11-20, before the first LLM controller)

- `ControllerHost.Restrict(obs, controller)` is applied to every Observation before `Decide`: for every controller except
  `Bug2Controller` the fields roverPos, startPos, goalPos are (0,0); sensor readings, goalDistance and goalAngleDeg are untouched.
  Frozen baselines: no file changed; Rule-based never reads those fields, Bug2 still gets them.
- Tests: `Rover > Test Observation Restriction` (15 checks, all pass, includes every class in the registry). End-to-end in Play mode with a
  throw-away probe controller: probe saw (0,0) x3 while the host's own observation held the real positions (probe deleted afterwards).
  Regression: Quick Test after the change gives the same numbers as before (RuleBased 10/10, Bug2 5/10 with 5 collisions).
- `Rover > Check LLM Files` (LlmFileCheck, read-only): for each file in Controllers/LLM reports file-name pattern, class name = file name,
  one class only, any use of roverPos/startPos/goalPos (harmless now, but report it), forbidden APIs from the spec (Random, Physics,
  GameObject/Transform, Time) and re-declared Observation/IRoverController. Tested on 3 sample files. Never edits.

## Excluded attempts (2026-11-20)

- ChatGPT attempt 1 (one-shot) was NOT run in a Temporary Chat: protocol deviation, excluded from the study, file kept (not deleted).
  Moved to `Assets/Scripts/Controllers/LLM/Excluded/LLM_ChatGPT_1_OS_excluded.cs`; class renamed to match the file name, nothing else changed.
  Quick test before exclusion: 7/10 reached the goal, 3 head-on collisions (twice, identical). Logged in `results/llm_generation_log.csv`
  with notes starting "excluded - not a temporary chat".
- Mechanism: `ControllerRegistry.KindOf` gives every class whose name ends with `_excluded` the kind `Excluded`; `ExperimentNames()`
  (what the experiment runner must use) = baselines + kind Llm only, so excluded files can never enter a run. Quick Test can still run them
  (listed last). `Rover > Check LLM Files` lists excluded files and flags any file in Excluded/ whose file or class name lacks the suffix.
- Verified: ExperimentNames = Bug2Controller, RuleBasedController; Excluded = LLM_ChatGPT_1_OS_excluded; Llm = 0; misnamed = 0.
- Attempt numbers: VALID attempts are numbered 1-5 per chatbot; the excluded file is not counted. The first valid ChatGPT attempt (temporary chat,
  one-shot, does not compile: CS0136 at line 343) is therefore `LLM_ChatGPT_1_OS.cs` (first saved as _2_, renamed file and class, no other change).
  The excluded file keeps its `_excluded` name and log row (attempt cell `X1` in the log, flagged excluded in the notes).

## Non-compiling LLM files (2026-11-20)

- Rule: a chatbot file that does not compile is evidence, not something to fix. It is moved UNEDITED to `LLM_NoCompile/` in the project root
  (outside Assets, so Unity ignores it and the project compiles). It stays in the study as a result: success = 0, outcome `no_compile`.
- `LLM_ChatGPT_1_OS.cs` (official one-shot result, CS0136 at line 343) is the first such file: moved to `LLM_NoCompile/LLM_ChatGPT_1_OS.cs`
  (its .meta deleted). The project compiles again (checked: no compile errors; registry lists Bug2, RuleBased, Manual, Stop and the excluded class).
- Automatic for future files: `python tools/quarantine_noncompiling.py` (Editor open; `--dry-run` to only report). It forces a recompile, and if
  all errors are in LLM files (not Excluded/) it moves each such file, removes its .meta, sets one_shot_compiled (OS) or final_compiled (FB) to FALSE
  in `results/llm_generation_log.csv` with the first compiler error in the notes, recompiles and confirms. Errors in any other file: nothing is moved.
  Tested with a throw-away broken file (moved, logged, project compiled; test file and test log row removed afterwards).
- For Phase 7: `ControllerRegistry.ExperimentEntries()` returns (name, compiles) for the baselines, all LLM controllers and the files in
  `LLM_NoCompile/` (`NoCompileNames()`, outcome string `ControllerRegistry.NoCompileOutcome` = "no_compile"). The runner must write one result row per
  trial/noise/map for every no-compile controller with success=0 and outcome no_compile, and never try to run it. Note: if OS fails to compile and an FB
  file later compiles, they are two separate controllers (…_OS no_compile, …_FB normal).

## Feedback protocol (2026-11-20)

- Up to 3 feedback rounds per attempt in total (compile-error rounds count); stop early at 10/10. Written into CLAUDE.md (Rules).
- Status: ChatGPT attempt 1 has used 2 of 3 rounds. Round 1 (compile error sent back): FB file compiled, Quick Test 3/10 (copy in `LLM_History/LLM_ChatGPT_1_FB_r1.cs`).
  Round 2 (Quick Test report sent back): the new FB file did NOT compile (CS0136 at line 414); it replaced the round-1 file, was moved unedited to
  `LLM_NoCompile/LLM_ChatGPT_1_FB.cs` and the project compiles. 1 round remains. OPEN: is the attempt's final result the LATEST round or the best compiled one?
  Until decided, the log has final_compiled FALSE and an empty score; the experiment currently counts LLM_ChatGPT_1_FB as no_compile (and _OS too).
- `LLM_History/` (was LLM_History/) (outside Assets) keeps a copy of every round's file as evidence.

## Notes for later phases

- Phase 7: for each trial seed 1-30 call `TrialSpawner.Spawn(seed)` (after `MapLoader.Load(map)`) so every controller starts
  from the same pose.
- Phase 7: the ExperimentRunner MUST switch all visual-only extras OFF for all 15,360 runs: the Perseverance model,
  the follow/chase cameras (C key), the path trail and the HUD. One call does it: make a runtime copy
  (`var cfg = Object.Instantiate(config); cfg.SetExperimentMode();`), give that copy to every rover
  (`rover.config = cfg; rover.ApplyConfig();`) and never modify the `SimConfig.asset` file itself.
  Any new viewing aid must also be switched off inside `SimConfig.SetExperimentMode()`.

### Rule (2026-11-20): final FB result = LAST round
- The final FB result is ALWAYS the code of the last round used, never the best round. Every round is kept as evidence in
  `LLM_History/LLM_<Model>_<Attempt>_FB_r1.cs, _r2.cs, _r3.cs` (outside Assets). If the last round does not compile, the FB result
  is "no_compile". This answers the earlier open question (latest vs best).
- ChatGPT attempt 1 is NOT finished: round 3 (compile error of round 2 sent back) is in progress. model_version, human_minutes still to fill.

### ChatGPT attempt 1 - round 3 (final)
- Round 3 file compiles; Quick Test 0/10 (10 timeouts, "gets stuck in a dead end"). Per the last-round rule this is the final FB result
  (r1 3/10, r2 no_compile, r3 0/10; files in `LLM_History/`). Three rounds used: attempt stopped. model_version and human_minutes still to fill.
- LLM_NoCompile/ keeps only LLM_ChatGPT_1_OS.cs (r2 copy removed: identical in LLM_History/).

### ChatGPT attempt 2 - one-shot (OS)
- LLM_ChatGPT_2_OS.cs saved (class renamed only), compiles, Rover > Check LLM Files: OK. Quick Test 9/10 (1 head-on collision). Temporary chat.
- Attempt still open (feedback rounds possible, 0 used so far). model_version and human_minutes still to fill.

### ChatGPT attempt 2 - feedback round 1
- LLM_ChatGPT_2_FB.cs (round 1) compiles, class renamed only; Quick Test 0/10 (1 collision, 9 timeouts, dead end). OS was 9/10.
  Last-round rule: the FB result is 0/10 unless rounds 2-3 change it. Files in `LLM_History/` (LLM_ChatGPT_2_OS.cs, LLM_ChatGPT_2_FB_r1.cs). 1 of 3 rounds used.

### ChatGPT attempt 2 - feedback round 2
- Round 2 compiles (class renamed only); Quick Test 3/10 (0 collisions, 7 timeouts, oscillating). Rounds so far: OS 9/10, r1 0/10, r2 3/10.
  Current FB result (last-round rule) 3/10. File `LLM_History/LLM_ChatGPT_2_FB_r2.cs`. 2 of 3 rounds used.

### ChatGPT attempt 2 - feedback round 3 (final)
- Round 3 compiles (class renamed only); Quick Test 3/10 (1 collision, 6 timeouts, oscillating). FINAL FB result = 3/10 (last-round rule).
  Scores: OS 9/10, r1 0/10, r2 3/10, r3 3/10. All files in `LLM_History/`. Attempt closed (3 of 3 rounds used). model_version, human_minutes still to fill.

### Gemini attempt 1 - one-shot (OS)
- LLM_Gemini_1_OS.cs saved (class renamed only), compiles, Check LLM Files OK. Quick Test 10/10. Stop early at 10/10: attempt closed, 0 feedback rounds.
  Note: its Name property returns "LLM_Controller" (untouched). model_version, human_minutes still to fill.

### Gemini attempt 1 excluded (not a temporary chat)
- Gemini attempt 1 was not run in a Temporary Chat (protocol deviation, as for ChatGPT's first attempt). `LLM_Gemini_1_OS.cs` moved to
  `LLM/Excluded/LLM_Gemini_1_OS_excluded.cs` (class renamed to match; nothing else changed). Quick Test before exclusion: 10/10. Log row kept as
  attempt `X1`, "excluded - not a temporary chat". Registry and runner ignore it; valid Gemini attempts start again at 1.

### Gemini attempt 1 (valid) - one-shot (OS)
- LLM_Gemini_1_OS.cs saved (class renamed only), compiles. Name property: "DeterministicSensorNavigator". Quick Test 5/10 (0 collisions, 5 timeouts, oscillating).
  Temporary Chat, Auto mode. Attempt open (0 of 3 rounds used). The excluded earlier file is kept as LLM_History/LLM_Gemini_1_OS_excluded.cs.

### Gemini attempt 1 - feedback round 1
- LLM_Gemini_1_FB.cs (round 1) compiles (class renamed only); Name "AntiOscillationSensorNavigator". Quick Test 5/10 (0 collisions, 5 timeouts, oscillating). OS was 5/10.
  Current FB result (last-round rule) 5/10. Files in `LLM_History/` (LLM_Gemini_1_OS.cs, LLM_Gemini_1_FB_r1.cs). 1 of 3 rounds used.

### Gemini attempt 1 - feedback round 2
- Round 2 compiles (class renamed only); Name "SteadyProgressNavigator". Quick Test 3/10 (0 collisions, 7 timeouts, oscillating). Scores so far: OS 5, r1 5, r2 3.
  Current FB result (last-round rule) 3/10. File `LLM_History/LLM_Gemini_1_FB_r2.cs`. 2 of 3 rounds used.

### Gemini attempt 1 - feedback round 3 (final)
- Round 3 compiles (class renamed only); Name "CleanWallSeekNavigator". Quick Test 9/10 (1 collision, 0 timeouts, head-on). FINAL FB result = 9/10 (last-round rule).
  Scores: OS 5, r1 5, r2 3, r3 9. All files in `LLM_History/`. Attempt closed (3 of 3 rounds used). model_version, human_minutes still to fill.

### human_minutes definition (2026-11-20)
- human_minutes = active human time per attempt, from opening the chatbot chat to the final code being saved, excluding time waiting for compile and Quick Test.
  Corrected values (estimated afterwards): Gemini attempt 1 = 5, ChatGPT attempt 1 = 10, ChatGPT attempt 2 = 8 (were 20, 30, 25 under the old definition).
  Definition also in CLAUDE.md and results/llm_generation_log_columns.txt.

### Play-mode stall investigation (2026-11-20)
- Symptom: after entering Play mode (Quick Test or `editor.playmode start`) Time.frameCount stayed at 1 for minutes; stop + start usually fixed it (up to 20 min lost per Quick Test).
- Checked: Application.runInBackground = false, Editor not focused, no Enter-Play-Mode options (domain reload on every Play entry, ~5 s), default Interaction Mode.
  Editor.log shows thousands of repeated exceptions from the Unity AI editor package (AiEditorToolsSdk ... TrySetResult), a background task unrelated to our code.
- Tested (about 20 Play entries): normal start 17-27 s; start after 45 s with no CLI contact (3x); start right after a forced recompile (3x);
  real Quick Test route (RuleBased 1x, ChatGPT_2_OS 3x); real Quick Test after a real script change + recompile (RuleBased 3x). NO stall reproduced; every run reached
  frames > 300 within 40 s. So the stall is intermittent and its root cause is NOT proven. Ruled out: a controller infinite loop (stalls also hit a plain Play start with no
  controller), Quick Test code (plain `editor.playmode start` stalled too), background throttling by silence (silent waits worked), recompile right before (worked).
  Most likely an Editor-side hiccup while entering Play mode (assembly reload / AI-package background work / window focus); not changeable from our code.
- Mitigation (no effect on physics): tools/run_quicktest.py <Controller> runs the normal Quick Test and, if the frame counter stops advancing for 45 s, stops Play mode and
  restarts (up to 5 times); a stall now costs about a minute instead of up to 20. Prefer it over typing the retry by hand.
- Physics unchanged (nothing in simulation or controller code was touched). Reruns: RuleBased 10/10 (same as before); LLM_ChatGPT_2_OS 9/10 (same), and its per-run detail
  file results/quicktest/LLM_ChatGPT_2_OS.txt is byte-identical to the earlier one (3 reruns + 1 via the new driver).

### Quick Test always through the driver (2026-11-20)
- Rule (CLAUDE.md): run every Quick Test with `python tools/run_quicktest.py <Controller>`. Each run is appended to `results/quicktest_restarts.csv`
  (date, controller, restarts, seconds, outcome, report). Earlier Quick Tests (before this rule) were not counted; the stalls were handled by hand.
- To revisit before Phase 7: stall handling for the overnight runner.

### Claude attempt 1 - one-shot (OS)
- LLM_Claude_1_OS.cs saved (class renamed only), compiles, Check LLM Files OK. Name property "LLM_Controller" (untouched). Quick Test 10/10 via run_quicktest.py (0 restarts).
  Stop early at 10/10: attempt closed, 0 feedback rounds. Incognito chat. model_version, human_minutes still to fill.

### FB file for every valid attempt (2026-11-20)
- Rule (CLAUDE.md): each valid attempt has an OS and an FB file; a 10/10 one-shot gets FB = exact copy of OS (identifier rename only). Tool: `tools/ensure_fb_files.py` (`--check` to report).
- Created LLM_Claude_1_FB.cs (class AND constructor renamed, 2 occurrences). Quick Test via run_quicktest.py gives the same 10/10 as the OS. Audit: ChatGPT 1 (OS no_compile, FB r3),
  ChatGPT 2 (OS, FB r3) and Gemini 1 (OS 5/10, FB r3) already had both files; excluded attempts are ignored. Only Claude 1 was missing.

### ChatGPT attempt 3 - one-shot (OS)
- LLM_ChatGPT_3_OS.cs (class renamed only) does NOT compile: CS0019 at (251,13), `obs == null` on the struct Observation. Quarantined to LLM_NoCompile/ (copy in LLM_History/).
  Name property: "Adaptive Goal Navigation". Attempt open, 0 of 3 rounds used (a compile-error round counts).

### ChatGPT attempt 3 - feedback round 1
- Round 1 (compile error returned) compiles, class renamed only; Quick Test 2/10 (8 collisions, hits obstacles while turning). OS = no_compile. Current FB result (last-round rule) 2/10.
  1 of 3 rounds used. File `LLM_History/LLM_ChatGPT_3_FB_r1.cs`.

### ChatGPT attempt 3 - feedback round 2
- Round 2 compiles (class renamed only); Name "Adaptive Safe Navigation V2". Quick Test 1/10 (0 collisions, 9 timeouts, oscillating). Rounds: OS no_compile, r1 2/10, r2 1/10.
  Current FB result (last-round rule) 1/10. File `LLM_History/LLM_ChatGPT_3_FB_r2.cs`. 2 of 3 rounds used.

### ChatGPT attempt 3 - feedback round 3 (final)
- Round 3 compiles (class renamed only); Name "Persistent Adaptive Navigation V3". Quick Test 2/10 (0 collisions, 8 timeouts, oscillating). FINAL FB result = 2/10 (last-round rule).
  Scores: OS no_compile, r1 2, r2 1, r3 2. Files in `LLM_History/`. Attempt closed (3 of 3 rounds used). model_version, human_minutes still to fill.

### Gemini attempt 2 - one-shot (OS)
- LLM_Gemini_2_OS.cs saved (class renamed only), compiles. Name property "LLM_Controller" (untouched). Quick Test 8/10 (0 collisions, 2 timeouts, oscillating) via run_quicktest.py (0 restarts).
  Attempt open (0 of 3 rounds used). model_version, human_minutes still to fill.
