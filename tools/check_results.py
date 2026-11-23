"""check_results.py - sanity checks of an experiment results file (results/quick.csv or results/results.csv).

  python tools/check_results.py results/quick.csv --seeds 2         (expected rows = 33 controllers x 4 maps x 4 noises x seeds)
  python tools/check_results.py results/results.csv --seeds 30
Checks: header, exact row count, no duplicate (controller, map, noise, seed), no empty cell, no negative number, time_s <= 60,
success + collision + timeout = 1 for simulated rows, efficiency <= 1, the no_compile rows (two files, all conditions) and the
success rate per family and map. "NA" is allowed only where a number does not exist (efficiency of a failed run; every number of a no_compile row).
Exit code 0 = all checks passed.
"""
import argparse, csv, collections, sys

HEADER = "controller,family,model_label,attempt,condition,map,noise,seed,success,collision,timeout,exceptions,path_m,straight_m,efficiency,time_s,min_clear_cm,outcome".split(",")
NUM = ["noise", "seed", "success", "collision", "timeout", "exceptions", "path_m", "straight_m", "efficiency", "time_s", "min_clear_cm"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    ap.add_argument("--seeds", type=int, default=30)
    ap.add_argument("--controllers", type=int, default=33)
    ap.add_argument("--maps", type=int, default=4)
    ap.add_argument("--noises", type=int, default=4)
    a = ap.parse_args()
    rows = list(csv.reader(open(a.csv, newline="", encoding="utf-8")))
    ok = True

    def check(cond, text):
        nonlocal ok
        print(("PASS  " if cond else "FAIL  ") + text)
        ok = ok and cond

    check(rows[0] == HEADER, "header is exactly the 18 agreed columns")
    data = [dict(zip(HEADER, r)) for r in rows[1:]]
    expected = a.controllers * a.maps * a.noises * a.seeds
    check(len(data) == expected, "row count %d (expected %d)" % (len(data), expected))
    check(all(len(r) == len(HEADER) for r in rows), "every row has 18 cells")
    keys = collections.Counter((d["controller"], d["map"], d["noise"], d["seed"]) for d in data)
    check(max(keys.values()) == 1, "no duplicate (controller, map, noise, seed)")
    empty = [d for d in data if any(v.strip() == "" for v in d.values())]
    check(not empty, "no empty cells (%d rows with an empty cell)" % len(empty))

    bad_neg, bad_time, bad_flag, bad_eff, bad_na = 0, 0, 0, 0, 0
    for d in data:
        nc = d["outcome"] in ("no_compile", "hang")          # rows without a finished simulation: the simulation numbers are NA
        for f in NUM:
            v = d[f]
            if v == "NA":
                allowed = nc and f in ("path_m", "efficiency", "time_s", "min_clear_cm") or (not nc and f == "efficiency" and d["success"] == "0")
                if not allowed:
                    bad_na += 1
                continue
            if float(v) < 0:
                bad_neg += 1
        if not nc:
            if d["time_s"] != "NA" and float(d["time_s"]) > 60.0 + 1e-6:
                bad_time += 1
            if int(d["success"]) + int(d["collision"]) + int(d["timeout"]) != 1:
                bad_flag += 1
            if d["success"] == "1" and not (d["efficiency"] != "NA" and 0 < float(d["efficiency"]) <= 1.0):
                bad_eff += 1
    check(bad_neg == 0, "no negative number")
    check(bad_time == 0, "no time_s above 60 s")
    check(bad_flag == 0, "success + collision + timeout = 1 in every simulated row")
    check(bad_eff == 0, "efficiency is in (0, 1] for every successful run")
    check(bad_na == 0, "NA only where a number does not exist")

    nc = [d for d in data if d["outcome"] == "no_compile"]      # (hang rows are checked separately below)
    names = sorted(set(d["controller"] for d in nc))
    check(names == ["LLM_ChatGPT_1_OS", "LLM_ChatGPT_3_OS"], "no_compile rows exist for exactly the 2 files: %s" % names)
    per = a.maps * a.noises * a.seeds
    check(all(sum(1 for d in nc if d["controller"] == n) == per for n in names), "each no_compile file has %d rows, all with success=0" % per)
    check(all(d["success"] == "0" and d["exceptions"] == "0" for d in nc), "no_compile rows: success 0")
    ctrl = sorted(set(d["controller"] for d in data))
    check(len(ctrl) == a.controllers, "%d different controllers" % len(ctrl))
    hang = [d for d in data if d["outcome"] == "hang"]
    check(all(d["success"] == "0" and d["collision"] == "0" and d["timeout"] == "0" for d in hang), "hang rows (%d): success 0" % len(hang))
    sim = [d for d in data if d["outcome"] not in ("no_compile", "hang")]
    check(all(d["outcome"] in ("goal", "collision", "timeout") for d in sim), "outcomes of simulated rows are goal / collision / timeout")
    print("exceptions in simulated rows: %d rows with exceptions > 0" % sum(1 for d in sim if int(d["exceptions"]) > 0))

    print("\nSuccess rate per family and map (all noise levels, no_compile rows included in LLM; counts = successes / runs):")
    maps = sorted(set(d["map"] for d in data))
    print("%-6s " % "family" + " ".join("%-18s" % m for m in maps) + " all maps")
    for fam in ("Rule", "Bug2", "Bug2C", "LLM"):
        cells = []
        tot_s = tot_n = 0
        for m in maps:
            sub = [d for d in data if d["family"] == fam and d["map"] == m]
            s = sum(int(d["success"]) for d in sub)
            cells.append("%5.1f%% (%d/%d)" % (100.0 * s / len(sub), s, len(sub)) if sub else "-")
            tot_s += s; tot_n += len(sub)
        print("%-6s " % fam + " ".join("%-18s" % c for c in cells) + " %5.1f%%" % (100.0 * tot_s / tot_n))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
