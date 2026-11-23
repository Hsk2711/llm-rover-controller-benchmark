"""run_experiment.py - starts the standalone experiment player and keeps it alive until the results file is complete.

What it does (Phase 7):
  1. launches Build/Experiment/RoverNavStudy.exe with -batchmode -nographics and the arguments you give;
  2. every few seconds checks the results csv: if NO NEW ROW appeared for --stall-seconds (default 180 s = 3 minutes), or the
     player exited before it said DONE, it kills the player and starts it again (the player RESUMES: it skips runs already in the csv);
  3. writes every restart to results/runner_restarts.csv (time, reason, rows, culprit run ...);
  4. stops when the player exited with code 0 and results/progress.txt says DONE (= the csv is complete).
  It gives up (exit code 3) after --max-failures restarts in a row that added no row at all (e.g. the program cannot start).

HANG HANDLING: the player lists the runs it is working on in results/inflight.txt. After a failure the next launch uses ONE arena, so
the run that freezes the program is known exactly. If two launches in a row fail on the SAME run (controller|map|noise|seed), that run
is added to results/hang_runs.txt; the player then writes it as success=0, outcome "hang" (no simulation) and the experiment goes on
with the normal number of arenas. If an isolated launch adds ISOLATION_ROWS (20) rows without failing, the failure was not a repeatable
hang and normal operation resumes. Every step is logged in results/runner_restarts.csv (column culprit_run).

Usage (arguments after "--" go to the player):
  python tools/run_experiment.py -- --quick --timescale 10 --arenas 8
  python tools/run_experiment.py -- --full --timescale 10 --arenas 8
Options of this script: --exe PATH, --stall-seconds N, --max-failures N, --poll N
Phase 8b (stress test): with "--out NAME.csv" the progress file is results/progress_NAME.txt and the restart log results/runner_restarts_NAME.csv,
so the main run's progress.txt is never overwritten.  Example (one condition per call, all into the same csv):
  python tools/run_experiment.py -- --full --timescale 10 --arenas 8 --noise-levels 0.20,0.30 --dropout 0.02 --out results_stress.csv
"""
import argparse, csv, os, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESULTS = os.path.join(ROOT, "results")
PROGRESS = os.path.join(RESULTS, "progress.txt")
RESTARTS = os.path.join(RESULTS, "runner_restarts.csv")
INFLIGHT = os.path.join(RESULTS, "inflight.txt")
HANGS = os.path.join(RESULTS, "hang_runs.txt")
ISOLATION_ROWS = 20          # an isolated launch that adds this many rows without failing: the failure was not a repeatable hang


def csv_name(player_args):
    if "--out" in player_args:
        return player_args[player_args.index("--out") + 1]
    return "quick.csv" if "--quick" in player_args else "results.csv"


def row_count(path):
    try:
        with open(path, "rb") as f:
            return max(0, f.read().count(b"\n") - 1)
    except OSError:
        return 0


def kill_tree(proc):
    if proc.poll() is None:
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True)
        try:
            proc.wait(timeout=30)
        except Exception:
            pass


def read_inflight():
    try:
        with open(INFLIGHT) as f:
            return [l.strip() for l in f if l.strip()]
    except OSError:
        return []


def with_arenas(player_args, n):
    """player arguments with --arenas replaced by n (None = leave as given)"""
    if n is None:
        return list(player_args)
    out, skip = [], False
    for x in player_args:
        if skip:
            skip = False
        elif x == "--arenas":
            skip = True
        else:
            out.append(x)
    return out + ["--arenas", str(n)]


def log_restart(attempt, reason, rows, seconds, exit_code, culprit=""):
    new = not os.path.exists(RESTARTS)
    with open(RESTARTS, "a", newline="") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["time", "launch_no", "reason", "rows_in_csv", "seconds_this_launch", "exit_code", "culprit_run"])
        w.writerow([time.strftime("%Y-%m-%d %H:%M:%S"), attempt, reason, rows, round(seconds), exit_code, culprit])


