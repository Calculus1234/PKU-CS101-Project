using UnityEngine;

namespace ChessDark.Unity
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class GiantBoardCameraFramer : MonoBehaviour
    {
        [Header("Composition")]
        public float pitch = 40f;
        public float fieldOfView = 60f;
        public float targetHeight = 2f;
        [Range(0.3f, 0.95f)] public float boardScreenWidth = 0.68f;

        [Header("Margins (NDC)")]
        public float boardScreenBottom = -0.95f;
        public float farFinialTop = 0.94f;
        public float farFinialOuter = 0.97f;

        [Header("Search Range")]
        public float minDistance = 10f;
        public float maxDistance = 160f;
        public float distanceStep = 0.1f;

        [Header("Viewing Side")]
        [Tooltip("勾选后相机移到黑方一侧，黑方棋子在屏幕下方")]
        public bool viewFromBlackSide;

        const string BaseName = "Base_Platform";
        const string TilesName = "Tiles";

        void Start()
        {
            Frame();
        }

        public void SetViewFromBlackSide(bool fromBlack)
        {
            viewFromBlackSide = fromBlack;
            Frame();
        }

        public void Frame()
        {
            var cam = GetComponent<Camera>();
            if (cam != null) FrameForAspect(cam.aspect);
        }

        public void FrameForAspect(float aspect)
        {
            var cam = GetComponent<Camera>();
            if (cam == null || aspect <= 0f) return;

            var baseObj = GameObject.Find(BaseName);
            var tilesObj = GameObject.Find(TilesName);
            if (baseObj == null || tilesObj == null) return;

            Bounds wb = WorldBounds(baseObj);
            Bounds tb = WorldBounds(tilesObj);

            float boardX = Mathf.Max(Mathf.Abs(tb.min.x), Mathf.Abs(tb.max.x));
            float boardZ = Mathf.Max(Mathf.Abs(tb.min.z), Mathf.Abs(tb.max.z));
            float boardTop = tb.max.y;

            float half = Mathf.Max(Mathf.Abs(wb.min.x), Mathf.Abs(wb.max.x));
            half = Mathf.Max(half, Mathf.Max(Mathf.Abs(wb.min.z), Mathf.Abs(wb.max.z)));
            float finial = half * 0.84f;
            float yMax = wb.max.y;

            Vector3 target = new Vector3(0f, targetHeight, 0f);
            float f = 1f / Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            float pitchRad = pitch * Mathf.Deg2Rad;
            float dirZ = (viewFromBlackSide ? 1f : -1f) * Mathf.Cos(pitchRad);
            Vector3 dir = new Vector3(0f, Mathf.Sin(pitchRad), dirZ);

            cam.fieldOfView = fieldOfView;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;

            float best = maxDistance;
            for (float d = minDistance; d <= maxDistance; d += distanceStep)
            {
                Vector3 pos = target + dir * d;
                Quaternion rot = Quaternion.LookRotation(target - pos, Vector3.up);
                cam.transform.SetPositionAndRotation(pos, rot);
                Matrix4x4 w2c = cam.worldToCameraMatrix;

                bool ok = true;

                for (int sx = -1; sx <= 1 && ok; sx += 2)
                {
                    for (int sz = -1; sz <= 1 && ok; sz += 2)
                    {
                        if (!Project(w2c, new Vector3(sx * boardX, boardTop, sz * boardZ), f, aspect, out float nx, out float ny))
                        {
                            ok = false;
                            break;
                        }
                        if (Mathf.Abs(nx) > boardScreenWidth || ny < boardScreenBottom) ok = false;
                    }
                }

                if (ok)
                {
                    float farZ = viewFromBlackSide ? -finial : finial;
                    for (int sx = -1; sx <= 1 && ok; sx += 2)
                    {
                        var p = new Vector3(sx * finial, yMax, farZ);
                        if (!Project(w2c, p, f, aspect, out float nx, out float ny))
                        {
                            ok = false;
                            break;
                        }
                        if (Mathf.Abs(nx) > farFinialOuter || ny > farFinialTop) ok = false;
                    }
                }

                if (ok) { best = d; break; }
            }

            Vector3 finalPos = target + dir * best;
            cam.transform.SetPositionAndRotation(finalPos, Quaternion.LookRotation(target - finalPos, Vector3.up));
        }

        static bool Project(Matrix4x4 w2c, Vector3 world, float f, float aspect, out float nx, out float ny)
        {
            Vector3 vp = w2c.MultiplyPoint3x4(world);
            float depth = -vp.z;
            if (depth <= 0.5f)
            {
                nx = 0f;
                ny = 0f;
                return false;
            }
            nx = vp.x * f / aspect / depth;
            ny = vp.y * f / depth;
            return true;
        }

        static Bounds WorldBounds(GameObject obj)
        {
            var renderers = obj.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }
    }
}
