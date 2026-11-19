# Lab report: Phases 3, 4 and 5

RoverNavStudy (IEEE ICARA 2027 paper). Unity 6000.3.23f1, URP. Written 2026-11-19.
Scope: Phase 3 (four test maps and repeatable starts), Phase 4 (controller plug and rule-based baseline), Phase 5 (Bug2).

## 0. Read this first (what this report can and cannot claim)

- **Commit status.** Phase 3 is committed (`5f5e43e` visual fixes, `512541e` maps, `680623b` TrialSpawner/pictures) and ticked in
  `docs/PROGRESS.md`. Phases 4 and 5 were first committed in `06c9620` (WIP) and are ticked in `docs/PROGRESS.md` in the commit that adds the Bug2 wall-lost rule (section 4.6). Ticked means "built and tested", not "Bug2 succeeds": see 4.5.
- **"What happened when I watched".** I (the assistant) do not have your own observations. Section 3.5 and 4.5 report what *I*
  observed: one live watched run (rule-based, M1, seed 1) and a headless batch of both controllers on all four maps. Fields for your own
  observations are left marked `[your notes]`.
- **The batch is exploratory, not study data.** Trial seeds 1-5, sensor noise 0.05 only, 8x game speed. The study itself (Phase 7)
  uses 4 noise levels and 30 seeds.
- **No "Learn questions" list exists in the project files** (searched CLAUDE.md, docs/). Section 5 answers the explanation questions you
  asked me in chat for these phases. Paste the official questions and I will add them.
- **Integrity rule going forward.** Both baselines have now been run on the test maps. Any change to a baseline after this point must be
  justified independently of the test-map results, or the comparison is biased.

## 1. Fixed parameters in force (`Assets/SimConfig.asset`)

| Group | Values |
|---|---|
| Arena | 6 m x 6 m; walls 0.5 m high, 0.1 m thick, outside the arena; zones 1 m x 1 m; start centre (-2.5,-2.5), goal centre (2.5,2.5); obstacle height 0.5 m |
| Rover | 0.20 x 0.15 x 0.08 m, 1 kg, velocity control; max speed 0.3 m/s; max turn 90 deg/s; no reversing; positive turn = left |
| Sensors | 3 ultrasonic at 0, +45 (left), -45 (right) deg; 5 rays per sensor in +-15 deg, shortest hit; range 0.02-4.0 m (no hit = 4.0); 20 Hz; noise {0, 0.02, 0.05, 0.10}; dropout 0.02 (returns 4.0) |
| Timing | controller decides every 0.05 s; physics step 0.02 s; run ends on goal (rover centre in goal zone), any collision, or 60 s |
| Trial starts | trial seeds 1-30 (`firstTrialSeed` 1, `trialCount` 30); `startHeadingRangeDeg` 45; `startWallClearance` 0.05 m |
| Viewing aids only (off in experiments) | trail width 0.015 m; beam width 0.01 m; beam colours: red < 0.5 m, yellow 0.5-1.5 m, green beyond |

## 2. Phase 3 - maps and repeatable starts

### 2.1 What was built
- Scripts (`Assets/Scripts/Experiment`): `MapDefinition.cs` (data asset: box obstacles + zone centres), `MapLoader.cs` (removes old
  obstacles, builds grey boxes tagged `Obstacle`, moves the zone squares, refreshes `RoverStatus`), `PracticeMapGenerator.cs` (seeded random
  practice maps with flood-fill free-path check; for chatbot feedback only), `TrialSpawner.cs`, `Editor/MapWindow.cs` (menu
  **Rover > Load Map**, **Rover > Export Start Poses**).
