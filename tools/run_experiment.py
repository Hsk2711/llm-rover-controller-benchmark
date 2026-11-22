"""run_experiment.py - starts the standalone experiment player and keeps it alive until the results file is complete.

What it does (Phase 7):
  1. launches Build/Experiment/RoverNavStudy.exe with -batchmode -nographics and the arguments you give;
  2. every few seconds checks the results csv: if NO NEW ROW appeared for --stall-seconds (default 180 s = 3 minutes), or the
     player exited before it said DONE, it kills the player and starts it again (the player RESUMES: it skips runs already in the csv);
  3. writes every restart to results/runner_restarts.csv (time, reason, rows, ...);
  4. stops when the player exited with code 0 and results/progress.txt says DONE (= the csv is complete).
  It gives up (exit code 3) after --max-failures restarts in a row that added no row at all (a run that always crashes or hangs).

Usage (arguments after "--" go to the player):
  python tools/run_experiment.py -- --quick --timescale 10 --arenas 8
  python tools/run_experiment.py -- --full --timescale 10 --arenas 8
Options of this script: --exe PATH, --stall-seconds N, --max-failures N, --poll N
"""
import argparse, csv, os, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESULTS = os.path.join(ROOT, "results")
PROGRESS = os.path.join(RESULTS, "progress.txt")
RESTARTS = os.path.join(RESULTS, "runner_restarts.csv")


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


def log_restart(attempt, reason, rows, seconds, exit_code):
    new = not os.path.exists(RESTARTS)
    with open(RESTARTS, "a", newline="") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["time", "launch_no", "reason", "rows_in_csv", "seconds_this_launch", "exit_code"])
        w.writerow([time.strftime("%Y-%m-%d %H:%M:%S"), attempt, reason, rows, round(seconds), exit_code])


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

    launch_no = 0
    failures_in_a_row = 0
    while True:
        launch_no += 1
        rows_before = row_count(csv_path)
        try:
            os.remove(PROGRESS)
        except OSError:
            pass
        cmd = [a.exe, "-batchmode", "-nographics", "-logFile", os.path.join(RESULTS, "player.log")] + player_args + ["--results", RESULTS]
        print("[%s] launch %d: %d rows in %s" % (time.strftime("%H:%M:%S"), launch_no, rows_before, os.path.basename(csv_path)), flush=True)
        proc = subprocess.Popen(cmd, cwd=os.path.dirname(a.exe))
        t0 = time.time()
        last_rows, last_change = rows_before, time.time()
        reason, exit_code = None, None
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
                break
            if time.time() - last_change > a.stall_seconds:
                reason = "no new row for %d s" % a.stall_seconds
                kill_tree(proc)
                exit_code = "killed"
                break
        rows = row_count(csv_path)
        log_restart(launch_no, reason, rows, time.time() - t0, exit_code)
        print("[%s] restart: %s (rows now %d)" % (time.strftime("%H:%M:%S"), reason, rows), flush=True)
        failures_in_a_row = 0 if rows > rows_before else failures_in_a_row + 1
        if failures_in_a_row >= a.max_failures:
            print("giving up: %d restarts in a row without a new row (see results/player.log, results/runner_errors.txt)" % failures_in_a_row)
            return 3


if __name__ == "__main__":
    sys.exit(main())
