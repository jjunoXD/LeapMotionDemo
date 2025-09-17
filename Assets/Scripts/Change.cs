using UnityEngine;

public class Change : MonoBehaviour
{
    public bool boolVar = true;
    public int intVar = 100;
    [Range(0f, 1f)] public float floatVar = 0.1f; // 슬라이더 예시
    public int[] arrayVar;

    public CustomStruct structVar;

    [System.Serializable]         // ★ 중요
    public struct CustomStruct
    {
        public string stringVar;
    }
}
