#!/usr/bin/env python3
"""ensure_fb_files.py - every valid LLM attempt must have BOTH an OS and an FB file.

    python tools/ensure_fb_files.py            # create missing FB files, update the log
    python tools/ensure_fb_files.py --check    # only report

Rule (CLAUDE.md): if an attempt reached 10/10 in the one-shot (stop early, 0 feedback rounds), its FB result is the OS code unchanged,
so LLM_<Model>_<N>_FB.cs is made as an exact copy of LLM_<Model>_<N>_OS.cs; the ONLY change is the identifier LLM_<Model>_<N>_OS ->
LLM_<Model>_<N>_FB (the class name, and the constructor name if the code has one - otherwise it would not compile). The log row gets
the note "FB = OS". Attempts that are not closed at 10/10 are only reported, never filled in. A no-compile OS file (LLM_NoCompile/)
counts as existing. Excluded attempts (Excluded/) are ignored. After running it, recompile (tools/quarantine_noncompiling.py).
"""
import csv, io, os, re, sys

LLM = os.path.join("Assets", "Scripts", "Controllers", "LLM")
NOCOMP = "LLM_NoCompile"
LOG = os.path.join("results", "llm_generation_log.csv")
OS = re.compile(r"^LLM_([A-Za-z0-9]+)_([0-9]+)_OS$")


def names(folder):
    return {os.path.splitext(f)[0] for f in os.listdir(folder) if f.endswith(".cs")} if os.path.isdir(folder) else set()


def main():
    check = "--check" in sys.argv
    have = names(LLM) | names(NOCOMP)
    text = open(LOG, encoding="utf-8", newline="").read()
    nl = "\r\n" if "\r\n" in text else "\n"
    rows = list(csv.reader(io.StringIO(text)))
    changed = False
    problems = 0
    for name in sorted(have):
        m = OS.match(name)
        if not m:
            continue
        model, n = m.groups()
        fb = "LLM_%s_%s_FB" % (model, n)
        if fb in have:
            continue
        row = next((r for r in rows[1:] if r[0] == model and r[3] == n and not r[9].startswith("excluded")), None)
        closed = row is not None and row[5] == "0" and row[4] == "TRUE" and row[7] == "10"
        if not closed:
            print("MISSING %s.cs and the attempt is not closed at 10/10 one-shot - NOT created (feedback rounds still to come?)" % fb)
            problems += 1
            continue
        if check:
            print("MISSING %s.cs (OS reached 10/10 with 0 rounds): would be created" % fb)
            problems += 1
            continue
        src = os.path.join(LLM, name + ".cs")
        code = open(src, encoding="utf-8", newline="").read()
        new, k = re.subn(r"\b%s\b" % re.escape(name), fb, code)
        open(os.path.join(LLM, fb + ".cs"), "w", encoding="utf-8", newline="").write(new)
        os.makedirs("LLM_History", exist_ok=True)
        open(os.path.join("LLM_History", fb + "_r0_copy_of_OS.cs"), "w", encoding="utf-8", newline="").write(new)
        row[9] += " | FB = OS: one-shot scored 10/10, so no feedback round; %s.cs is an exact copy of %s.cs with only the identifier %s -> %s renamed (%d occurrence(s): class name%s). FB result = 10/10, 0 feedback rounds." % (
            fb, name, name, fb, k, " and constructor" if k > 1 else "")
        changed = True
        print("created %s.cs (%d identifier occurrence(s) renamed)" % (fb, k))
    if changed:
        out = io.StringIO()
        csv.writer(out, lineterminator=nl).writerows(rows)
        open(LOG, "w", encoding="utf-8", newline="").write(out.getvalue())
    if not problems and not changed:
        print("OK: every valid OS attempt has an FB file.")
    return 1 if (check and problems) else 0


if __name__ == "__main__":
    sys.exit(main())
