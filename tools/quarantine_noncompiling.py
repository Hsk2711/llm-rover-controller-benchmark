#!/usr/bin/env python3
"""quarantine_noncompiling.py - keep the Unity project compiling when a chatbot-written controller does not.

Run from the project root while the Unity Editor is open (needs the `unity` command line tool):
    python tools/quarantine_noncompiling.py            # recompile; quarantine and log any LLM file with a compile error
    python tools/quarantine_noncompiling.py --dry-run  # only report what it would do

What it does
  1. Forces a script recompile and reads the compiler errors.
  2. If every error is in a file of Assets/Scripts/Controllers/LLM/ (not Excluded/): each such file is MOVED (not edited, not
     deleted) to LLM_NoCompile/<same name>.cs outside Assets, and its .meta file is removed. Unity ignores that folder, so the
     project compiles again. The study keeps the file as evidence and counts the controller as success=0 "no_compile".
  3. Writes the log: results/llm_generation_log.csv (file LLM_<Model>_<Attempt>_OS -> one_shot_compiled=FALSE; _FB ->
     final_compiled=FALSE; a row is created if there is none), with the first compiler error in the notes.
  4. Recompiles and confirms.
If an error is anywhere else (not an LLM file) NOTHING is moved: that is a project problem, not a study result.
"""
import csv, io, json, os, re, shutil, subprocess, sys, time

ROOT = os.getcwd()
LLM_DIR = os.path.join("Assets", "Scripts", "Controllers", "LLM")
QUARANTINE = "LLM_NoCompile"
LOG = os.path.join("results", "llm_generation_log.csv")
ERR = re.compile(r"^(Assets[\\/].*?\.cs)\((\d+),(\d+)\):\s*error\s+(CS\d+):\s*(.*)$")
NAME = re.compile(r"^LLM_([A-Za-z0-9]+)_([0-9]+)_(OS|FB)$")
COLUMNS = ["model", "model_version", "date", "attempt", "one_shot_compiled", "feedback_rounds", "final_compiled",
           "final_quicktest_score", "human_minutes", "notes"]


def unity(*args):
    exe = shutil.which("unity")
    if not exe:
        sys.exit("the 'unity' command line tool was not found on PATH")
    out = subprocess.run([exe, "command", "--project-path", ".", *args], capture_output=True, cwd=ROOT)
    return (out.stdout or b"").decode("utf-8", "replace") + (out.stderr or b"").decode("utf-8", "replace")


def recompile():
    """Force a recompile and return (failed, [error strings])."""
    unity("script", "recompile")
    time.sleep(8)
    last = None
    for _ in range(90):
        raw = unity("script", "recompile_status", "--result-only")
        try:
            last = json.loads(raw[raw.index("{"):raw.rindex("}") + 1])
        except ValueError:
            last = None
        if last and last.get("status") in ("completed", "up_to_date"):
            return bool(last.get("failed") or last.get("compilationFailed")), list(last.get("errors", []))
        time.sleep(4)
    sys.exit("timed out waiting for the Unity recompile to finish")


def update_log(file_name, first_error):
    m = NAME.match(file_name)
    if not m:
        print("  (not logged: '%s' does not match LLM_<Model>_<Attempt>_<OS|FB>)" % file_name)
        return
    model, attempt, cond = m.groups()
    with open(LOG, encoding="utf-8", newline="") as f:
        text = f.read()
    nl = "\r\n" if "\r\n" in text else "\n"
    rows = list(csv.reader(io.StringIO(text)))
    note = ("Did NOT compile (%s). %s moved unedited to LLM_NoCompile/ (outside Assets) by tools/quarantine_noncompiling.py; "
            "counted by the experiment as success=0 no_compile.") % (first_error, file_name + ".cs")
    target = None
    for r in rows[1:]:
        if len(r) == len(COLUMNS) and r[0] == model and r[3] == attempt and not r[9].startswith("excluded"):
            target = r
    if target is None:
        target = [model, "", time.strftime("%Y-%m-%d"), attempt, "", "0", "", "", "", ""]
        rows.append(target)
    if cond == "OS":
        target[4] = "FALSE"
    else:
        target[6] = "FALSE"
    if (file_name + ".cs moved") not in target[9]:
        target[9] = (target[9] + " | " if target[9] else "") + note
    out = io.StringIO()
    csv.writer(out, lineterminator=nl).writerows(rows)
    with open(LOG, "w", encoding="utf-8", newline="") as f:
        f.write(out.getvalue())


def main():
    dry = "--dry-run" in sys.argv
    if not os.path.isdir(LLM_DIR):
        sys.exit("run this from the project root (folder with Assets/)")
    failed, errors = recompile()
    if not failed:
        print("The project compiles. Nothing to do.")
        return 0
    files = {}
    others = []
    for e in errors:
        m = ERR.match(e.strip())
        if not m:
            others.append(e)
            continue
        path = m.group(1).replace("\\", "/")
        in_llm = path.startswith(LLM_DIR.replace("\\", "/") + "/") and "/Excluded/" not in path
        if in_llm:
            files.setdefault(path, "%s(%s,%s) error %s: %s" % (os.path.basename(path), m.group(2), m.group(3), m.group(4), m.group(5)))
        else:
            others.append(e)
    if others or not files:
        print("Compile errors that are NOT in an LLM controller file - nothing was moved:")
        for e in others or errors:
            print("  " + e)
        return 2
    print("%d LLM file(s) with compile errors:" % len(files))
    for path, first in sorted(files.items()):
        print("  %s\n    %s" % (path, first))
    if dry:
        print("--dry-run: nothing moved.")
        return 0
    os.makedirs(QUARANTINE, exist_ok=True)
    for path, first in sorted(files.items()):
        name = os.path.splitext(os.path.basename(path))[0]
        dest = os.path.join(QUARANTINE, name + ".cs")
        if os.path.exists(dest):
            print("  refusing to overwrite existing evidence file " + dest)
            return 3
        shutil.move(path, dest)
        if os.path.exists(path + ".meta"):
            os.remove(path + ".meta")
        update_log(name, first)
        print("  moved -> " + dest + " and logged")
    failed, errors = recompile()
    print("After moving, the project %s." % ("STILL DOES NOT COMPILE: " + "; ".join(errors[:3]) if failed else "compiles"))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
