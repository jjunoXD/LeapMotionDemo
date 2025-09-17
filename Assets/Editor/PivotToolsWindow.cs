#if UNITY_EDITOR
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using System.IO;

public class PivotPreviewWindow : EditorWindow
{
    // ---------- UI 옵션 ----------
    private enum PivotMode { GeometricCenter, BottomCenter }         // 피벗 모드
    private enum ApplyMode { BakeIntoMesh, ParentPivot }             // 적용 방식

    [MenuItem("Tools/Pivot Tools")]
    public static void Open() => GetWindow<PivotPreviewWindow>("Pivot Tools");

    [SerializeField] private GameObject target; // 씬 오브젝트 선택
    private PivotMode pivotMode = PivotMode.GeometricCenter;
    private ApplyMode applyMode = ApplyMode.BakeIntoMesh;
    private bool includeInactiveChildren = true;
    private bool parentPivotForSkinned = true;   // 스키닝은 부모 피벗으로 대체 적용

    // ---------- 프리뷰 카메라 ----------
    private PreviewRenderUtility preview;
    private GameObject previewRoot;      // 타겟 복제본(프리뷰 전용)
    private Bounds previewBounds;
    private float yaw = -30f, pitch = 20f, distance = 3f;
    private Vector3 pan = Vector3.zero;

    // ---------- 시각화 마커 ----------
    private GameObject markerCurrentPivot, markerNewPivot, markerLink; // 빨강/초록/연결선
    private Material matRed, matGreen, matGray;

    // ---------- 계산 결과(프리뷰용) ----------
    private Vector3 proposedWorldPivot = Vector3.zero; // 초록 구슬 위치
    private bool hasValidPivot = false;

    // ---------- 프레임 일시 객체(바운딩 박스 라인 등) ----------
    private readonly List<GameObject> transientGOs = new List<GameObject>();

    private bool enablePreviewTransparency = true;   //  투명 프리뷰 사용 여부
    private float previewOpacity = 0.35f;            //  0.05 ~ 1.0 권장
    private float lastAppliedOpacity = -1f;          //  중복 적용 방지
    private bool lastTransparencyEnabled = false;    //  토글 상태 캐시
    private readonly Dictionary<Renderer, Material[]> previewMatCopies = new Dictionary<Renderer, Material[]>();
    private bool previewMatsPrepared = false;

    // ---------- 라이프사이클 ----------
    private void OnEnable()
    {
        CreateMaterials();
        SetupPreview();
        Selection.selectionChanged += OnSelectionChanged;
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
        CleanupPreview();
        CleanupMaterials();
    }

    private void OnSelectionChanged()
    {
        if (Selection.activeGameObject != null)
        {
            target = Selection.activeGameObject;
            RebuildPreviewRoot();
            ReframeCamera();
            ApplyPreviewOpacity(true);
            RecomputePivot();
            ApplyPreviewOpacity(true);
            Repaint();
        }
    }

    // ---------- 머티리얼 ----------
    private void CreateMaterials()
    {
        matRed = MakeColorMat(Color.red);
        matGreen = MakeColorMat(Color.green);
        matGray = MakeColorMat(new Color(0.6f, 0.6f, 0.6f, 0.9f));
    }

    private Material MakeColorMat(Color c)
    {
        Shader s = Shader.Find("Unlit/Color");
        if (s == null) s = Shader.Find("Standard"); // Fallback
        var m = new Material(s);
        if (s.name == "Standard") m.SetColor("_Color", c);
        else m.color = c;
        return m;
    }

    private void CleanupMaterials()
    {
        DestroyImmediate(matRed);
        DestroyImmediate(matGreen);
        DestroyImmediate(matGray);
    }

    // ---------- PreviewRenderUtility ----------
    private void SetupPreview()
    {
        if (preview != null) return;
        preview = new PreviewRenderUtility(true);
#if UNITY_2020_1_OR_NEWER
        preview.cameraFieldOfView = 30f;
#else
        preview.camera.fieldOfView = 30f;
#endif
        // 조명/앰비언트
        var light = preview.lights[0];
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.transform.rotation = Quaternion.Euler(50f, 30f, 0f);
        try { preview.ambientColor = new Color(0.35f, 0.35f, 0.35f, 1f); } catch { }
    }

