using UnityEditor;
using UnityEngine;

#if UNITY_EDITOR
[CustomEditor(typeof(Change))] //CustomClass�� Ŀ���� Inspector�� ǥ���� ��
public class ChangeEditor : Editor
{
    // 1) ĳ���� ������Ƽ��
    SerializedProperty boolVarProp;
    SerializedProperty intVarProp;
    SerializedProperty floatVarProp;
    SerializedProperty arrayVarProp;
    SerializedProperty structVarProp;

    void OnEnable()
    {
        // 2) OnEnable���� �� ���� FindProperty
        boolVarProp = serializedObject.FindProperty("boolVar");
        intVarProp = serializedObject.FindProperty("intVar");
        floatVarProp = serializedObject.FindProperty("floatVar");
        arrayVarProp = serializedObject.FindProperty("arrayVar");
        structVarProp = serializedObject.FindProperty("structVar");
    }

    public override void OnInspectorGUI()
    {
        // 3) �ֽ� ���� ����ȭ
        serializedObject.Update();

        // 4) �׸���
        EditorGUILayout.LabelField("Basic Fields", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(boolVarProp, new GUIContent("Bool Var"));
        EditorGUILayout.PropertyField(intVarProp, new GUIContent("Int Var"));
        EditorGUILayout.PropertyField(floatVarProp, new GUIContent("Float Var"));

        EditorGUILayout.Space(8);

        // �迭: true => children����(��ҵ�) �Բ� ������
        EditorGUILayout.LabelField("Array (int[])", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(arrayVarProp, new GUIContent("Array Var"), true);

        EditorGUILayout.Space(8);

        // ����ü: true�� �ϸ� ���� �ʵ�� �ڵ� ǥ��
        EditorGUILayout.LabelField("Custom Struct", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(structVarProp, new GUIContent("Struct Var"), true);

        EditorGUILayout.Space(8);

        // ����: ��ư �ϳ� �߰�
        if (GUILayout.Button("Print Values to Console"))
        {
            foreach (var t in targets)
            {
                var comp = (Change)t;
                Debug.Log($"bool={comp.boolVar}, int={comp.intVar}, float={comp.floatVar}, struct.string={comp.structVar.stringVar}");
            }
        }

        // 5) ������� �ݿ�(Undo/Prefab override ����)
        serializedObject.ApplyModifiedProperties();
    }
}
#endif