// ControllerFactory: finds every controller class in the project (any non-abstract class that implements IRoverController
// and has a parameterless constructor) and creates them by class name. This is what fills the Inspector dropdown, so a new
// controller file appears in the list by itself, with no registration step.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class ControllerFactory
{
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

    /// <summary>A new instance of the controller class with this name, or null if there is none.</summary>
    public static IRoverController Create(string className)
    {
        var type = Types().FirstOrDefault(t => t.Name == className);
        if (type == null)
        {
            Debug.LogError("ControllerFactory: no controller class named '" + className + "'");
            return null;
        }
        return (IRoverController)Activator.CreateInstance(type);
    }
}
