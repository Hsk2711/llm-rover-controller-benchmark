# Progress

Checklist of project phases. Update at the end of each phase.

- [x] Phase 0 - Setup check and project brief (Unity project, CLAUDE.md, git, folders, SimConfig, Python 3.12 venv)
- [x] Phase 1 - Arena and rover
- [x] Phase 2 - Ultrasonic sensor model and accuracy test (UltrasonicSensor, SensorRig, SensorTest scene, results/sensor_validation.csv)
- [x] Phase 3 - Four test maps and repeatable starts (MapDefinition, MapLoader, 4 maps, PracticeMapGenerator, TrialSpawner, results/start_poses.csv, Figure 2 pictures)
- [ ] Phase 4 - Controller plug and rule-based baseline
- [ ] Phase 5 - Bug2 controller
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

## Notes for later phases

- Phase 7: for each trial seed 1-30 call `TrialSpawner.Spawn(seed)` (after `MapLoader.Load(map)`) so every controller starts
  from the same pose.
- Phase 7: the ExperimentRunner MUST switch all visual-only extras OFF for all 15,360 runs: the Perseverance model,
  the follow/chase cameras (C key), the path trail and the HUD. One call does it: make a runtime copy
  (`var cfg = Object.Instantiate(config); cfg.SetExperimentMode();`), give that copy to every rover
  (`rover.config = cfg; rover.ApplyConfig();`) and never modify the `SimConfig.asset` file itself.
  Any new viewing aid must also be switched off inside `SimConfig.SetExperimentMode()`.
