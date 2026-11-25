# LLM Rover Controller Benchmark

Code, data and chatbot transcripts for the paper submitted to IEEE ICARA 2027: *does robot
control code written by LLMs (chatbots) survive realistic, noisy, low-cost sensing?*

30 obstacle-avoidance controllers written by three chatbots (ChatGPT, Gemini, Claude; one-shot "OS" and
after up to 3 rounds of feedback "FB") are compared with a rule-based baseline and the Bug2 algorithm on
the same simulated rover (Unity 6, URP; 6 m x 6 m arena, 4 maps, 5 ultrasonic sensors, noise 0 / 0.02 /
0.05 / 0.10, 30 trial seeds). Main run: 15,360 simulated runs. An exploratory stress test (noise 0.20 and
0.30, and 10 % dropout) adds 11,520 runs. The fixed study parameters are in `CLAUDE.md`; the full
step-by-step history is in `docs/PROGRESS.md`.

## How to reproduce

Requirements: Unity 6 (editor version in `ProjectSettings/ProjectVersion.txt`, 6000.3.23f1), Python 3,
Windows (the runner starts `Build/Experiment/RoverNavStudy.exe`).

1. Open the repository folder as a project in Unity 6. Let it import (first import takes a while).
2. Build the experiment player: menu **Rover > Experiment > Build Player** (writes `Build/Experiment/`).
   Rebuild after any code change.
3. Run the experiment (about 2.5 h for the main run at 8 arenas):
   `python tools/run_experiment.py -- --full --timescale 10 --arenas 8`
   Stress test: add `--noise-levels 0.20,0.30 --dropout 0.02 --out results_stress.csv` (see the header of
   `tools/run_experiment.py`). Check the file with `python tools/check_results.py results/results.csv`.
4. Analysis: `pip install -r analysis/requirements.txt`, then `python analysis/analyze.py`. Tables, figures and
   `summary.md` are written to `analysis/out/`.

Results depend on float arithmetic and on the arena position (70 m offset in `ExperimentRunner`); do not change
the offset or the sensor-seed scheme if you want bit-identical numbers.

## Folder overview

| Folder | Content |
|---|---|
| `Assets/`, `Packages/`, `ProjectSettings/` | the Unity project (rover, sensors, runner, maps, scenes) |
| `Assets/Scripts/Controllers/` | rule-based baseline, Bug2, parameters (frozen after the pilot) |
| `Assets/Scripts/Controllers/LLM/` | the chatbot-written controllers, unedited except the class name |
| `LLM_History/` | code of every feedback round (`_FB_r1/_r2/_r3`) and one-shot files, as evidence |
| `LLM_NoCompile/` | LLM files that did not compile (kept unedited; scored as failures) |
| `LLM_History/Excluded/`, `Excluded_Baselines/` | attempts / baseline variants excluded from the study, kept for transparency |
| `docs/` | task text given to the chatbots (`llm_task_spec.txt`), feedback template, transcripts (`llm_transcripts/`), reports, progress log |
| `results/` | `results.csv` (main run), `results_stress.csv`, `llm_generation_log.csv`, start poses, example paths, test logs |
| `analysis/` | `analyze.py` and `out/` (tables A1-S2, figures 1-6, `summary.md`) |
| `tools/` | experiment runner with watchdog, result checks, quick-test runner, FB-file and quarantine helpers |

## Third-party material

No third-party 3D models, textures or fonts are included. (An optional NASA/JPL Perseverance visual model that was
used for demo screenshots has been removed from the release; it was never active in the experiments, so the rover is
the plain box shown in the figures.)

- `Assets/TutorialInfo/`, `Assets/Readme.asset`, `Assets/Settings/`: Unity URP project template files, used
  under the Unity terms.
- Unity packages are listed in `Packages/manifest.json` and downloaded by the Unity Package Manager; they are
  not redistributed here.

## Licences

Code: MIT (`LICENSE`). Data, figures, transcripts and chatbot-generated controller files: CC BY 4.0
(`DATA_LICENSE`).

## How to cite

See `CITATION.cff` (GitHub shows a "Cite this repository" button). Authors: Hassankutty Kiliyamparambil
(ORCID 0009-0008-9335-994X) and Lubna Kakkengal.
