using UnityEditor;
using UnityEngine;

#if UNITY_EDITOR
[CustomEditor(typeof(Change))] //CustomClass는 커스텀 Inspector로 표시할 것
public class ChangeEditor : Editor
{
    // 1) 캐싱할 프로퍼티들
    SerializedProperty boolVarProp;
    SerializedProperty intVarProp;
    SerializedProperty floatVarProp;
    SerializedProperty arrayVarProp;
    SerializedProperty structVarProp;

    void OnEnable()
    {
        // 2) OnEnable에서 한 번만 FindProperty
        boolVarProp = serializedObject.FindProperty("boolVar");
        intVarProp = serializedObject.FindProperty("intVar");
        floatVarProp = serializedObject.FindProperty("floatVar");
        arrayVarProp = serializedObject.FindProperty("arrayVar");
        structVarProp = serializedObject.FindProperty("structVar");
    }

    public override void OnInspectorGUI()
    {
        // 3) 최신 상태 동기화
        serializedObject.Update();

        // 4) 그리기
        EditorGUILayout.LabelField("Basic Fields", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(boolVarProp, new GUIContent("Bool Var"));
        EditorGUILayout.PropertyField(intVarProp, new GUIContent("Int Var"));
        EditorGUILayout.PropertyField(floatVarProp, new GUIContent("Float Var"));

        EditorGUILayout.Space(8);

        // 배열: true => children까지(요소들) 함께 렌더링
        EditorGUILayout.LabelField("Array (int[])", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(arrayVarProp, new GUIContent("Array Var"), true);

        EditorGUILayout.Space(8);

        // 구조체: true로 하면 내부 필드들 자동 표시
        EditorGUILayout.LabelField("Custom Struct", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(structVarProp, new GUIContent("Struct Var"), true);

        EditorGUILayout.Space(8);

        // 예시: 버튼 하나 추가
        if (GUILayout.Button("Print Values to Console"))
        {
            foreach (var t in targets)
            {
                var comp = (Change)t;
                Debug.Log($"bool={comp.boolVar}, int={comp.intVar}, float={comp.floatVar}, struct.string={comp.structVar.stringVar}");
            }
        }

        // 5) 변경사항 반영(Undo/Prefab override 포함)
        serializedObject.ApplyModifiedProperties();
    }
}
#endif