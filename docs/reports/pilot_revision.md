# Pilot revision: side sensors and baseline changes

RoverNavStudy (IEEE ICARA 2027 paper). Written 2026-11-20. This is a **pilot study**: a platform revision made before Phase 6
(the chatbot controllers), validated only on practice and synthetic maps. After it, both baselines are frozen.

## 0. Provenance: read this before citing any number

- The revision was decided **after** the baselines had failed on the four test maps (0 of 40 runs reached the goal, 0 of 12 with the
  first Bug2 wall-lost rule; see `docs/reports/phases_3-5.md`) and after practice-map failures. So the design is **not blind to the
  test maps**: the geometry problem that motivated it (a sensor on the front face cannot see a corner beside the body) was seen there.
  The paper must say that the baselines were revised after a pilot that included observing their failures on the test maps.
- In this pilot **nothing was run on the four test maps**. Validation used only `PracticeMapGenerator` seeds 3000-3049 (50 maps) plus two synthetic maps.
- Numbers are exploratory (noise 0.05 only, 3 trial seeds, sensor seed = trial seed). The study proper is Phase 7.

## 1. What changed

### 1.1 Platform (applies to EVERY controller, including the 30 chatbot ones)
| Item | Change |
|---|---|
| Sensors | 5 instead of 3. New: **left side +90 deg** and **right side -90 deg**, same model as the others (5 rays in +-15 deg, shortest hit, range 0.02-4.0 m, 20 Hz, same noise and dropout), mounted at the middle of each side of the body: left side at rover-local (-0.075, 0, 0) looking left, right side at (+0.075, 0, 0) looking right (measured). The original three stay at the middle of the front face |
| `SimConfig.sensorAnglesDeg` | `{0, 45, -45, 90, -90}` (indices 3 and 4 are the side-mounted ones) |
| `SensorRig` | new fields `leftSide`, `rightSide`; mounting for side sensors; ticked in the fixed order front, left, right, leftSide, rightSide; `GetReadings()` returns five values |
| Random stream | still ONE seeded `System.Random` shared by all sensors, now drawing in the order above. Consequence: the noise sequence of the original three sensors is **not** the same as on the 3-sensor platform (the before and after runs are not noise-paired) |
| Rover prefab | two new child objects `Sensor_LeftSide`, `Sensor_RightSide` (94 added lines, nothing removed) |
| `Observation` | new fields `leftSide`, `rightSide`; `ControllerHost` fills them |
| HUD, beams | HUD shows "Left side" and "Right side"; `SensorBeamView` draws 25 beams (5 sensors x 5 rays); verified 25 line objects |
| `CLAUDE.md` | sensors, controller inputs, and a new rule that freezes the baselines |
| Phase 2 files | `SensorValidation`, `UltrasonicSensor` and `results/sensor_validation.csv` untouched |

### 1.2 Bug2 (`Bug2Controller.cs`, `Bug2Params`)
- Wall-following distance now comes from the **right side sensor (90 deg)**, target still **0.25 m** (now measured square-on from the body side, so the gap to the body is 0.25 m; before, the 45 deg reading gave about 0.10 m).
- The earlier wall-lost rule (right 45 deg reading > 0.60 m -> 50% speed + 60% right turn) is **replaced** by three phases inside FOLLOW_WALL:
  1. **following** (normal proportional steering, gain 4, 50% speed);
  2. when the right side reading goes above **0.60 m**: **drive straight at 50% speed for 0.20 m** (one body length: the side sensor is mid-body, so the front half of the body is already past the corner);
  3. then **turn right at 60% of the max turn rate** (at 50% speed) until the wall is found again (right side reading <= 0.60 m), then phase 1 resumes.
