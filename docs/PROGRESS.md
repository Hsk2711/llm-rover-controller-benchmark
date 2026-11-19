# Progress

Checklist of project phases. Update at the end of each phase.

- [x] Phase 0 - Setup check and project brief (Unity project, CLAUDE.md, git, folders, SimConfig, Python 3.12 venv)
- [x] Phase 1 - Arena and rover
- [x] Phase 2 - Ultrasonic sensor model and accuracy test (UltrasonicSensor, SensorRig, SensorTest scene, results/sensor_validation.csv)
- [ ] Phase 3 - Four test maps and repeatable starts
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

## Notes for later phases

- Phase 7: the ExperimentRunner MUST switch all visual-only extras OFF for all 15,360 runs: the Perseverance model,
  the follow/chase cameras (C key), the path trail and the HUD. One call does it: make a runtime copy
  (`var cfg = Object.Instantiate(config); cfg.SetExperimentMode();`), give that copy to every rover
  (`rover.config = cfg; rover.ApplyConfig();`) and never modify the `SimConfig.asset` file itself.
  Any new viewing aid must also be switched off inside `SimConfig.SetExperimentMode()`.
