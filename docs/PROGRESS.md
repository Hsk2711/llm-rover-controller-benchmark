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
  controller, noise level or map. Position: uniform in x and z within +-0.375 m of the start-zone centre, i.e. x, z in
  [-2.875, -2.125] m (zone half-width 0.5 m minus the rover's half-diagonal 0.125 m, so the whole rover is inside the zone).
  Heading: the rover faces the goal, turned by a uniform goal angle in [-45, +45] deg (positive = goal to the left;
  SimConfig startHeadingRangeDeg). Random numbers: `System.Random` seeded with an integer HASH of the trial seed. Plain
  `System.Random(seed)` with seeds 1, 2, 3, ... was tested and gives a perfect lattice (every draw grows by a constant step
  per seed, so poses are evenly spaced, not random; chi-square 24 against about 124 for random data); with the hash the
  steps are irregular and chi-square is 129. Checked on 1000 seeds: repeatable, rover always fully inside the zone, goal angle
  sign correct; on 4 maps x 200 seeds the rover never touched a wall or obstacle at the start.
  `TrialSpawner.Spawn(seed)` places and stops the rover and restarts its records.
- `results/start_poses.csv` (menu Rover > Export Start Poses): columns seed, x, z, heading_deg, goal_angle_deg for seeds
  1-30. heading_deg = rover heading in the map frame, counter-clockwise from east (+x), 0-360 (Unity yaw = 90 - heading);
  goal_angle_deg = angle to the goal as the rover sees it, positive = goal to the left.
- Figure 2 pictures: `docs/figures/maps/M1_Open.png` ... `M4_Trap.png`, 1920 x 1920 px, rendered by Unity from the arena's
  top-down orthographic camera (north up, x to the right), rover and HUD hidden, white background outside the arena.
  Orange = arena walls, grey = obstacles, green = start zone, blue = goal zone.
- The practice-map generator (`PracticeMapGenerator`, 5-15 boxes, sometimes a U-shape, free path guaranteed) is only for
  showing chatbots an example in Phase 6; it is not one of the test maps.

## Notes for later phases

- Phase 7: for each trial seed 1-30 call `TrialSpawner.Spawn(seed)` (after `MapLoader.Load(map)`) so every controller starts
  from the same pose.
- Phase 7: the ExperimentRunner MUST switch all visual-only extras OFF for all 15,360 runs: the Perseverance model,
  the follow/chase cameras (C key), the path trail and the HUD. One call does it: make a runtime copy
  (`var cfg = Object.Instantiate(config); cfg.SetExperimentMode();`), give that copy to every rover
  (`rover.config = cfg; rover.ApplyConfig();`) and never modify the `SimConfig.asset` file itself.
  Any new viewing aid must also be switched off inside `SimConfig.SetExperimentMode()`.