- **Front < 0.30 m still has priority** (stop and turn left) and resets the phase.
- New parameter `lostWallAdvance` = 0.20 (m). Values kept in `Bug2Params`: `lostWallDistance` 0.60, `lostWallSpeed` 0.5, `lostWallAdvance` 0.20, `lostWallTurn` 0.6, `wallDistance` 0.25, `wallGain` 4, `hitDistance` 0.30, `frontClearDistance` 0.45, `goSpeed` 1.0, `wallSpeed` 0.5, `mLineTolerance` 0.05, `progressMargin` 0.10, `loopPathLength` 1.0, `hitReturnRadius` 0.15, `medianWindow` 3, `goalAngleForFullTurnDeg` 45.
- My readings of the brief (not specified by you): the speed during the turning phase is also 50%; if the wall comes back during the straight part, normal following resumes at once; "wall found again" means reading <= 0.60 m. All five sensors are median-filtered; Bug2 uses front and right side only.
- The scene-view label now also shows the phase ("wall lost: driving straight" / "turning right").

### 1.3 Rule-based (`RuleBasedController.cs`)
- Rule 2 (side obstacle) now uses, on each side, the **smaller of the 45 deg and the 90 deg reading**, same 0.20 m threshold, same actions (50% speed, steer away; both close: away from the nearer one). Nothing else changed: rule 1 still picks its turn direction from the 45 deg left and right readings, rule 3 unchanged. `RuleParams` unchanged.

## 2. Phase 2 sensor checks, rerun for the two new sensors
Tool: Editor menu **Rover > Validate Side Sensors** (`SideSensorValidation.cs`). It builds the real rover prefab in a throw-away scene 1000 m from the arena, puts a flat wall square-on to each side sensor at 0.05, 0.10, 0.20, 0.50, 1.00, 2.00, 3.00 and 4.50 m, takes 100 readings per point for each noise level {0, 0.02, 0.05, 0.10}, seed 12345 (same as Phase 2). Output: `results/sensor_validation_side.csv` (64 rows).

| Criterion (Phase 2) | leftSide | rightSide |
|---|---|---|
| 0% noise: mean = true distance (4.50 m reads 4.0000, out of range) | pass | pass |
| sd / (noise x mean), average over noisy in-range rows (Phase 2: 0.995) | 0.994 (min 0.80, max 1.12) | 0.994 (min 0.80, max 1.12) |
| sd grows with distance (each noise level) | pass | pass |
| sd grows with noise (1, 2, 3 m) | pass | pass |
| dropout rate on in-range rows (expected 0.02) | 0.020 | 0.020 |

- 27 of 32 rows per sensor are **identical** to the Phase 2 file; the other 5 differ by 0.0001 m in the mean only (float rounding: the test rover stands at 1000 m).
- Mounting check on the real rover at the arena start (-2.5, -2.5), noise 0: the left side sensor reads **0.425 m** (west wall 0.5 m from the centre minus 0.075 m), as predicted. Hand-cast centre rays of the front and right side sensors hit the far walls (5.39 m, 5.43 m, so they read 4.0). The 1.93 m and 1.96 m readings seen there come from the +-15 deg cone edges clipping the near walls (0.5 m / sin 15 deg = 1.93 m), not from a fault.

## 3. Code tests
- **40 table cases pass**: 16 rule-based (rule 1 unchanged and ignoring the side sensors; the new side rule with each side sensor alone, with the 45 deg sensor alone, the minimum, the 0.20 m boundary, both-close and tie cases; rule 1 beating rule 2; rule 3 unchanged) and 24 Bug2 (go to goal; median; follow steering uses the SIDE sensor and ignores the 45 deg one; the 0.60 m boundary; wall lost: straight at 0.10 and 0.19 m, turn right 60% at 0.20 m, found again -> following; wall back during the straight part; front priority over the turning phase and a new advance afterwards; reset; leave conditions; loop detection).
- One failure during testing was a wrong expected sign in my test (rule 1 picks the side with more space by the 45 deg readings, so it turned left); the controller was correct and the test was fixed.
- Play mode: five sensors present, `Observation` carries `leftSide`/`rightSide`, 25 beam objects, HUD shows both new lines.

