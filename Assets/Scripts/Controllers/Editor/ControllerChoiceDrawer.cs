// ControllerChoiceDrawer: Editor-only. Draws a string field marked [ControllerChoice] as a dropdown listing every controller
// class found by ControllerFactory. If the stored name no longer exists it is shown as "(missing)".
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(ControllerChoiceAttribute))]
public class ControllerChoiceDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.String)
        {
            EditorGUI.LabelField(position, label.text, "[ControllerChoice] needs a string field");
            return;
        }

        var names = ControllerFactory.Names().ToList();
        string current = property.stringValue;
        int index = names.IndexOf(current);
        if (index < 0)
        {
            names.Insert(0, "(missing) " + current);
            index = 0;
        }

        EditorGUI.BeginChangeCheck();
        int picked = EditorGUI.Popup(position, label.text, index, names.ToArray());
        if (EditorGUI.EndChangeCheck() && !names[picked].StartsWith("(missing)"))
            property.stringValue = names[picked];
    }
}
