// ControllerRegistry: finds every controller class in the project by itself (any non-abstract class that implements
// IRoverController and has a parameterless constructor) and creates them by class name. A new file in
// Assets/Scripts/Controllers/LLM/ shows up in the ControllerHost dropdown, the Quick Test tool and the experiment runner
// with no registration step. Each controller has a Kind: Baseline (the two frozen reference controllers), Llm (class name
// starts with "LLM_") or Utility (Manual, Stop). The experiment runner takes Baselines + Llm; Utility is for testing only.
// Files in Assets/Scripts/Controllers/LLM/Excluded/ are named LLM_..._excluded (attempts removed from the study, e.g. a protocol
// deviation): their class name ends with "_excluded", they get Kind Excluded and are NEVER in ExperimentNames().
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

public enum ControllerKind { Baseline, Llm, Utility, Excluded }

public static class ControllerRegistry
{
    /// <summary>Required shape of an LLM controller class (= file) name: LLM_&lt;Model&gt;_&lt;Attempt&gt;_&lt;OS|FB&gt;, e.g. LLM_Claude_3_OS.</summary>
    public static readonly Regex LlmNamePattern = new Regex(@"^LLM_[A-Za-z0-9]+_[0-9]+_(OS|FB)$");

    public const string ExcludedSuffix = "_excluded";

    static readonly string[] BaselineNames = { "RuleBasedController", "Bug2Controller", "Bug2_Corrected" };   // Bug2_Corrected added in Phase 7 (family Bug2C)

    static List<Type> types;

    static List<Type> Types()
    {
        if (types == null)
        {
            types = typeof(IRoverController).Assembly.GetTypes()
                .Where(t => typeof(IRoverController).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract
                            && t != typeof(SafeController) && t.GetConstructor(Type.EmptyTypes) != null)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }
        return types;
    }

    /// <summary>Class names of all available controllers, sorted.</summary>
    public static string[] Names() => Types().Select(t => t.Name).ToArray();

    /// <summary>Class names of one kind, sorted.</summary>
    public static string[] Names(ControllerKind kind) => Types().Where(t => KindOf(t.Name) == kind).Select(t => t.Name).ToArray();

    /// <summary>Names of the controllers the experiment uses: the two baselines, then all LLM controllers.</summary>
    public static string[] ExperimentNames() => Names(ControllerKind.Baseline).Concat(Names(ControllerKind.Llm)).ToArray();

    public static ControllerKind KindOf(string className)
    {
        if (className.EndsWith(ExcludedSuffix, StringComparison.Ordinal)) return ControllerKind.Excluded;
        if (className.StartsWith("LLM_", StringComparison.Ordinal)) return ControllerKind.Llm;
        if (Array.IndexOf(BaselineNames, className) >= 0) return ControllerKind.Baseline;
        return ControllerKind.Utility;
    }

    // ---- LLM files that did not compile ----
    // A file that does not compile would block the whole project, so tools/quarantine_noncompiling.py moves it OUT of Assets to
    // <project>/LLM_NoCompile/ (Unity ignores it there; kept as evidence). Such a controller is still part of the study: the
    // experiment runner must count it as success = 0 with outcome "no_compile" in every trial (never skip it, never fix it).
    public const string NoCompileOutcome = "no_compile";

    /// <summary>Folder holding the LLM files that did not compile (outside Assets).</summary>
    public static string NoCompileFolder => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "LLM_NoCompile");

    /// <summary>Class names (= file names) of the LLM controllers that did not compile, sorted.</summary>
    public static string[] NoCompileNames()
    {
        if (!System.IO.Directory.Exists(NoCompileFolder)) return new string[0];
        return System.IO.Directory.GetFiles(NoCompileFolder, "LLM_*.cs").Select(System.IO.Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Everything the experiment must report on: (name, true) = runs normally, (name, false) = did not compile (record success 0, outcome "no_compile").</summary>
    public static (string name, bool compiles)[] ExperimentEntries() =>
        ExperimentNames().Select(n => (n, true)).Concat(NoCompileNames().Select(n => (n, false))).ToArray();

    /// <summary>LLM controller classes whose name does not follow LLM_&lt;Model&gt;_&lt;Attempt&gt;_&lt;OS|FB&gt; (to be reported, never fixed by us).</summary>
    public static string[] MisnamedLlmControllers() => Names(ControllerKind.Llm).Where(n => !LlmNamePattern.IsMatch(n)).ToArray();

    public static bool Exists(string className) => Types().Any(t => t.Name == className);

    /// <summary>A new instance of the controller class with this name, or null if there is none.</summary>
    public static IRoverController Create(string className)
    {
        var type = Types().FirstOrDefault(t => t.Name == className);
        if (type == null)
        {
            Debug.LogError("ControllerRegistry: no controller class named '" + className + "'");
            return null;
        }
        return (IRoverController)Activator.CreateInstance(type);
    }
}