## 4. Validation on practice maps only: before and after
Maps: `PracticeMapGenerator` seeds **3000-3049** (50 maps, 5-15 boxes each, sometimes a U-shape; the same 50 maps before and after), plus two synthetic maps: a 3.0 x 0.1 m wall through the arena centre perpendicular to the start-goal line, and a 1 x 1 m box on the start-goal line. Trial seeds 1, 2, 3 (so 150 practice runs + 3 + 3 per cell), noise 0.05, sensor seed = trial seed, 60 s limit, 16x game speed. **Before** = the code of commit `62db47a` (3 sensors; Bug2 with the 0.60 m / 60% wall-lost rule). **After** = this revision. 0 controller exceptions in all 624 runs.

| Controller | Condition | Practice (150): goal / collision / timeout | Synthetic wall (3) | Synthetic box (3) |
|---|---|---|---|---|
| Rule-based | before | 78 (52.0%) / 72 (48.0%) / 0 | 0 / 3 / 0 | 0 / 3 / 0 |
| Rule-based | **after** | **140 (93.3%)** / 6 (4.0%) / 4 (2.7%) | **3** / 0 / 0 | **3** / 0 / 0 |
| Bug2 | before | 22 (14.7%) / 128 (85.3%) / 0 | 0 / 3 / 0 | 0 / 3 / 0 |
| Bug2 | **after** | **48 (32.0%)** / 88 (58.7%) / 14 (9.3%) | **3** / 0 / 0 | **3** / 0 / 0 |

- Mean time to the goal on practice maps (successful runs): rule-based 23.1 s -> 28.0 s; Bug2 22.4 s -> 32.0 s.
- Per map (3 tries each, 50 maps): rule-based had more goals after on 27 maps, fewer on 1, equal on 22 (exact sign test p = 2.2e-7); Bug2 more on 13, fewer on 2, equal on 35 (p = 0.0074).
- Reading: both baselines improved a lot on practice maps; the rule-based controller now reaches the goal in 93% of tries, Bug2 in 32%. Bug2 still fails or times out most often on random cluttered maps. The timeouts (4 and 14 runs) were not analysed further.
- Caveats: the 3 tries on one map are not independent; before and after use different noise realisations (section 1.1); one noise level; practice maps are not the test maps, so these numbers do not predict the test-map results.

## 5. Freeze
From this commit on, **no further changes** to the two baselines. Frozen files (git blob ids; `git hash-object <file>` must still print the same id):

| File | Blob id |
|---|---|
| `Assets/Scripts/Controllers/RuleBasedController.cs` | `934c2f0139cfeabd86addc609d23559fe3d8cc1a` |
| `Assets/Scripts/Controllers/RuleParams.cs` | `ff4a6a7adc21ccb87de2bf562bb1e710ad9224ff` |
| `Assets/Resources/RuleParams.asset` | `1b1a864d323b3e3187752133b01d84c66c333e58` |
| `Assets/Scripts/Controllers/Bug2Controller.cs` | `8bad09e803dd0c38b1df1ff98ea26b89ed3f4e83` |
| `Assets/Scripts/Controllers/Bug2Params.cs` | `f33ffa551e376f8cc493e63d30be956c2fe7667e` |
| `Assets/Resources/Bug2Params.asset` | `5d4b0b0e53ee47e42d7af4dbec7110ac47c6ee05` |

The freeze is also written into `CLAUDE.md` (Rules) and `docs/PROGRESS.md`. Platform files (sensor rig, `SimConfig`, `Observation`) are covered by the existing "fixed parameters, never change without asking" rule.

## 6. Consequences for the study
- The Phase 6 prompt to the chatbots must describe **five** sensors and the two new inputs (`leftSide`, `rightSide`, +90 / -90 deg, at the middle of each side of the body).
- The earlier report `docs/reports/phases_3-5.md` and Phase 2 notes describe the 3-sensor platform; they remain a correct record of that earlier platform.
- `results/start_poses.csv` is unaffected (start poses do not depend on the sensors).
- Disclose in the paper: the baselines were revised after a pilot that included their failures on the test maps (section 0); the baselines use the side sensors (and Bug2 also the rover position).
