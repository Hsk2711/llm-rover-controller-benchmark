// LlmFileCheck: Editor menu "Rover > Check LLM Files". READ-ONLY audit of every .cs file in Assets/Scripts/Controllers/LLM/.
// It never edits a file (LLM controllers are study subjects). For each file it reports: file name follows
// LLM_<Model>_<Attempt>_<OS|FB>; the class name equals the file name (not still "LLM_Controller"); a use of roverPos, startPos or
// goalPos (the harness zeroes them for LLM controllers, but the use is still reported for the paper); forbidden API words from the
// task spec (UnityEngine.Random, Physics, GameObject, Transform, Time, Find...); and re-declared Observation / IRoverController.
// The text search is a simple scan after removing comments and string literals; it points at lines to read, it is not a parser.
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class LlmFileCheck
{
    static readonly (string label, Regex rx)[] Rules =
    {
        ("uses rover/start/goal POSITION field (zeroed by the harness)", new Regex(@"\b(roverPos|startPos|goalPos)\b")),
        ("UnityEngine.Random / Random.", new Regex(@"\bRandom\s*\.|UnityEngine\s*\.\s*Random|new\s+Random\b|System\s*\.\s*Random")),
        ("Physics", new Regex(@"\bPhysics2?D?\b")),
        ("GameObject / Transform / Find", new Regex(@"\b(GameObject|Transform|FindObject\w*|FindWithTag|MonoBehaviour|Rigidbody)\b")),
        ("Time (Time.time / deltaTime / DateTime / Stopwatch)", new Regex(@"\bTime\s*\.|\bDateTime\b|\bStopwatch\b|Environment\s*\.\s*TickCount")),
        ("re-declares Observation or IRoverController", new Regex(@"\b(struct|class|interface)\s+(Observation|IRoverController)\b")),
    };

    [MenuItem("Rover/Check LLM Files")]
    public static void Menu()
    {
        string folder = Path.Combine(Application.dataPath, "Scripts", "Controllers", "LLM");
        Debug.Log(Scan(folder));
    }

    /// <summary>Report for every .cs file in the folder (nothing is modified).</summary>
    public static string Scan(string folder)
    {
        var sb = new StringBuilder();
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.cs") : new string[0];
        System.Array.Sort(files, System.StringComparer.Ordinal);
        int flagged = 0;
        sb.AppendLine("LLM file check (read-only): " + files.Length + " file(s) in " + folder);
        foreach (string path in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var issues = new StringBuilder();
            if (!ControllerRegistry.LlmNamePattern.IsMatch(name)) issues.AppendLine("    file name does not match LLM_<Model>_<Attempt>_<OS|FB>");

            string raw = File.ReadAllText(path);
            string code = StripCommentsAndStrings(raw);
            var classes = Regex.Matches(code, @"\bclass\s+(\w+)");
            bool hasOwn = false;
            foreach (Match m in classes) if (m.Groups[1].Value == name) hasOwn = true;
            if (!hasOwn)
            {
                string found = classes.Count > 0 ? classes[0].Groups[1].Value : "(no class)";
                issues.AppendLine("    class name is '" + found + "', file name is '" + name + "' (class must be renamed to the file name)");
            }
            if (classes.Count != 1) issues.AppendLine("    " + classes.Count + " class declarations (the spec asks for exactly one class)");

            string[] lines = code.Split('\n');
            foreach (var (label, rx) in Rules)
            {
                var hits = new StringBuilder();
                for (int i = 0; i < lines.Length; i++) if (rx.IsMatch(lines[i])) hits.Append(hits.Length > 0 ? "," : "").Append(i + 1);
                if (hits.Length > 0) issues.AppendLine("    " + label + ": line(s) " + hits);
            }
            if (issues.Length == 0) sb.AppendLine("  OK   " + name);
            else { flagged++; sb.AppendLine("  FLAG " + name); sb.Append(issues); }
        }
        // Excluded attempts (Excluded/ subfolder): they must be named ..._excluded in file AND class, otherwise the registry would
        // treat them as study controllers. Listed for information only.
        string exFolder = Path.Combine(folder, "Excluded");
        if (Directory.Exists(exFolder))
        {
            foreach (string path in Directory.GetFiles(exFolder, "*.cs"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string code = StripCommentsAndStrings(File.ReadAllText(path));
                bool classOk = Regex.IsMatch(code, @"\bclass\s+" + Regex.Escape(name) + @"\b") && name.EndsWith(ControllerRegistry.ExcludedSuffix);
                if (classOk) sb.AppendLine("  excluded (ignored by the study): " + name);
                else { flagged++; sb.AppendLine("  FLAG Excluded/" + name + ": file and class must end with _excluded and match each other, else the registry would use it"); }
            }
        }
        sb.AppendLine(flagged == 0 ? "No file flagged." : flagged + " file(s) flagged. Nothing was changed; report these, do not fix them.");
        return sb.ToString();
    }

    // Replace comments and string/char literals by blanks (keeping newlines) so words inside them do not count.
    static string StripCommentsAndStrings(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i], n = i + 1 < s.Length ? s[i + 1] : '\0';
            if (c == '/' && n == '/') { while (i < s.Length && s[i] != '\n') i++; }
            else if (c == '/' && n == '*')
            {
                i += 2;
                while (i < s.Length && !(s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')) { if (s[i] == '\n') sb.Append('\n'); i++; }
                i += 2;
            }
            else if (c == '@' && n == '"')
            {
                i += 2;
                while (i < s.Length) { if (s[i] == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; } break; } if (s[i] == '\n') sb.Append('\n'); i++; }
                i++;
            }
            else if (c == '"' || c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != c && s[i] != '\n') { if (s[i] == '\\') i++; i++; }
                i++;
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString();
    }
}