- Assets: `Assets/Maps/M1_Open, M2_Cluttered, M3_Corridor, M4_Trap.asset`; `Assets/Materials/Obstacle.mat` (grey).
- Tool: `analysis/make_maps.py` defines the maps, checks every rule, writes the assets and `docs/figures/maps_overview.png`.
- Scene `Arena`: `MapLoader` and `TrialSpawner` objects added (no obstacles stored in the scene; map M1_Open is the scene's map).
- Outputs: `results/start_poses.csv` (seeds 1-30), Figure 2 pictures `docs/figures/maps/M1_Open.png ... M4_Trap.png`
  (1920 x 1920 px, top-down orthographic Unity render, north up, rover/HUD hidden, white background).
- Before this phase (commit `5f5e43e`): visual fixes: cyan 1.5 cm trail, nose hidden when the Perseverance model is shown, HUD margin 16 px,
  Game-view sensor beams (key G) coloured by distance. No physics or sensor logic changed.

### 2.2 Map parameters (all boxes are listed in Appendix A)
| Map | Construction |
|---|---|
| M1_Open | 5 boxes, all sides 0.4-0.6 m; hand-placed; smallest gap box-box 0.98 m, box-wall 1.25 m, box-zone 0.90 m |
| M2_Cluttered | 18 boxes, sides 0.3-0.6 m, rotation 0-90 deg; random placement, `random.Random(20270)`, minimum gap 0.52 m to boxes, walls and zones; measured gaps 0.527 (box-box), 0.525 (box-wall), 0.544 (box-zone) |
| M3_Corridor | 12 thin walls (0.1 m). Two sealed corner rooms 1.75 m x 1.75 m (containing the zones) joined by a right-angle staircase corridor, **0.5 m wide (inner face to inner face), 5.5 m long**. Centre line: (-1.5,-1.25) N0.75 (-1.5,-0.5) E1.5 (0,-0.5) N1.0 (0,0.5) E1.5 (1.5,0.5) N0.75 (1.5,1.25). Everything outside is sealed. Measured: nearest wall to the centre line 0.250 m everywhere |
| M4_Trap | U-shaped dead end **1.5 m wide x 1.2 m deep clear inside** (walls 0.1 m, so 1.7 x 1.3 m outside), centred at (0,0) on the start-goal line, rotated 45 deg, opening towards the start; 3 extra boxes (-1.3,1.1), (1.3,-1.1), (-0.7,-2.0). Extra boxes 0.53 m from the U; the start-goal line first touches the U at x = z = 0.43 m (inside face of the closed end) |

### 2.3 TrialSpawner: exact ranges
- Random numbers: `System.Random` seeded with an integer hash (murmur3 finalizer) of the trial seed. Same seed = same pose for every controller, noise level and map.
- **Position:** x and z each uniform within **+-0.325 m of the start-zone centre**, i.e. **x, z in [-2.825, -2.175] m**
  (= zone half-width 0.5 - rover half-diagonal 0.125 - wall clearance 0.05). The whole rover, in any heading, stays inside the zone and at least 5 cm from its edge.
- **Heading:** the rover faces the goal, turned by a goal angle uniform in **[-45, +45] deg** (positive = goal to the left).
- Observed over the 30 exported poses: x in [-2.8193, -2.1771], z in [-2.8213, -2.1940], goal angle in [-43.05, +43.91] deg,
  heading (map frame, counter-clockwise from east) in [1.76, 91.32] deg. All 30 poses: Appendix B.

### 2.4 Shortest collision-free path, start-zone centre to goal-zone centre
Method: grid 0.05 m, 16-neighbour Dijkstra, rover treated as a point kept 0.10 m from every box and wall, then straightened by line-of-sight shortcuts.
| Map | Grid path (m) | Shortest path (m) | x straight line | Time at 0.30 m/s | Time at 0.15 m/s |
|---|---|---|---|---|---|
| M1_Open | 7.26 | **7.18** | 1.02 | 24 s | 48 s |
| M2_Cluttered | 7.25 | **7.17** | 1.01 | 24 s | 48 s |
| M3_Corridor | 7.68 | **7.56** | 1.07 | 25 s | 50 s |
| M4_Trap | 7.41 | **7.39** | 1.04 | 25 s | 49 s |

Straight line 7.07 m. Requirement (yours): every map at most about 8 m so a controller at half speed can finish in 60 s. All four pass.

### 2.5 Decisions and changes (and why)
1. **M3 shortened.** First version: a 10 m staircase straight between the 1 m zones (the Manhattan distance between the zone centres is 10 m).
   You judged 10 m too long (a controller at 0.15 m/s would time out). Now 5.5 m between two enlarged sealed corner rooms; shortest path 7.56 m.
2. **U-shape size.** "1.5 x 1.2 m" was interpreted as the clear inside; you confirmed this.
3. **Corridor shape.** Right-angle staircase (alternating left/right turns); a true back-and-forth zig-zag would be 18 m or more.
4. **Trial seeds are hashed.** Plain `System.Random(seed)` with seeds 1, 2, 3, ... gave a perfect lattice: the step between consecutive seeds was identical
   for all 29 pairs on x, z and heading, and 1000 seeds fell in 125 cells with chi-square 24 (random data: about 124). With the hash, steps are irregular
   (24-25 distinct among 29) and chi-square is 129. This is a deviation from your literal wording `System.Random(seed)`; it still uses `System.Random`.
5. **Start range reduced from +-0.375 m to +-0.325 m** (wall clearance 0.05 m added). See failure F2 below.
6. An earlier repetition-based generator (+-0.30 m, +-30 deg, 30 repetitions) was replaced by the seed-based `TrialSpawner` (+-45 deg) and its code removed.
7. CLAUDE.md "Fixed parameters" now lists trial seeds 1-30, the position range, the +-45 deg heading and the hashed seeding.

### 2.6 Test results
Passed:
- `make_maps.py` rule checks, all four maps (counts, side ranges, inside the arena, clear of zones, gaps, free path by flood fill, corridor width 0.250 m).
- Editor test: M1/M2/M3/M4 build 5/18/12/6 tagged obstacles; loading the next map removes the old ones at once; rotation direction and 0.5 m height correct; 0 missing colliders.
- `PracticeMapGenerator`, seeds 1-200: 5 to 15 boxes every time, 70 of 200 contain a U-shape (35%), 0 differences between two generations of the same seed.
- `TrialSpawner`, seeds 1-1000: repeatable (0 differences), whole rover inside the zone (0 corners outside), goal-angle sign correct (0 mismatches), |goal angle| never above 45 deg;
  on all 4 maps x 1000 seeds, no wall or obstacle within 2 cm of the rover at the start (0).
- Play mode: `TrialSpawner.Spawn` puts the rover on the pose, zeroes velocity, resets status; same seed after moving the rover gives the same pose (0 differences, 4 maps x 200 seeds).
- `results/start_poses.csv`: two exports byte-identical; independent Python check of all 30 rows (range, heading + goal angle = direction to the goal): 0 violations; smallest gap to a wall 5.5 cm.

Failed and fixed:
- **F1 lattice pattern** in the first CSV (item 4 above). My first correlation test missed it (it cannot see a sawtooth); a step-regularity test found it.
- **F2 collision at t = 0.02 s for seed 2.** The rover corner started 6 mm from the arena wall, inside the physics engine's 10 mm contact margin, which counts as a collision. My earlier overlap test (exact geometry) could not see it. Fixed with the 0.05 m clearance; poses for seeds 1-30 changed, CSV regenerated.
- **F3 process error.** Saving the Arena scene from a stale in-memory copy briefly reverted the HUD margin to 30 px; restored to 16 px before commit.

## 3. Phase 4 - controller plug and rule-based baseline

### 3.1 What was built (`Assets/Scripts/Controllers`)
`Observation.cs`, `IRoverController.cs`, `SafeController.cs`, `ControllerHost.cs` (on the Rover prefab), `ControllerFactory.cs`, `ControllerChoiceAttribute.cs` +
`Editor/ControllerChoiceDrawer.cs` (Inspector dropdown of all controller classes), `ManualController.cs` (keyboard; replaces the old `KeyboardController`, which was
deleted), `StopController.cs`, `RuleBasedController.cs`, `RuleParams.cs` + `Assets/Resources/RuleParams.asset`. `RoverHud.cs` shows controller name, speed command, turn command and error count.
Scenes: Arena rover now carries `ControllerHost` (class `RuleBasedController`), old `KeyboardController` component removed; `TrialSpawner` has `spawnOnStart` (demo seed 1) so pressing Play runs M1_Open from seed 1.

### 3.2 Parameters
- `Observation`: front, left, right (m, noisy), goalDistance (m), goalAngleDeg (-180..180, positive = goal left); roverPos, startPos, goalPos (x,z) used only by Bug2.
- `SafeController`: speed clamped to 0..1 (-1..1 only if reversing were allowed), turn to -1..1; an exception or NaN output becomes (0,0); counters per run: exceptions, NaN outputs, clamped outputs.
- `ControllerHost`: decision interval = `controllerInterval` 0.05 s counted in 0.02 s physics steps (decisions 2 or 3 steps apart); execution order 100 (after the sensors); command held between decisions; stops the rover and stops asking after a collision or the goal.
- `RuleParams` (standard values, not tuned): front stop 0.30 m; front clear 0.45 m; escape turn 1.0 (full); side distance 0.20 m; side speed 0.5; side turn 1.0; cruise speed 1.0; goal angle for full turn 45 deg.
- Rule order: (1) front < 0.30: speed 0, full turn towards the side with more space (tie turns left), keep turning until front > 0.45; (2) else left or right < 0.20: speed 0.5, full turn away (both close: away from the nearer); (3) else speed 1.0, turn = clamp(goalAngle / 45, -1, 1).

### 3.3 Decisions and changes
- `KeyboardController` (a scene-only component writing the command every frame) would have fought with the host, so manual driving became `ManualController` (same W/A/D keys) and the old script was deleted.
- Rule-based numbers are the round values from your brief; the 0.45 m release and "stop while turning" are implemented as written. No tuning.
- Controller settings live in a `Resources` asset so a plain class can load them by name.

### 3.4 Test results
Passed:
- `SafeController` (9 situations): thrower x3 -> (0,0), 3 exceptions counted; NaN speed or turn -> (0,0); (5,-3) -> (1,-1) clamped; speed -0.5 without reversing -> 0, with reversing kept; +-Infinity clamped to (1,-1); valid (0.4,-0.2) unchanged; counters reset by `ResetController`; throwing reset counted; throwing `Name` falls back to the class name.
- Observation goal angle (rover at (-2.5,-2.5)): facing north -45.0, east +45.0, south +135.0, west -135.0 deg; goal distance 7.071 m; start position remembered at a new run; goal angle for seed 5 equals the spawner's 32.05 deg.
- Host timing: 78 decisions in 3.900 s = **20.00 Hz**; command 0.5 gives 0.150 m/s on the ground.
- Live host with a throwing controller from seed 2: 61 decisions, 61 exceptions counted, rover still, no collision, simulation kept running.
- Collision: rover driven into the west wall; collision at t = 0.38 s, 8 decisions, then speed 0 and no further decisions.
- Rule-based logic: 20 table cases pass (each rule, the exact boundaries front = 0.30 and side = 0.20, ties, rule order, the "keep turning until 0.45" memory, reset).

Failed / found: the seed-2 start collision (F2 above) was discovered by this phase's live test.

### 3.5 What happened on the four maps (rule-based)
One live watched run: M1, seed 1, noise default (sensor seed 0): drove towards the goal, **collision at t = 13.9 s** at (0.16,-0.56), left sensor 0.21 m (just above the 0.20 m rule). It brushed the middle box with its side.
Headless batch (mine): trial seeds 1-5, noise 0.05, sensor seed = trial seed. **Goal reached: 0 of 20.**
| Map | Result (5 seeds) | Time | Where it ended | What happened |
|---|---|---|---|---|
| M1_Open | 5 collisions | 14.0-14.9 s | (0.11..0.13, -0.56..-0.58), 3.9 m from goal | grazed the central box (0.1,-0.1), 3.56-3.88 m driven |
| M2_Cluttered | 5 collisions | 21.1-22.4 s | (1.41..1.44, 1.07..1.08), 1.8 m from goal | reached within 1.8 m of the goal, then hit box 14; 5.70-6.04 m driven |
| M3_Corridor | 5 collisions | 8.6-9.9 s | (-1.38..-1.41, -0.77..-0.84), 5.1 m from goal | failed at the **first bend** of the corridor (north to east), 2.25-2.61 m driven |
| M4_Trap | **5 timeouts (60 s)** | 60.0 s | inside the U, 3.2-3.6 m from goal | **Yes, it got stuck.** It entered the U every time, spent 48.6-49.6 s of the 60 s inside, never left, and did not collide; it kept moving in a region about 1 m wide (x -0.41..0.56, z -0.51..0.56), 11.3-11.8 m driven, closest approach 0.05-0.07 m |

Your own observations: `[your notes]`.
Spread between seeds is small: the start poses span 0.64 m (x) and 87 deg (goal angle, -43.05 to +43.91), but the paths converge on the same failure point.

## 4. Phase 5 - Bug2

### 4.1 What was built
`Bug2Controller.cs`, `Bug2Params.cs` + `Assets/Resources/Bug2Params.asset`, `IControllerGizmos.cs` (optional Scene-view drawing; `ControllerHost.OnDrawGizmos` calls it only while viewing aids are on).
Scene view: magenta M-line start-goal, red hit point (grey after leaving the wall), state marker and label on the rover ("Bug2: FOLLOW_WALL, hit point (x,z), d m from goal"). Checked in Play mode.

### 4.2 Parameters (`Bug2Params`, standard values, not tuned)
As specified by you: hit distance 0.30 m; wall distance 0.25 m (right sensor); M-line tolerance 0.05 m; progress margin 0.10 m; loop path length 1.0 m; median window 3.
Left open by the brief (my choices): front clear 0.45 m; go-to-goal speed 1.0; wall-follow speed 0.5; wall gain 4 (turn per metre of error, so a 0.25 m error is a full turn); goal angle for full turn 45 deg; hit-point return radius 0.15 m; stop-and-turn-left in place (speed 0) when front < 0.30 m while following.
**Wall-lost rule (added later at the author's request; values in `Bug2Params`):** `lostWallDistance` 0.60 m, `lostWallSpeed` 0.5, `lostWallTurn` 0.6. In FOLLOW_WALL, if the (median-filtered) right reading is above 0.60 m: 50% speed and turn right at 60% of the max turn rate until the reading is back under 0.60 m, then normal wall following resumes. Front < 0.30 m still takes priority. It was added because the textbook Bug2 algorithm assumes continuous wall contact, which a sonar on a rover does not give.
Logic: GO_TO_GOAL: turn = clamp(goalAngle/45, -1, 1); front (median) < 0.30 -> FOLLOW_WALL, store hit point and its goal distance. FOLLOW_WALL: turn = clamp(4 x (0.25 - right), -1, 1); leave when within 0.05 m of the M-line AND at least 0.10 m closer to the goal than the hit point AND front > 0.45; back within 0.15 m of the hit point after more than 1 m driven -> UNREACHABLE (stop). The left sensor is smoothed but not used.

### 4.3 Decisions
The wall-lost rule (4.2) was added after the first four-map result (section 4.5, first table) and after the diagnosis in 4.4. It is the author's decision, justified by the textbook assumption of continuous wall contact, not by tuning on the test maps. Its values (0.60 m, 50%, 60%) are the author's and were not changed after seeing results.
Bug2 uses rover, start and goal position (the one controller with extra knowledge; state this in the paper). Median filter latency: two consecutive low front readings are needed to trigger a hit (one spike is ignored by design).

### 4.4 Test results
Passed: 25 state-machine cases (go to goal and clamps; single 0.1 m spike ignored, two low readings trigger; follow-wall steering at right = 0.35, 0.25, 0.15, 4.0; front 0.20 turns left; each leave condition tested alone: 0.042 m off the line leaves, 0.071 m stays; 0.09 m closer stays, 0.11 m closer leaves; front 0.40 stays, 0.50 leaves; loop of 3.1 m gives up at step 77 of 80, a 0.63 m loop does not; UNREACHABLE stays stopped; reset).
Exploratory live runs (practice and synthetic maps only, chosen so the test maps stayed unseen at that time; noise 0 and 0.05, trial seeds 1-5):
| Situation | Result |
|---|---|
| Empty arena | **10 of 10 goals** (21.7-22.8 s, 6.51-6.83 m) |
| 10 random practice maps (seed 1) | 1 goal (map 7, 21.7 s), 9 collisions, 0 exceptions |
| Straight 3 m wall across the M-line | 0 of 10 (all collisions, 21.6-22.8 s) |
| 1 m x 1 m box on the M-line | 0 of 10 (all collisions, 17.2-18.3 s) |
Cause (from traces): along a straight wall the rover holds the right reading at 0.25 m with turn about 0. At the **end** of the wall the right sensor reads 4.0 m, the proportional rule saturates to a full right turn, and the rover's front-right corner clips the wall end. Sensors sit on the front face, so a corner beside the body is invisible.
Temporary experiment (removed, not in the code): limiting the right turn to a cap when the wall is lost: cap 0.4 -> 0 of 20 goals; **cap 0.2 -> 20 of 20 goals** on the wall and box maps.

### 4.5 What happened on the four maps (Bug2)
Headless batch (mine): trial seeds 1-5, noise 0.05. **Goal reached: 0 of 20.** Variation between seeds is small. 0 exceptions.
| Map | Result (5 seeds) | Time | Where it ended | What happened |
|---|---|---|---|---|
| M1_Open | 5 collisions | 8.9-10.4 s | (-1.65,-1.03), FOLLOW_WALL | started following the first box (-1.2,-1.0) and clipped its corner; 5.4 m from goal |
| M2_Cluttered | 5 collisions | 4.8-19.2 s | seeds 1,3,4: (-0.2,-0.15) at 11.6 s in GO_TO_GOAL (side graze of box 4); seed 2: 19.2 s FOLLOW_WALL near box 8; seed 5: 4.8 s GO_TO_GOAL near box 1 | 4 of 5 collisions happened while *not* following a wall |
| M3_Corridor | 5 collisions | 10.1-11.6 s | (-1.40,-0.80), FOLLOW_WALL | same first bend as the rule-based controller (the right wall ends where the corridor turns right) |
| M4_Trap | 5 collisions | 24.2-25.2 s | (-0.81,+0.03), FOLLOW_WALL, 4.1 m from goal | **It did not escape, but it came very close.** It drove into the U (GO_TO_GOAL until t = 12 s), hit the closed end at t = 13 s, turned left and followed the inside wall (wall on its right) round the dead end and down the left arm to the mouth of the U (13.6-14.2 s inside the U, seed 1 trace checked every second). It then collided with the **tip of the left arm** at the mouth, about 0.05 m before leaving the U (convex corner, same failure as elsewhere), 5.3-5.6 m driven |

Your own observations: `[your notes]`.

#### 4.5b With the wall-lost rule (final Bug2 as committed)
Test: all four maps, **3 tries each** (trial seeds 1, 2, 3), sensor noise 0.05, 8x speed, exploratory. **Goal reached: 0 of 12.** 0 exceptions.

| Map | Tries | Outcome | Time (s), seeds 1/2/3 | Same seeds without the rule (s) | State at collision |
|---|---|---|---|---|---|
| M1_Open | 3 | 3 collisions, **no goal** | 9.0 / 10.3 / 9.2 | 8.9 / 10.3 / 9.2 | FOLLOW_WALL |
| M2_Cluttered | 3 | 3 collisions, **no goal** | 11.6 / 19.2 / 11.7 | 11.6 / 19.2 / 11.7 | GO_TO_GOAL, FOLLOW_WALL, GO_TO_GOAL |
| M3_Corridor | 3 | 3 collisions, **no goal** | 10.2 / 11.4 / 10.3 | 10.1 / 11.4 / 10.2 | FOLLOW_WALL |
| M4_Trap | 3 | 3 collisions, **no goal** | 24.2 / 25.2 / 24.2 | 24.2 / 25.2 / 24.2 | FOLLOW_WALL |

The rule changed the outcome of no run (times differ by at most 0.1 s). A live trace (M1, seed 1, 0.1 s steps) shows the rule firing: at t = 8.0-8.2 s and 8.5-8.6 s the right reading is above 0.60 m and the command is speed 0.5, turn -0.60. But at t = 8.3 s the right sensor suddenly sees the next face of the rotated box at 0.16 m (0.13 m at 8.4 s) and the rover collides at 9.0 s. A 60% turn at 0.15 m/s is an arc of radius about 0.15 / (0.6 x 1.571) = 0.16 m, which swings the front of the 0.2 m rover onto the next face. Also, below 0.60 m the proportional term (gain 4) already commands -0.8 to -1.0 for readings 0.45-0.60 m, so the rule only softens the turn in the range above 0.60 m. The earlier temporary experiment (4.4) found a cap of 0.4 failed and 0.2 succeeded on the simple maps.

### 4.6 Decision history
The wall-lost rule was added as the author decided (above). Because it did not fix the corner collisions, the open question is whether to keep it as is and report Bug2's failure, or to change the rule. Earlier options were: (1) keep it as specified and report it; (2) add a documented rule such as a limited turn when the wall is lost (the 0.2 cap worked on simple maps); (3) drive straight a short distance before turning at a lost wall. Because both baselines have now been seen on the test maps, whichever option is chosen should be justified on the practice and synthetic maps and on the geometry, not on the test-map outcomes.

## 5. Answers to the explanation questions asked in chat

**Phase 3 - why must the chatbots never see the 4 test maps, and why the same seeds for every controller?**
Overfitting is like a student who memorises last year's exam: a perfect score on that exam, but failure when the questions change. A chatbot that saw the test maps could write code tailored to them ("turn left at x = 1.2 m"), which would show it can fit a known answer, not that it writes good obstacle avoidance. The test must be a surprise, so chatbots only get random practice maps.
A paired comparison tests two things under identical conditions, like two tyres on the same car, road and day. Luck (the start pose, the sensor noise and dropouts) is the weather. With the same seed every controller gets the same start and the same noise sequence (the sensors draw their random numbers at fixed 20 Hz steps), so a difference between controllers is the controller, not luck. It makes the comparison fair, the statistics stronger (compare seed by seed, luck cancels) and the study reproducible.

**Phase 4 - the plug idea.** A controller is like a power plug and the rover a wall socket. The socket is the same for everyone (`Observation`: front, left, right, goal distance, goal angle). The plug shape is the interface (`IRoverController`: a name, a reset, and Decide -> speed, turn). Between every plug and the rover sits a fuse box (`SafeController`): if a controller crashes or produces nonsense, the fuse trips, the rover gets (0,0) and the fault is counted. Nobody gets special treatment, so any controller that fits the plug can be swapped in from a dropdown.

**Phase 5 - the Bug2 state machine.** Crossing a dark room to a light switch with a tight string from where you started to the switch (the M-line). GO_TO_GOAL: walk along the string. You bump a sofa: note where you bumped and how far the switch is, then FOLLOW_WALL: slide your right hand along the sofa and walk around it (turn left if something is ahead). Leave the sofa only when you are back on the string, clearly closer to the switch than where you bumped, with a clear path ahead. If you get back to the exact bump spot after walking more than a metre, you have circled the obstacle: the switch is unreachable (UNREACHABLE: stop).

## 6. Caveats
- Batch results are one noise level, five seeds, 8x speed, exploratory; the real results come from Phase 7.
- Phases 4 and 5 are ticked in `docs/PROGRESS.md` as built and tested. Bug2 with the wall-lost rule reaches the goal in 0 of 12 exploratory tries on the four test maps; any further change to it is an open author decision and must not be justified by those test-map outcomes.
- Rover and sensor geometry make convex corners hard for any controller that reacts only when the wall is lost; this is a property of the setup and should be discussed in the paper, not hidden.

## Appendix A - every obstacle of the four test maps

Source: `Assets/Maps/*.asset` (generated by `analysis/make_maps.py`). Coordinates in metres, x = east, z = north, arena centre (0,0). Rotation in degrees, positive = counter-clockwise from above (= left). size_x runs along the box's own x axis before rotation. Height of every box 0.5 m (`SimConfig.obstacleHeight`). Start zone centre (-2.5, -2.5), goal zone centre (2.5, 2.5), zones 1 m x 1 m on all four maps.


### M1_Open (5 boxes)

| # | x | z | rotation | size_x | size_z |
|---|---|---|---|---|---|
| 0 | -1.2 | -1 | 30 | 0.5 | 0.5 |
| 1 | 1.1 | 1.2 | -20 | 0.6 | 0.4 |
| 2 | -1.4 | 1.3 | 45 | 0.5 | 0.5 |
| 3 | 1.3 | -1.2 | 10 | 0.4 | 0.6 |
| 4 | 0.1 | -0.1 | 60 | 0.5 | 0.5 |

### M2_Cluttered (18 boxes)

| # | x | z | rotation | size_x | size_z |
|---|---|---|---|---|---|
| 0 | -1.214 | 1.4903 | 77.4818 | 0.4856 | 0.3292 |
| 1 | -1.2142 | -1.7855 | 17.9505 | 0.5402 | 0.3257 |
| 2 | 0.6763 | -1.6926 | 38.6573 | 0.3176 | 0.5503 |
| 3 | 1.6768 | -0.5484 | 34.7149 | 0.5892 | 0.537 |
| 4 | -0.4474 | 0.1474 | 33.4376 | 0.3621 | 0.5178 |
| 5 | -2.1737 | 0.3571 | 7.7362 | 0.5284 | 0.5603 |
| 6 | 2.1743 | 0.9845 | 57.0631 | 0.4262 | 0.363 |
| 7 | -1.2144 | -0.6359 | 26.0031 | 0.4454 | 0.3767 |
| 8 | 0.6527 | 0.4304 | 54.8884 | 0.4024 | 0.457 |
| 9 | -0.2986 | 1.2957 | 27.3019 | 0.3545 | 0.3367 |
| 10 | 2.0257 | -1.95 | 12.5311 | 0.5611 | 0.3492 |
| 11 | 0.6238 | 2.2879 | 84.9231 | 0.3427 | 0.3627 |
| 12 | -2.1645 | 2.1683 | 0.9023 | 0.5439 | 0.4845 |
| 13 | -0.2906 | -1.1552 | 30.0021 | 0.3613 | 0.3376 |
| 14 | 1.2091 | 1.3791 | 3.1701 | 0.5375 | 0.3319 |
| 15 | -2.2925 | -1.0439 | 9.9063 | 0.313 | 0.3273 |
| 16 | -0.313 | 2.2565 | 86.6002 | 0.3448 | 0.3069 |
| 17 | 0.5544 | -0.5999 | 8.2158 | 0.3318 | 0.3236 |

### M3_Corridor (12 boxes)

| # | x | z | rotation | size_x | size_z |
|---|---|---|---|---|---|
| 0 | -2.375 | -1.2 | 0 | 1.25 | 0.1 |
| 1 | -1.8 | -0.7 | 0 | 0.1 | 1.1 |
| 2 | -1.05 | -0.2 | 0 | 1.6 | 0.1 |
| 3 | -1.2 | -1.875 | 0 | 0.1 | 2.25 |
| 4 | -0.45 | -0.8 | 0 | 1.6 | 0.1 |
| 5 | -0.3 | 0.3 | 0 | 0.1 | 1.1 |
| 6 | 0.45 | 0.8 | 0 | 1.6 | 0.1 |
| 7 | 0.3 | -0.3 | 0 | 0.1 | 1.1 |
| 8 | 1.05 | 0.2 | 0 | 1.6 | 0.1 |
| 9 | 1.2 | 1.875 | 0 | 0.1 | 2.25 |
| 10 | 1.8 | 0.7 | 0 | 0.1 | 1.1 |
| 11 | 2.375 | 1.2 | 0 | 1.25 | 0.1 |

### M4_Trap (6 boxes)

| # | x | z | rotation | size_x | size_z |
|---|---|---|---|---|---|
| 0 | 0.4596 | 0.4596 | 45 | 0.1 | 1.5 |
| 1 | -0.5303 | 0.601 | 45 | 1.3 | 0.1 |
| 2 | 0.601 | -0.5303 | 45 | 1.3 | 0.1 |
| 3 | -1.3 | 1.1 | 25 | 0.5 | 0.5 |
| 4 | 1.3 | -1.1 | -25 | 0.5 | 0.5 |
| 5 | -0.7 | -2 | 70 | 0.4 | 0.6 |

## Appendix B - the 30 start poses (`results/start_poses.csv`)

Trial seeds 1-30, produced by `TrialSpawner` (menu Rover > Export Start Poses). heading_deg = rover heading in the map frame, counter-clockwise from east (+x), 0-360 (Unity yaw = 90 - heading). goal_angle_deg = angle to the goal as the rover sees it, positive = goal to the LEFT.

| seed | x (m) | z (m) | heading (deg) | goal angle (deg) |
|---|---|---|---|---|
| 1 | -2.5603 | -2.6037 | 87.46 | -42.21 |
| 2 | -2.8193 | -2.8213 | 62.66 | -17.65 |
| 3 | -2.7459 | -2.4977 | 8.95 | 34.66 |
| 4 | -2.7423 | -2.4731 | 4.18 | 39.31 |
| 5 | -2.7165 | -2.7126 | 12.93 | 32.05 |
| 6 | -2.4337 | -2.3100 | 28.37 | 15.90 |
| 7 | -2.2148 | -2.1940 | 26.87 | 18.00 |
| 8 | -2.7922 | -2.4618 | 15.75 | 27.41 |
| 9 | -2.1771 | -2.6410 | 90.68 | -42.98 |
| 10 | -2.3476 | -2.6022 | 60.05 | -13.59 |
| 11 | -2.7909 | -2.5049 | 6.54 | 36.87 |
| 12 | -2.4136 | -2.6315 | 9.83 | 36.41 |
| 13 | -2.3076 | -2.4325 | 58.34 | -12.61 |
| 14 | -2.6752 | -2.4460 | 84.93 | -41.23 |
| 15 | -2.8058 | -2.5078 | 54.59 | -11.24 |
| 16 | -2.2034 | -2.7735 | 91.32 | -43.05 |
| 17 | -2.3934 | -2.6869 | 52.23 | -5.57 |
| 18 | -2.2812 | -2.3957 | 1.76 | 43.91 |
| 19 | -2.5102 | -2.7148 | 10.76 | 35.39 |
| 20 | -2.4643 | -2.7649 | 59.81 | -13.13 |
| 21 | -2.4355 | -2.2455 | 83.14 | -39.26 |
| 22 | -2.6976 | -2.3741 | 19.31 | 23.85 |
| 23 | -2.3811 | -2.3349 | 45.99 | -1.26 |
| 24 | -2.6390 | -2.4626 | 5.21 | 38.79 |
| 25 | -2.8129 | -2.2698 | 27.68 | 14.24 |
| 26 | -2.6180 | -2.7582 | 87.59 | -41.82 |
| 27 | -2.3608 | -2.4219 | 12.97 | 32.39 |
| 28 | -2.4598 | -2.2274 | 21.23 | 22.40 |
| 29 | -2.1979 | -2.6725 | 42.08 | 5.67 |
| 30 | -2.4871 | -2.4438 | 65.22 | -20.47 |
