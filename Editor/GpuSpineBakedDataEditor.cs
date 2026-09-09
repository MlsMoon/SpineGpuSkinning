using GpuSpine.Baking;
using UnityEditor;
using UnityEngine;

namespace GpuSpine.Editor {
    /// <summary>生成数据只读；仅保留皮肤组合声明作为可编辑的烘焙输入。</summary>
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

        /// <summary>允许展开查看嵌套审计与条目，但禁止改值、引用和数组长度。</summary>
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
