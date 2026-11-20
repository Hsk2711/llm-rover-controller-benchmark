// ControllerRegistry: finds every controller class in the project by itself (any non-abstract class that implements
// IRoverController and has a parameterless constructor) and creates them by class name. A new file in
// Assets/Scripts/Controllers/LLM/ shows up in the ControllerHost dropdown, the Quick Test tool and the experiment runner
// with no registration step. Each controller has a Kind: Baseline (the two frozen reference controllers), Llm (class name
// starts with "LLM_") or Utility (Manual, Stop). The experiment runner takes Baselines + Llm; Utility is for testing only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

public enum ControllerKind { Baseline, Llm, Utility }

public static class ControllerRegistry
{
    /// <summary>Required shape of an LLM controller class (= file) name: LLM_&lt;Model&gt;_&lt;Attempt&gt;_&lt;OS|FB&gt;, e.g. LLM_Claude_3_OS.</summary>
    public static readonly Regex LlmNamePattern = new Regex(@"^LLM_[A-Za-z0-9]+_[0-9]+_(OS|FB)$");

    static readonly string[] BaselineNames = { "RuleBasedController", "Bug2Controller" };

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
        if (className.StartsWith("LLM_", StringComparison.Ordinal)) return ControllerKind.Llm;
        if (Array.IndexOf(BaselineNames, className) >= 0) return ControllerKind.Baseline;
        return ControllerKind.Utility;
    }

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