def main():
    argv = sys.argv[1:]
    if "--" in argv:
        i = argv.index("--")
        mine, player_args = argv[:i], argv[i + 1:]
    else:
        mine, player_args = argv, []
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=os.path.join(ROOT, "Build", "Experiment", "RoverNavStudy.exe"))
    ap.add_argument("--stall-seconds", type=int, default=180)
    ap.add_argument("--max-failures", type=int, default=5)
    ap.add_argument("--poll", type=int, default=10)
    a = ap.parse_args(mine)
    a.exe = os.path.abspath(a.exe)          # the player is started with its own folder as working directory
    if not os.path.exists(a.exe):
        print("player not found:", a.exe, "- build it first (menu Rover > Experiment > Build Player)")
        return 1
    if "--full" not in player_args and "--quick" not in player_args:
        player_args = ["--full"] + player_args
    csv_path = os.path.join(RESULTS, csv_name(player_args))
    os.makedirs(RESULTS, exist_ok=True)
    if "--out" in player_args:                       # separate progress / restart files, the main run's files stay untouched
        global PROGRESS, RESTARTS
        stem = os.path.splitext(os.path.basename(csv_path))[0]
        PROGRESS = os.path.join(RESULTS, "progress_%s.txt" % stem)
        RESTARTS = os.path.join(RESULTS, "runner_restarts_%s.csv" % stem)

    launch_no = 0
    failures_in_a_row = 0            # restarts in a row that added no row
    isolate = False                  # next launch uses one arena (to find the run that freezes the program)
    suspect, suspect_count = None, 0
    while True:
        launch_no += 1
        rows_before = row_count(csv_path)
        for f in (PROGRESS, INFLIGHT):
            try:
                os.remove(f)
            except OSError:
                pass
        args_now = with_arenas(player_args, 1 if isolate else None)
        cmd = [a.exe, "-batchmode", "-nographics", "-logFile", os.path.join(RESULTS, "player.log")] + args_now + ["--results", RESULTS]
        print("[%s] launch %d: %d rows in %s%s" % (time.strftime("%H:%M:%S"), launch_no, rows_before, os.path.basename(csv_path),
              "  (ONE arena: looking for the run that freezes the program)" if isolate else ""), flush=True)
        proc = subprocess.Popen(cmd, cwd=os.path.dirname(a.exe))
        t0 = time.time()
        last_rows, last_change = rows_before, time.time()
        reason, exit_code = None, None
        inflight_at_failure = []
        while True:
            time.sleep(a.poll)
            rows = row_count(csv_path)
            if rows != last_rows:
                last_rows, last_change = rows, time.time()
            code = proc.poll()
            if code is not None:
                exit_code = code
                done = os.path.exists(PROGRESS) and "DONE" in open(PROGRESS).read()
                if code == 0 and done:
                    print("[%s] finished: %d rows in %s" % (time.strftime("%H:%M:%S"), rows, os.path.basename(csv_path)), flush=True)
                    return 0
                reason = "player exited early (exit code %s)" % code
                inflight_at_failure = read_inflight()
                break
            if time.time() - last_change > a.stall_seconds:
                reason = "no new row for %d s" % a.stall_seconds
                inflight_at_failure = read_inflight()
                kill_tree(proc)
                exit_code = "killed"
                break
            if isolate and rows - rows_before >= ISOLATION_ROWS:
                # the isolated launch ran past the earlier failure: it was not a repeatable hang. Back to normal operation.
                kill_tree(proc)
                msg = "isolation ended: %d rows without failure, back to normal arenas" % (rows - rows_before)
                log_restart(launch_no, msg, row_count(csv_path), time.time() - t0, "stopped")
                print("[%s] %s" % (time.strftime("%H:%M:%S"), msg), flush=True)
                isolate, suspect, suspect_count, failures_in_a_row = False, None, 0, 0
                reason = None
                break
        if reason is None:
            continue
        rows = row_count(csv_path)
        culprit = ""
        if isolate and len(inflight_at_failure) == 1:
            culprit = inflight_at_failure[0]
            suspect_count = suspect_count + 1 if culprit == suspect else 1
            suspect = culprit
        elif isolate:
            suspect, suspect_count = None, 0       # failed with no (or several) runs in flight: cannot name a run
        made_progress = rows > rows_before
        if culprit and suspect_count >= 2:
            with open(HANGS, "a", encoding="utf-8") as f:
                f.write(culprit + "\n")
            reason += "; HANG: run %s failed twice in a row -> recorded as success=0, outcome hang" % culprit
            isolate, suspect, suspect_count = False, None, 0
            made_progress = True                   # a hang row is progress: this restart does not count as a failure
        else:
            isolate = True                         # next launch: one arena, so the guilty run can be named
        log_restart(launch_no, reason, rows, time.time() - t0, exit_code, culprit)
        print("[%s] restart: %s (rows now %d)" % (time.strftime("%H:%M:%S"), reason, rows), flush=True)
        failures_in_a_row = 0 if made_progress else failures_in_a_row + 1
        if failures_in_a_row >= a.max_failures:
            print("giving up: %d restarts in a row without a new row (see results/player.log, results/runner_errors.txt)" % failures_in_a_row)
            return 3


if __name__ == "__main__":
    sys.exit(main())
