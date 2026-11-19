# RoverNavStudy

Simulation study (Unity 6, URP) for an IEEE ICARA 2027 paper. Question: does robot control code
written by LLMs (chatbots) survive realistic, noisy, low-cost sensing? We compare 30 LLM-written
obstacle-avoidance controllers with a rule-based baseline and the Bug2 algorithm on the same
simulated rover.

See `CLAUDE.md` for the fixed study parameters and `docs/PROGRESS.md` for the phase checklist.

## Credits

Perseverance 3D model: NASA/JPL-Caltech, NASA 3D Resources

The model is an optional visual only (`SimConfig.showVisualModel`). It has no colliders, Rigidbody or
scripts, and it is switched off during experiments, so it plays no part in the physics or the results.
