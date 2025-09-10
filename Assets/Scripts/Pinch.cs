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
    [SerializeField] private float zoomDeadzoneMeters = 0.02f; //������ Ȯ��(2cm)
    [SerializeField] private float zoomGain = 0.35f;           //����
    [SerializeField] private float distanceSmooth = 10f;        //�Ÿ� EMA ������ ����
    [SerializeField] private float maxScaleSpeed = 0.5f;        //�ʴ� �ִ� ������ ��ȭ��
    [SerializeField] private float minUniformScale = 0.05f;
    [SerializeField] private float maxUniformScale = 0.1f;

    [Header("One hand Rotate")]
    [SerializeField] private float rotateGainDegPerMeter = 200f; //�¿� 1m �̵� �� ȸ�� ����
    [SerializeField] private float rotateDeadzoneMeters = 0.01f; //1cm ���� ����
    [SerializeField] private float rotationSmooth = 12f;          //EMA ������ ����
    [SerializeField] private float rotationMaxSpeedDeg = 180f;    //�ʴ� �ִ� ȸ����
    [SerializeField] private float minYawDeg = -170f;             //��� �ּ� ���� Yaw
    [SerializeField] private float maxYawDeg = -6f;               //��� �ִ� ���� Yaw

    private bool isShow = false;

    private bool isPinchingLeft;
    private bool isPinchingRight;
    private bool twoHandZoomActive;
    private float startDistance;
    private float filteredDistance; // EMA�� ���� �հŸ�
    private Vector3 startScale;


    private bool oneHandRotateActive = false;
    private Chirality rotateHand;
    private Vector3 lastPinchPosWorld;
    private float dxEma = 0f; // ī�޶� ���� X �̵��� EMA

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

        //�� �� ��ġ
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
                    //Ȱ�� �� �ٲ�� ����
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
                        dx = currCam.x - prevCam.x; //ī�޶� ���� ���� �̵�(����)

                        if (Mathf.Abs(dx) < rotateDeadzoneMeters) dx = 0f;

                        //�̵��� EMA
                        float a = 1f - Mathf.Exp(-rotationSmooth * dt);
                        dxEma = Mathf.Lerp(dxEma, dx, a);

                        // �հ� "�ݴ� ����" ȸ��: ��ȣ ����(-)
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
            oneHandRotateActive = false; // �� �� ��ġ(��)
        }

        // �� �� ��ġ ����: ���� �Ÿ�/������ ����
        if (bothPinchingNow && !twoHandZoomActive)
        {
            Vector3 lp = left.GetPinchPosition();
            Vector3 rp = right.GetPinchPosition();

            startDistance = Vector3.Distance(lp, rp);
            if (startDistance > Mathf.Epsilon)
            {
                filteredDistance = startDistance;   // EMA �ʱ�ȭ
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

            //������: ���ؿ��� �ʹ� �̼��� ��ȭ�� ����(������ ����)
            if (Mathf.Abs(rawDistance - startDistance) <= zoomDeadzoneMeters)
                return;

            //�Ÿ� EMA ������ (������ ����)
            float a = 1f - Mathf.Exp(-distanceSmooth * Time.deltaTime);
            filteredDistance = Mathf.Lerp(filteredDistance, rawDistance, a);

            if (startDistance <= Mathf.Epsilon) return;

            //���� ����: ratio^zoomGain (zoomGain<1�̸� �ϸ�)
            float ratio = Mathf.Max(filteredDistance / startDistance, 1e-4f);
            float ratioMapped = Mathf.Pow(ratio, zoomGain);

            //��ǥ ������(���� ������) + ���� ����
            float targetUniform = Mathf.Clamp(startScale.x * ratioMapped, minUniformScale, maxUniformScale);

            //�ʴ� ��ȭ�� ����
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