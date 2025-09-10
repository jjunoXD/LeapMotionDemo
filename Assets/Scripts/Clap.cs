using Leap;
using UnityEngine;

public class Clap : MonoBehaviour
{
    [Header("Target")]
    public GameObject[] target;

    [Header("Clap Detection")]
    [SerializeField] private float clapDistance = 0.08f;        //손바닥 사이 거리 임계값
    [SerializeField] private float minApproachSpeed = 0.6f;     //접근 속도 임계값(m/s) — 빠르게 붙을 때만
    [SerializeField] private float maxPalmFacingAngle = 55f;    //손바닥이 서로 마주보는 최대 각
    [SerializeField] private float cooldown = 0.35f;            //토글 쿨다운
    [SerializeField] private float speedSmooth = 8f;            //속도 EMA 스무딩 세기

    private float prevPalmDistance = -1f;   //이전 프레임 손바닥 거리
    private float approachSpeedEma = 0f;    //접근 속도 EMA
    private float lastClapTime = -999f;     //마지막 토글 시각

    void Update()
    {
        if (target == null) return;

        var provider = Hands.Provider;
        if (provider == null) return;

        var left = provider.GetHand(Chirality.Left);
        var right = provider.GetHand(Chirality.Right);
        if (left == null || right == null)
        {
            //한 손이라도 없으면 측정 리셋
            prevPalmDistance = -1f;
            approachSpeedEma = 0f;
            return;
        }

        //손바닥 위치·노멀
        Vector3 lpPalm = left.PalmPosition;
        Vector3 rpPalm = right.PalmPosition;

        float currDistance = Vector3.Distance(lpPalm, rpPalm);

        //접근 속도(+) = 손바닥끼리 가까워지는 속도
        float dt = Mathf.Max(Time.deltaTime, 1e-5f);
        if (prevPalmDistance < 0f) prevPalmDistance = currDistance;

        float instApproachSpeed = (prevPalmDistance - currDistance) / dt; //m/s
        float k = 1f - Mathf.Exp(-speedSmooth * dt);                      //EMA 계수
        approachSpeedEma = Mathf.Lerp(approachSpeedEma, instApproachSpeed, k);
        prevPalmDistance = currDistance;

        //손바닥이 서로 '마주보고' 있는지(팜 노멀 반대방향)
        bool palmsFacing = PalmsFacing(left, right, maxPalmFacingAngle);

        //쿨다운 후에만 토글 허용
        bool canToggle = (Time.time - lastClapTime) > cooldown;

        //토글 조건: (1) 마주봄 (2) 충분히 가까움 (3) 충분히 빠른 접근
        if (canToggle &&
            palmsFacing &&
            currDistance <= clapDistance &&
            approachSpeedEma >= minApproachSpeed)
        {
            foreach(var obj in target)
            {
                obj.SetActive(!obj.activeSelf);
            }
            lastClapTime = Time.time;
        }
    }

    //팜 노멀이 서로 반대방향(≈마주봄)인지 체크
    private bool PalmsFacing(Hand l, Hand r, float maxAngleDeg)
    {
        Vector3 ln = l.PalmNormal.normalized;
        Vector3 rn = r.PalmNormal.normalized;

        //ln · (-rn) = cos(theta)  (theta가 작을수록 마주봄)
        float facing = Vector3.Dot(ln, -rn);
        float cosMax = Mathf.Cos(maxAngleDeg * Mathf.Deg2Rad);
        return facing >= cosMax;
    }
}