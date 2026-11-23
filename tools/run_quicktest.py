#!/usr/bin/env python3
"""run_quicktest.py - run the Quick Test of one controller through the open Unity Editor and survive Play-mode stalls.

    python tools/run_quicktest.py LLM_Gemini_1_FB
    python tools/run_quicktest.py Bug2_Corrected --noise 0      (optional sensor noise; default 0.05 = the normal Quick Test)

Start route: Play mode is entered with `editor.playmode start` and the Quick Test is started from inside Play mode (changed 2026-11-22).
Why: now and then the Editor enters Play mode but the game loop stays on frame 1 and the test never starts (seen ~1 in 3 runs
after a new file was added, cost up to 20 min each time). This driver starts the test, polls the Editor's frame counter, and when
the frame has not advanced past 2 for STALL_SECONDS it stops Play mode and starts again (up to ATTEMPTS times). Nothing about the
simulation changes: the same QuickTestWindow.Start / QuickTestRunner code runs, only the start is retried. Prints the one-line
report (also in results/quicktest/<controller>.txt).
Every run is appended to results/quicktest_restarts.csv: date, controller, restarts (stalled starts that had to be redone),
seconds, outcome (ok / failed), report. This is how we count how often the stall happens.
"""
import csv, os, shutil, subprocess, sys, time

STALL_SECONDS = 45
RUN_SECONDS = 240
ATTEMPTS = 5


def unity(*args, timeout=40):
    exe = shutil.which("unity") or sys.exit("the 'unity' command line tool was not found on PATH")
    try:
        out = subprocess.run([exe, "command", "--project-path", ".", *args], capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return ""
    return (out.stdout or b"").decode("utf-8", "replace") + (out.stderr or b"").decode("utf-8", "replace")


LOG = os.path.join("results", "quicktest_restarts.csv")


def log_run(name, restarts, seconds, outcome, report):
    new = not os.path.exists(LOG)
    with open(LOG, "a", encoding="utf-8", newline="") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["date", "controller", "restarts", "seconds", "outcome", "report"])
        w.writerow([time.strftime("%Y-%m-%d %H:%M:%S"), name, restarts, int(seconds), outcome, report])


def frame():
    out = unity("script", "eval", "--code", "Time.frameCount.ToString()")
    for line in out.splitlines():
        parts = line.split("\t")
        if len(parts) >= 3 and parts[0].startswith("script eval") and parts[2].strip().isdigit():
            return int(parts[2])
    return None


def main():
    args = sys.argv[1:]
    noise = "0.05"
    if len(args) == 3 and args[1] == "--noise":
        noise = args[2]
        args = args[:1]
    if len(args) != 1:
        sys.exit(__doc__)
    name = args[0]
    result = os.path.join("results", "quicktest", name + ".txt")
    start_all = time.time()
    for attempt in range(1, ATTEMPTS + 1):
        unity("editor.playmode", "stop")
        time.sleep(10)
        # Enter Play mode with the plain CLI command (it worked in every stall episode, while entering Play mode from inside a
        # script eval did not), wait until the game loop really runs, and only then start the Quick Test inside Play mode.
        unity("editor.playmode", "start")
        t0 = time.time()
        f = None
        while time.time() - t0 < STALL_SECONDS:
            f = frame()
            if f is not None and f > 5:
                break
            time.sleep(3)
        else:
            print("attempt %d: Play mode did not start (frame %s after %d s) - stopping and retrying" % (attempt, f, STALL_SECONDS))
            continue
        mark = time.time()
        unity("script", "eval", "--code", 'QuickTestRunner.DefaultNoise = %sf; QuickTestWindow.Start("%s"); "started"' % (noise, name))
        t0 = time.time()
        last_move = t0
        last_frame = 0
        while time.time() - t0 < RUN_SECONDS:
            if os.path.exists(result) and os.path.getmtime(result) > mark:
                print("attempt %d ok after %d s:" % (attempt, time.time() - start_all))
                report = open(result, encoding="utf-8").readline().strip()
                print(report)
                print("restarts needed: %d" % (attempt - 1))
                log_run(name + ("" if noise == "0.05" else " (noise %s)" % noise), attempt - 1, time.time() - start_all, "ok", report)
                return 0
            f = frame()                     # also keeps the Editor awake
            if f is not None and f > last_frame + 2:
                last_frame, last_move = f, time.time()
            if time.time() - last_move > STALL_SECONDS:
                print("attempt %d: Play mode stalled (frame %s for %d s) - stopping and retrying" % (attempt, f, STALL_SECONDS))
                break
            time.sleep(4)
        else:
            print("attempt %d: no result after %d s - retrying" % (attempt, RUN_SECONDS))
    unity("editor.playmode", "stop")
    print("FAILED after %d attempts" % ATTEMPTS)
    log_run(name, ATTEMPTS, time.time() - start_all, "failed", "")
    return 1


if __name__ == "__main__":
    sys.exit(main())
