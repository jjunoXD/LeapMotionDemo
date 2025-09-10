using Leap;
using UnityEngine;

public class Pinch : MonoBehaviour
{
    [Header("Target")]
    public GameObject obj;
    public GameObject ui;

    [Header("Pinch thresholds")]
    [SerializeField] private float pinchOn = 0.8f;
    [SerializeField] private float pinchOff = 0.7f;

    [Header("Two hand Zoom")]
    [SerializeField] private float zoomDeadzoneMeters = 0.02f; //데드존 확대(2cm)
    [SerializeField] private float zoomGain = 0.35f;           //감도
    [SerializeField] private float distanceSmooth = 10f;        //거리 EMA 스무딩 세기
    [SerializeField] private float maxScaleSpeed = 0.5f;        //초당 최대 스케일 변화량
    [SerializeField] private float minUniformScale = 0.05f;
    [SerializeField] private float maxUniformScale = 0.1f;

    [Header("One hand Rotate")]
    [SerializeField] private float rotateGainDegPerMeter = 200f; //좌우 1m 이동 시 회전 각도
    [SerializeField] private float rotateDeadzoneMeters = 0.01f; //1cm 이하 무시
    [SerializeField] private float rotationSmooth = 12f;          //EMA 스무딩 세기
    [SerializeField] private float rotationMaxSpeedDeg = 180f;    //초당 최대 회전각
    [SerializeField] private float minYawDeg = -170f;             //허용 최소 로컬 Yaw
    [SerializeField] private float maxYawDeg = -6f;               //허용 최대 로컬 Yaw

    private bool isShow = false;

    private bool isPinchingLeft;
    private bool isPinchingRight;
    private bool twoHandZoomActive;
    private float startDistance;
    private float filteredDistance; // EMA된 현재 손거리
    private Vector3 startScale;


    private bool oneHandRotateActive = false;
    private Chirality rotateHand;
    private Vector3 lastPinchPosWorld;
    private float dxEma = 0f; // 카메라 기준 X 이동량 EMA

    void Update()
    {
        var provider = Hands.Provider;
        if (provider == null || obj == null) return;


        var left = provider.GetHand(Chirality.Left);
        var right = provider.GetHand(Chirality.Right);

        if (left != null || right != null)
        {
            if (!isShow)
            {
                ui.gameObject.SetActive(true);
            }
            isShow = true;
        }
        else
        {   
            ui.gameObject.SetActive(false);
            isShow = false;
        }

        UpdatePinchState(left, ref isPinchingLeft);
        UpdatePinchState(right, ref isPinchingRight);

        bool bothPinchingNow = isPinchingLeft && isPinchingRight;

        //한 손 핀치
        if (!bothPinchingNow)
        {
            bool leftOnly = isPinchingLeft && !isPinchingRight;
            bool rightOnly = isPinchingRight && !isPinchingLeft;

            if ((leftOnly || rightOnly) && (left != null || right != null))
            {
                Hand h = leftOnly ? left : right;
                Vector3 p = h.GetPinchPosition();

                if (!oneHandRotateActive)
                {
                    oneHandRotateActive = true;
                    rotateHand = leftOnly ? Chirality.Left : Chirality.Right;
                    lastPinchPosWorld = p;
                    dxEma = 0f;
                }
                else
                {
                    //활성 손 바뀌면 리셋
                    if ((rotateHand == Chirality.Left && !leftOnly) ||
                        (rotateHand == Chirality.Right && !rightOnly))
                    {
                        oneHandRotateActive = false;
                    }
                    else
                    {
                        Vector3 prev = lastPinchPosWorld;
                        lastPinchPosWorld = p;

                        float dt = Mathf.Max(Time.deltaTime, 1e-5f);
                        float dx;
                        var cam = Camera.main;

                        Vector3 prevCam = cam.transform.InverseTransformPoint(prev);
                        Vector3 currCam = cam.transform.InverseTransformPoint(p);
                        dx = currCam.x - prevCam.x; //카메라 기준 가로 이동(미터)

                        if (Mathf.Abs(dx) < rotateDeadzoneMeters) dx = 0f;

                        //이동량 EMA
                        float a = 1f - Mathf.Exp(-rotationSmooth * dt);
                        dxEma = Mathf.Lerp(dxEma, dx, a);

                        // 손과 "반대 방향" 회전: 부호 반전(-)
                        float deltaDeg = -rotateGainDegPerMeter * dxEma;
                        deltaDeg = Mathf.Clamp(deltaDeg, -rotationMaxSpeedDeg * dt, rotationMaxSpeedDeg * dt);

                        obj.transform.Rotate(Vector3.up, deltaDeg, Space.Self);
                    }
                }
            }
            else
            {
                oneHandRotateActive = false;
            }
        }
        else
        {
            oneHandRotateActive = false; // 두 손 핀치(줌)
        }

        // 두 손 핀치 시작: 기준 거리/스케일 설정
        if (bothPinchingNow && !twoHandZoomActive)
        {
            Vector3 lp = left.GetPinchPosition();
            Vector3 rp = right.GetPinchPosition();

            startDistance = Vector3.Distance(lp, rp);
            if (startDistance > Mathf.Epsilon)
            {
                filteredDistance = startDistance;   // EMA 초기화
                startScale = obj.transform.localScale;
                twoHandZoomActive = true;
            }
        }

        if (twoHandZoomActive)
        {
            if (!bothPinchingNow)
            {
                twoHandZoomActive = false;
                return;
            }

            Vector3 lp = left.GetPinchPosition();
            Vector3 rp = right.GetPinchPosition();

            float rawDistance = Vector3.Distance(lp, rp);

            //데드존: 기준에서 너무 미세한 변화면 무시(노이즈 억제)
            if (Mathf.Abs(rawDistance - startDistance) <= zoomDeadzoneMeters)
                return;

            //거리 EMA 스무딩 (지수형 람다)
            float a = 1f - Mathf.Exp(-distanceSmooth * Time.deltaTime);
            filteredDistance = Mathf.Lerp(filteredDistance, rawDistance, a);

            if (startDistance <= Mathf.Epsilon) return;

            //감도 맵핑: ratio^zoomGain (zoomGain<1이면 완만)
            float ratio = Mathf.Max(filteredDistance / startDistance, 1e-4f);
            float ratioMapped = Mathf.Pow(ratio, zoomGain);

            //목표 스케일(균일 스케일) + 범위 제한
            float targetUniform = Mathf.Clamp(startScale.x * ratioMapped, minUniformScale, maxUniformScale);

            //초당 변화율 제한
            float currUniform = obj.transform.localScale.x;
            float maxDelta = maxScaleSpeed * Time.deltaTime;
            float nextUniform = Mathf.Clamp(targetUniform, currUniform - maxDelta, currUniform + maxDelta);

            obj.transform.localScale = new Vector3(nextUniform, nextUniform, nextUniform);
        }
    }

    private void UpdatePinchState(Hand hand, ref bool state)
    {
        if (hand == null) { state = false; return; }
        float s = hand.PinchStrength;
        if (!state && s > pinchOn) state = true;
        else if (state && s < pinchOff) state = false;
    }
}