    private void CleanupPreview()
    {
        foreach (var go in transientGOs) DestroyGO(go);
        transientGOs.Clear();

        foreach (var kv in previewMatCopies)
        {
            var mats = kv.Value;
            if (mats == null) continue;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i]) DestroyImmediate(mats[i]);
        }
        previewMatCopies.Clear();
        previewMatsPrepared = false;

        DestroyGO(previewRoot); previewRoot = null;
        DestroyGO(markerCurrentPivot); markerCurrentPivot = null;
        DestroyGO(markerNewPivot); markerNewPivot = null;
        DestroyGO(markerLink); markerLink = null;

        preview?.Cleanup();
        preview = null;
    }

    // ---------- 프리뷰 씬 오브젝트 등록/정리 ----------
    private void RegisterGO(GameObject go)
    {
        if (go == null || preview == null) return;
        preview.AddSingleGO(go);
    }

    private void DestroyGO(GameObject go)
    {
        if (go == null) return;
        DestroyImmediate(go);
    }

    // ---------- 프리뷰 복제본 구성 ----------
    private void RebuildPreviewRoot()
    {
        DestroyGO(previewRoot);
        previewRoot = null;

        if (target == null) return;

        previewRoot = Instantiate(target);
        previewRoot.name = $"{target.name}_PreviewClone";
        StripUnsupportedComponents(previewRoot);
        RegisterGO(previewRoot);

        PreparePreviewMaterials();
        ApplyPreviewOpacity(force: true);

        previewBounds = GetCombinedWorldBounds(previewRoot, includeInactiveChildren);
        if (previewBounds.size == Vector3.zero)
            previewBounds = new Bounds(previewRoot.transform.position, Vector3.one * 0.5f);

        BuildMarkers();
    }

    private void StripUnsupportedComponents(GameObject root)
    {
        foreach (var anim in root.GetComponentsInChildren<Animator>(true)) DestroyImmediate(anim);
        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true)) DestroyImmediate(ps);
    }

    private void BuildMarkers()
    {
        DestroyGO(markerCurrentPivot);
        DestroyGO(markerNewPivot);
        DestroyGO(markerLink);
        markerCurrentPivot = markerNewPivot = markerLink = null;

        // 구슬
        markerCurrentPivot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        markerNewPivot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        foreach (var c in new[] { markerCurrentPivot.GetComponent<Collider>(), markerNewPivot.GetComponent<Collider>() })
            if (c) DestroyImmediate(c);

        markerCurrentPivot.name = "Pivot_Current";
        markerNewPivot.name = "Pivot_New";

        var s = Vector3.one * previewBounds.extents.magnitude * 0.04f;
        markerCurrentPivot.transform.localScale = s;
        markerNewPivot.transform.localScale = s;

        // 연결선(실린더)
        markerLink = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        var col = markerLink.GetComponent<Collider>(); if (col) DestroyImmediate(col);
        markerLink.name = "Pivot_Link";

        // 머티리얼
        markerCurrentPivot.GetComponent<MeshRenderer>().sharedMaterial = matRed;
        markerNewPivot.GetComponent<MeshRenderer>().sharedMaterial = matGreen;
        markerLink.GetComponent<MeshRenderer>().sharedMaterial = matGray;

        RegisterGO(markerCurrentPivot);
        RegisterGO(markerNewPivot);
        RegisterGO(markerLink);
    }

    private void ReframeCamera()
    {
        if (preview == null) return;
        float radius = previewBounds.extents.magnitude;
        distance = Mathf.Max(0.1f, radius * 2.2f);
        yaw = -30f; pitch = 20f; pan = Vector3.zero;
    }

    private void RecomputePivot()
    {
        hasValidPivot = false;
        if (target == null || previewRoot == null) return;

        // 단일 프리뷰 마커는 "전체" 기준으로 계산
        switch (pivotMode)
        {
            case PivotMode.GeometricCenter:
                proposedWorldPivot = GetVertexCentroidWorld(previewRoot, includeInactiveChildren);
                break;
            case PivotMode.BottomCenter:
                var b = GetCombinedWorldBounds(previewRoot, includeInactiveChildren);
                proposedWorldPivot = new Vector3(b.center.x, b.min.y, b.center.z);
                break;
        }

        hasValidPivot = true;

        // 마커 배치
        if (markerCurrentPivot != null) markerCurrentPivot.transform.position = previewRoot.transform.position;
        if (markerNewPivot != null) markerNewPivot.transform.position = proposedWorldPivot;

        if (markerLink != null)
        {
            Vector3 a = markerCurrentPivot.transform.position;
            Vector3 b2 = markerNewPivot.transform.position;
            Vector3 mid = (a + b2) * 0.5f;
            float len = (a - b2).magnitude;
            markerLink.transform.position = mid;
            markerLink.transform.up = (b2 - a).normalized;
            markerLink.transform.localScale = new Vector3(markerCurrentPivot.transform.localScale.x * 0.3f, len * 0.5f, markerCurrentPivot.transform.localScale.x * 0.3f);
        }
    }

    // ---------- GUI ----------
    private void OnGUI()
    {
        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
            target = (GameObject)EditorGUILayout.ObjectField("Target Object (Scene)", target, typeof(GameObject), true);
            if (GUILayout.Button("Use Selection", GUILayout.Width(110)))
            {
                target = Selection.activeGameObject;
                RebuildPreviewRoot();
                ReframeCamera();
                RecomputePivot();
            }
        }

        EditorGUILayout.Space(4);
        pivotMode = (PivotMode)EditorGUILayout.EnumPopup("Pivot Mode", pivotMode);
        applyMode = (ApplyMode)EditorGUILayout.EnumPopup("Apply Mode", applyMode);
        includeInactiveChildren = EditorGUILayout.ToggleLeft("Include inactive children", includeInactiveChildren);
        parentPivotForSkinned = EditorGUILayout.ToggleLeft("Use Parent-Pivot for SkinnedMeshRenderer", parentPivotForSkinned);

        enablePreviewTransparency = EditorGUILayout.ToggleLeft("Dim object (transparent preview)", enablePreviewTransparency);
        if (enablePreviewTransparency)
        {
            float newOpacity = EditorGUILayout.Slider("Preview Opacity", previewOpacity, 0.05f, 1f);
            if (!Mathf.Approximately(newOpacity, previewOpacity))
            {
                previewOpacity = newOpacity;
                ApplyPreviewOpacity(); // 업데이트
            }
        }
        else
        {
            if (lastTransparencyEnabled) ApplyPreviewOpacity();
        }

        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Recompute Preview", GUILayout.Height(24))) { RebuildPreviewRoot(); RecomputePivot(); ApplyPreviewOpacity(true); }
            GUI.enabled = target != null;
            if (GUILayout.Button("Apply to Target", GUILayout.Height(24))) { ApplyToTarget(); }
            GUI.enabled = true;
        }

        EditorGUILayout.Space(8);
        var r = GUILayoutUtility.GetRect(position.width - 20, Mathf.Clamp(position.height - 210, 220, 10000));
        DrawPreview(r);
    }

    // ---------- 프리뷰 렌더 ----------
    private void DrawPreview(Rect r)
    {
        if (preview == null)
        {
            SetupPreview();
            if (target != null) { RebuildPreviewRoot(); ReframeCamera(); RecomputePivot(); }
        }

        if (previewRoot == null)
        {
            EditorGUI.DrawRect(r, new Color(0.15f, 0.15f, 0.15f, 1f));
            GUI.Label(r, "Select a scene GameObject to preview.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        HandlePreviewCameraInput(r);

        foreach (var go in transientGOs) DestroyGO(go);
        transientGOs.Clear();

        preview.BeginPreview(r, GUIStyle.none);

        var b = previewBounds;
        var center = b.center + pan;
        var cam = preview.camera;
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        cam.transform.position = center + rot * (Vector3.back * distance);
        cam.transform.rotation = rot;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = Mathf.Max(1000f, distance * 10f);

        preview.lights[0].transform.rotation = rot * Quaternion.Euler(25f, 45f, 0f);

        DrawBoundsBoxGOs(b);

        preview.Render(true, true);
        preview.EndAndDrawPreview(r);

        foreach (var go in transientGOs) DestroyGO(go);
        transientGOs.Clear();
    }

    private void HandlePreviewCameraInput(Rect r)
    {
        var e = Event.current;
        if (!r.Contains(e.mousePosition)) return;

        int controlID = GUIUtility.GetControlID(FocusType.Passive);
        switch (e.type)
        {
            case EventType.ScrollWheel:
                float dz = e.delta.y * 0.05f * Mathf.Max(0.2f, distance);
                distance = Mathf.Max(0.05f, distance + dz);
                e.Use();
                Repaint();
                break;
            case EventType.MouseDrag:
                if (e.button == 0) // orbit
                {
                    yaw += e.delta.x * 0.5f;
                    pitch = Mathf.Clamp(pitch - e.delta.y * 0.5f, -89f, 89f);
                    e.Use(); Repaint();
                }
                else if (e.button == 2 || (e.button == 0 && e.shift)) // pan
                {
                    var scale = previewBounds.extents.magnitude * 0.0025f;
                    Vector3 right = Quaternion.Euler(0, yaw, 0) * Vector3.right;
                    Vector3 up = Quaternion.Euler(pitch, yaw, 0) * Vector3.up;
                    pan -= (right * e.delta.x + up * -e.delta.y) * scale;
                    e.Use(); Repaint();
                }
                break;
            case EventType.MouseDown:
                if (e.button == 0 || e.button == 2) GUIUtility.hotControl = controlID;
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == controlID) GUIUtility.hotControl = 0;
                break;
        }
    }

    private void DrawBoundsBoxGOs(Bounds b)
    {
        Vector3[] corners = GetBoundsCorners(b);
        int[,] edges = new int[,] {
            {0,1},{1,2},{2,3},{3,0},
            {4,5},{5,6},{6,7},{7,4},
            {0,4},{1,5},{2,6},{3,7}
        };

        float thickness = Mathf.Max(0.002f, b.extents.magnitude * 0.01f);
        for (int i = 0; i < 12; i++)
        {
            Vector3 a = corners[edges[i, 0]];
            Vector3 c = corners[edges[i, 1]];
            float len = (a - c).magnitude;

            var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            var col = cyl.GetComponent<Collider>(); if (col) DestroyImmediate(col);
            var mr = cyl.GetComponent<MeshRenderer>(); mr.sharedMaterial = matGray;

            cyl.transform.position = (a + c) * 0.5f;
            cyl.transform.up = (c - a).normalized;
            cyl.transform.localScale = new Vector3(thickness, len * 0.5f, thickness);

            RegisterGO(cyl);
            transientGOs.Add(cyl);
        }
    }

    private static Vector3[] GetBoundsCorners(Bounds b)
    {
        Vector3 c = b.center;
        Vector3 e = b.extents;
        return new Vector3[]
        {
            c + new Vector3(-e.x, -e.y, -e.z),
            c + new Vector3( e.x, -e.y, -e.z),
            c + new Vector3( e.x, -e.y,  e.z),
            c + new Vector3(-e.x, -e.y,  e.z),
            c + new Vector3(-e.x,  e.y, -e.z),
            c + new Vector3( e.x,  e.y, -e.z),
            c + new Vector3( e.x,  e.y,  e.z),
            c + new Vector3(-e.x,  e.y,  e.z),
        };
    }

    // ---------- 적용 로직 ----------
    private void ApplyToTarget()
    {
        if (target == null) return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();

        int processed = 0;

        if (applyMode == ApplyMode.ParentPivot)
        {
            // 전체 기준으로 world pivot 계산 → 부모 피벗 생성
            Vector3 worldPivot = ComputeGlobalWorldPivot(target, pivotMode, includeInactiveChildren);
            CreateParentPivotAt(target, worldPivot);
            processed = 1;
        }
        else // BakeIntoMesh
        {
            // 모든 MeshFilter에 대해 '로컬 피벗점'을 계산하여 메시 굽기
            foreach (var mf in target.GetComponentsInChildren<MeshFilter>(includeInactiveChildren))
            {
                var mr = mf.GetComponent<Renderer>();
                if (!mr) continue;

                if (TryGetLocalTargetCenter(mf, mr, pivotMode, includeInactiveChildren, out var localTarget))
                {
                    RecenterMesh(mf, localTarget);
                    processed++;
                }
            }

            // SkinnedMeshRenderer는 옵션에 따라 부모 피벗으로 대체
            if (parentPivotForSkinned)
            {
                foreach (var smr in target.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactiveChildren))
                {
                    Vector3 w = GetWorldPivotForRenderer(smr, pivotMode);
                    CreateParentPivotAt(smr.gameObject, w);
                    processed++;
                }
            }
        }

        EditorSceneManager.MarkAllScenesDirty();
        Undo.CollapseUndoOperations(group);
        Debug.Log($"[PivotTools] Applied. Processed units: {processed}");
    }

    // 전역 피벗(프리뷰 계산과 동일)을 다시 계산
    private static Vector3 ComputeGlobalWorldPivot(GameObject root, PivotMode mode, bool includeInactive)
    {
        switch (mode)
        {
            case PivotMode.GeometricCenter:
                return GetVertexCentroidWorld(root, includeInactive);
            case PivotMode.BottomCenter:
            default:
                var b = GetCombinedWorldBounds(root, includeInactive);
                return new Vector3(b.center.x, b.min.y, b.center.z);
        }
    }

    // SMR/Renderer용 월드 피벗 계산
    private static Vector3 GetWorldPivotForRenderer(Renderer r, PivotMode mode)
    {
        if (!r) return Vector3.zero;
        switch (mode)
        {
            case PivotMode.GeometricCenter:
                return r.bounds.center;
            case PivotMode.BottomCenter:
            default:
                return new Vector3(r.bounds.center.x, r.bounds.min.y, r.bounds.center.z);
        }
    }

    private static Bounds GetCombinedWorldBounds(GameObject root, bool includeInactive)
    {
        Bounds? total = null;
        var renderers = includeInactive ? root.GetComponentsInChildren<Renderer>(true)
                                        : root.GetComponentsInChildren<Renderer>(false);
        foreach (var r in renderers)
        {
            if (!total.HasValue) total = r.bounds;
            else { var b = total.Value; b.Encapsulate(r.bounds); total = b; }
        }
        return total ?? new Bounds(root.transform.position, Vector3.zero);
    }

    private static Vector3 GetVertexCentroidWorld(GameObject root, bool includeInactive)
    {
        Vector3 sum = Vector3.zero; int cnt = 0;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(includeInactive))
        {
            var mesh = mf.sharedMesh; if (!mesh) continue;
            if (!EnsureReadable(mesh)) continue;
            var verts = mesh.vertices;
            for (int i = 0; i < verts.Length; i++)
                sum += mf.transform.TransformPoint(verts[i]);
            cnt += verts.Length;
        }
        if (cnt == 0) return GetCombinedWorldBounds(root, includeInactive).center;
        return sum / cnt;
    }

    private static bool TryGetLocalTargetCenter(
        MeshFilter mf, Renderer r,
        PivotMode mode, bool includeInactive,
        out Vector3 localTarget)
    {
        var mesh = mf.sharedMesh;
        if (!mesh) { localTarget = Vector3.zero; return false; }

        switch (mode)
        {
            case PivotMode.GeometricCenter:
                {
                    if (!EnsureReadable(mesh)) { localTarget = mesh.bounds.center; return true; }
                    var verts = mesh.vertices;
                    if (verts == null || verts.Length == 0) { localTarget = mesh.bounds.center; return true; }
                    Vector3 sum = Vector3.zero;
                    for (int i = 0; i < verts.Length; i++) sum += verts[i];
                    localTarget = sum / verts.Length;
                    return true;
                }

            case PivotMode.BottomCenter:
                {
                    if (!EnsureReadable(mesh))
                    {
                        var bcWorld = new Vector3(r.bounds.center.x, r.bounds.min.y, r.bounds.center.z);
                        localTarget = mf.transform.InverseTransformPoint(bcWorld);
                        return true;
                    }
                    var verts = mesh.vertices;
                    if (verts == null || verts.Length == 0) { localTarget = mesh.bounds.center; return true; }
                    var b = new Bounds(verts[0], Vector3.zero);
                    for (int i = 1; i < verts.Length; i++) b.Encapsulate(verts[i]);
                    localTarget = new Vector3(b.center.x, b.min.y, b.center.z);
                    return true;
                }
        }
        localTarget = Vector3.zero; return false;
    }

    // 메시 굽기(저장 기능 제거, _pivoted 1회만)
    private static void RecenterMesh(MeshFilter mf, Vector3 localTargetCenter)
    {
        if (!mf || !mf.sharedMesh) return;

        var original = mf.sharedMesh;
        if (!EnsureReadable(original))
        {
            Debug.LogWarning($"[PivotTools] Mesh \"{original.name}\" not readable.");
            return;
        }

        Undo.RegisterCompleteObjectUndo(mf, "Center Pivot (MeshFilter)");
        Undo.RegisterCompleteObjectUndo(mf.transform, "Center Pivot (Transform)");

        Mesh newMesh = UnityEngine.Object.Instantiate(original);

        // 이름에 _pivoted 중복 방지
        string baseName = StripSuffix(original.name, "_pivoted");
        newMesh.name = baseName + "_pivoted";

        var verts = newMesh.vertices;
        for (int i = 0; i < verts.Length; i++) verts[i] -= localTargetCenter;
        newMesh.vertices = verts;
        newMesh.RecalculateBounds();

        mf.sharedMesh = newMesh;

        // 월드 위치 보정
        Vector3 worldDelta = mf.transform.TransformVector(localTargetCenter);
        mf.transform.position += worldDelta;

        // 파일 저장(에셋 생성) 제거: 요청에 따라 저장하지 않음
    }

    private static string StripSuffix(string name, string suffix)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(suffix)) return name;
        while (name.EndsWith(suffix, StringComparison.Ordinal))
            name = name.Substring(0, name.Length - suffix.Length);
        return name;
    }

    private static bool EnsureReadable(Mesh mesh)
    {
        if (mesh.isReadable) return true;
        string path = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(path)) return false;
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null) return false;
        if (!importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
        return true;
    }

    private static void CreateParentPivotAt(GameObject go, Vector3 worldCenter)
    {
        Undo.RegisterFullObjectHierarchyUndo(go, "Create Parent Pivot");

        var t = go.transform;
        var oldParent = t.parent;

        var pivotGO = new GameObject(go.name + "_Pivot");
        var p = pivotGO.transform;

        // 부모를 먼저 목표 위치로
        p.position = worldCenter;
        p.rotation = t.rotation;
        p.localScale = Vector3.one; // 부모 스케일은 1 권장

        if (oldParent) p.SetParent(oldParent, true);

        // 자식을 부모로 옮기되 월드 트랜스폼 보존
        t.SetParent(p, true);
    }

    private void ApplyPreviewOpacity(bool force = false)
    {
        if (previewRoot == null) return;

        if (!previewMatsPrepared)
            PreparePreviewMaterials();

        if (!force && Mathf.Approximately(lastAppliedOpacity, previewOpacity) &&
            lastTransparencyEnabled == enablePreviewTransparency) return;

        foreach (var kv in previewMatCopies)
        {
            var r = kv.Key;
            if (!r) continue;

            var mats = kv.Value;
            if (mats == null) continue;

            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (!m) continue;

                if (enablePreviewTransparency) MakeTransparent(m, previewOpacity);
                else RestoreOpaque(m);
            }
        }

        lastAppliedOpacity = previewOpacity;
        lastTransparencyEnabled = enablePreviewTransparency;
    }

    private static void MakeTransparent(Material mat, float alpha)
    {
        if (mat.HasProperty("_Color"))
        {
            var c = mat.color; c.a = alpha; mat.color = c;
        }
        if (mat.HasProperty("_BaseColor"))
        {
            var c = mat.GetColor("_BaseColor"); c.a = alpha; mat.SetColor("_BaseColor", c);
        }

        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // URP: Transparent
        if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 3f);       // Standard: Transparent

        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = (int)RenderQueue.Transparent;

        if (mat.HasProperty("_SurfaceType")) mat.SetFloat("_SurfaceType", 1f); // HDRP
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
    }

    private static void RestoreOpaque(Material mat)
    {
        if (mat.HasProperty("_Color"))
        {
            var c = mat.color; c.a = 1f; mat.color = c;
        }
        if (mat.HasProperty("_BaseColor"))
        {
            var c = mat.GetColor("_BaseColor"); c.a = 1f; mat.SetColor("_BaseColor", c);
        }

        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f);
        if (mat.HasProperty("_Mode")) mat.SetFloat("_Mode", 0f);

        mat.SetInt("_SrcBlend", (int)BlendMode.One);
        mat.SetInt("_DstBlend", (int)BlendMode.Zero);
        mat.SetInt("_ZWrite", 1);
        mat.DisableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = -1;

        if (mat.HasProperty("_SurfaceType")) mat.SetFloat("_SurfaceType", 0f);
    }

    private void PreparePreviewMaterials()
    {
        if (previewRoot == null) return;

        foreach (var kv in previewMatCopies)
        {
            var mats = kv.Value;
            if (mats == null) continue;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i]) DestroyImmediate(mats[i]);
        }
        previewMatCopies.Clear();

        var renderers = previewRoot.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            if (!r) continue;
            var goName = r.gameObject.name;
            if (goName.StartsWith("Pivot_")) continue; // 마커 제외

            var shared = r.sharedMaterials;
            if (shared == null || shared.Length == 0) continue;

            var copies = new Material[shared.Length];
            for (int i = 0; i < shared.Length; i++)
            {
                var src = shared[i];
                if (src == null) continue;
                copies[i] = new Material(src) { name = src.name + " (PreviewCopy)" };
            }

            r.sharedMaterials = copies;
            previewMatCopies[r] = copies;
        }

        previewMatsPrepared = true;
    }
}
#endif