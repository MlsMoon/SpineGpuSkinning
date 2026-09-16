using GpuSpine.Baking;
using UnityEditor;
using UnityEngine;

namespace GpuSpine.Editor {
    /// <summary>Generated fields are read-only. Only DeclaredCombos stay editable as a bake input.</summary>
    [CustomEditor(typeof(GpuSpineBakedData))]
    public sealed class GpuSpineBakedDataEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            serializedObject.Update();
            EditorGUILayout.HelpBox("Baked output is read-only. Edit the source skeleton or skin combinations, then rebake.", MessageType.Info);
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enter = true;
            while (iterator.NextVisible(enter)) {
                enter = false;
                if (iterator.name == nameof(GpuSpineBakedData.DeclaredCombos)) continue;
                DrawReadOnly(iterator.Copy());
            }
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode)) {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(GpuSpineBakedData.DeclaredCombos)), true);
                serializedObject.ApplyModifiedProperties();
                var data = (GpuSpineBakedData)target;
                using (new EditorGUI.DisabledScope(data.SourceAsset == null)) {
                    if (GUILayout.Button("Rebake from source")) GpuSpineBakerEditorUtility.Rebake(data.SourceAsset);
                }
            }
        }

        /// <summary>Allow expanding nested audit and entries, but block value, reference, and array-size edits.</summary>
        static void DrawReadOnly(SerializedProperty property) {
            if (!property.hasVisibleChildren || property.propertyType == SerializedPropertyType.ObjectReference) {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(property, false);
                return;
            }
            property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, property.displayName, true);
            if (!property.isExpanded) return;
            EditorGUI.indentLevel++;
            SerializedProperty child = property.Copy();
            SerializedProperty end = child.GetEndProperty();
            if (child.NextVisible(true)) {
                do {
                    if (SerializedProperty.EqualContents(child, end)) break;
                    DrawReadOnly(child.Copy());
                } while (child.NextVisible(false));
            }
            EditorGUI.indentLevel--;
        }
    }
}
