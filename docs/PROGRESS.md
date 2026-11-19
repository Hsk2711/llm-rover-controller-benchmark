# Progress

Checklist of project phases. Update at the end of each phase.

- [x] Phase 0 - Setup check and project brief (Unity project, CLAUDE.md, git, folders, SimConfig, Python 3.12 venv)
- [ ] Phase 1 - Arena and rover
- [ ] Phase 2 - Ultrasonic sensor model and accuracy test
- [ ] Phase 3 - Four test maps and repeatable starts
- [ ] Phase 4 - Controller plug and rule-based baseline
- [ ] Phase 5 - Bug2 controller
- [ ] Phase 6 - LLM-written controllers (30)
- [ ] Phase 7 - Experiment runner and overnight run
- [ ] Phase 8 - Statistics and figures
- [ ] Phase 9 - Writing the paper
- [ ] Phase 10 - Final checks and submission

## Notes for later phases

- Phase 7: the ExperimentRunner MUST switch all visual-only extras OFF for all 15,360 runs: the Perseverance model,
  the follow/chase cameras (C key), the path trail and the HUD. One call does it: make a runtime copy
  (`var cfg = Object.Instantiate(config); cfg.SetExperimentMode();`), give that copy to every rover
  (`rover.config = cfg; rover.ApplyConfig();`) and never modify the `SimConfig.asset` file itself.
  Any new viewing aid must also be switched off inside `SimConfig.SetExperimentMode()`.
