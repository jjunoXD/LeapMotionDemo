using UnityEngine;

public class Change : MonoBehaviour
{
    public bool boolVar = true;
    public int intVar = 100;
    [Range(0f, 1f)] public float floatVar = 0.1f; // �����̴� ����
    public int[] arrayVar;

    public CustomStruct structVar;

    [System.Serializable]         // �� �߿�
    public struct CustomStruct
    {
        public string stringVar;
    }
}
