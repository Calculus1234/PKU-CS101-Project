using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HilbertLoop
{
    /// <summary>
    /// 结局实景导演（重构）：结局不再用黑屏 + 大段文字交代，
    /// 而是由本类接管一台结局相机，在真实场景里完成：
    ///   A · 号码顺移后，黑影翻窗补杀新的 9 号；
    ///   B · 灯灭后天亮，众人遗忘、离开，新的复制体在 1 号床上醒来；
    ///   C · 圆桌旁所有人一个接一个摘下面具，露出同一张脸；
    ///   D · 活下来的人把证据摆回桌面，摘下面具，认知崩塌。
    ///
    /// 所有位移 / 摘面具 / 灯光 / 白场都用真实对象完成，
    /// Yarn 台词只作为底部的旁白字幕，不再遮挡场景。
    /// </summary>
    public class EndingDirector : MonoBehaviour
    {
        public static EndingDirector Instance { get; private set; }

        [Header("节奏")]
        public float cameraSmooth = 1f;
        public float unmaskSeconds = 0.9f;

        Camera _camera;
        Camera _playerCamera;
        Transform _meetingArea;
        Transform _propsRoot;
        Light _keyLight;
        Image _flash;

        readonly List<Light> _worldLights = new List<Light>();
        readonly List<float> _worldLightIntensities = new List<float>();
        Color _ambientColor;
        bool _prepared;
        bool _playing;

        public bool IsPlaying => _playing;
        public Camera EndingCamera => _camera;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------
        // 接管场景
        // ------------------------------------------------------------------

        /// <summary>
        /// 接管场景。
        /// cinematicCamera = true：C / D，需要一台结局相机在真实场景里跟拍；
        /// cinematicCamera = false：A / B，只清场，把画面留给主菜单同款监控探头。
        /// </summary>
        public void Prepare(PlayerController player, bool cinematicCamera = true)
        {
            if (_prepared) return;
            _prepared = true;

            DisableIfExists("HUD");
            DisableIfExists("MeetingHUD");
            DisableIfExists("VotingUI_Canvas");
            DisableIfExists("PrivateChatHUD");

            if (GhostSpectator.Instance != null && GhostSpectator.Instance.IsActive)
                GhostSpectator.Instance.Exit();

            if (player == null) player = FindObjectOfType<PlayerController>();
            if (player != null && player.cameraPivot != null)
                _playerCamera = player.cameraPivot.GetComponent<Camera>();
            if (_playerCamera == null) _playerCamera = Camera.main;

            // 关闭玩家相机与会议相机；保留监控相机（它渲染到 RenderTexture）。
            if (_playerCamera != null) _playerCamera.enabled = false;
            MeetingSystem meeting = FindObjectOfType<MeetingSystem>();
            if (meeting != null && meeting.meetingCamera != null)
                meeting.meetingCamera.enabled = false;

            Camera[] cameras = FindObjectsOfType<Camera>();
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera cam = cameras[i];
                if (cam == null) continue;
                if (cam.targetTexture != null) continue;
                cam.enabled = false;
            }

            // A / B 不需要结局相机：监控探头（depth 20）已经在渲染画面。
            // 以前无条件创建这台停在原点的 depth 50 相机，把监控画面整个盖住，
            // 于是镜头看起来「挂在桌子底下」。
            if (cinematicCamera)
            {
                GameObject cameraGo = new GameObject("EndingCamera");
                cameraGo.transform.SetParent(transform, false);
                _camera = cameraGo.AddComponent<Camera>();
                _camera.depth = 50f;
                _camera.fieldOfView = 55f;
                _camera.nearClipPlane = 0.05f;
                _camera.farClipPlane = 150f;
                _camera.clearFlags = CameraClearFlags.Skybox;

                if (_playerCamera != null)
                {
                    _camera.clearFlags = _playerCamera.clearFlags;
                    _camera.backgroundColor = _playerCamera.backgroundColor;
                    _camera.cullingMask = _playerCamera.cullingMask;
                    _camera.fieldOfView = _playerCamera.fieldOfView;
                }

                _camera.enabled = true;
            }

            GameObject meetingGo = GameObject.Find("MeetingArea");
            if (meetingGo != null) _meetingArea = meetingGo.transform;

            GameObject props = new GameObject("EndingProps");
            props.transform.SetParent(transform, false);
            _propsRoot = props.transform;

            CaptureLights();
            _ambientColor = RenderSettings.ambientLight;
            if (cinematicCamera) BuildFlashOverlay();

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            Debug.Log(cinematicCamera
                ? "[EndingDirector] 结局相机与实景演出已接管场景。"
                : "[EndingDirector] A/B 只清场，画面交给监控探头。");
        }

        static void DisableIfExists(string name)
        {
            GameObject go = GameObject.Find(name);
            if (go != null) go.SetActive(false);
        }

        void BuildFlashOverlay()
        {
            var canvasGo = new GameObject("EndingFlash_Canvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 450;

            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var imageGo = new GameObject("Flash", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            imageGo.transform.SetParent(canvasGo.transform, false);
            RectTransform rect = imageGo.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            _flash = imageGo.GetComponent<Image>();
            _flash.color = new Color(1f, 1f, 1f, 0f);
            _flash.raycastTarget = false;
        }

        void CaptureLights()
        {
            _worldLights.Clear();
            _worldLightIntensities.Clear();
            Light[] lights = FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null) continue;
                _worldLights.Add(light);
                _worldLightIntensities.Add(light.intensity);
            }
        }

        // ------------------------------------------------------------------
        // 对外入口
        // ------------------------------------------------------------------

        public IEnumerator Play(EndingType type)
        {
            if (!_prepared)
            {
                Debug.LogError("[EndingDirector] 未调用 Prepare，无法播放实景结局。");
                yield break;
            }

            _playing = true;
            switch (type)
            {
                case EndingType.A:
                    yield return PlayEndingA();
                    break;
                case EndingType.C:
                    yield return PlayEndingC();
                    break;
                case EndingType.D:
                    yield return PlayEndingD();
                    break;
                default:
                    yield return PlayEndingB();
                    break;
            }
            _playing = false;
        }

        // ------------------------------------------------------------------
        // 结局 A：号码顺移，新的 9 号在锁着的房间里被补杀
        // ------------------------------------------------------------------

        IEnumerator PlayEndingA()
        {
            Transform room9 = FindRoom(9);
            Vector3 table = MeetingCenter;
            Vector3 tableLook = table + Vector3.up * 0.8f;

            SetCameraImmediate(table + new Vector3(0f, 3.6f, -5.2f), tableLook);
            yield return Wait(0.8f);
            yield return MoveCamera(table + new Vector3(0f, 2.2f, -3.2f), tableLook, 2.8f);

            // 灯一盏盏暗下去，最后只剩房间的灯。
            yield return SetWorldLightMultiplier(0.35f, 2.6f);
            yield return SetAmbient(Color.Lerp(_ambientColor, Color.black, 0.7f), 2.6f);

            if (room9 == null) yield break;

            Vector3 bedPoint = room9.TransformPoint(new Vector3(-1.6f, 0.55f, 0.3f));
            Vector3 windowPos = room9.TransformPoint(new Vector3(0f, 0f, 2.0f));
            Vector3 approach = room9.TransformPoint(new Vector3(-0.7f, 0.9f, 0.75f));

            // 新的 9 号已经躺在床上；门从里面闩着。
            GameObject newCorpse = SpawnLyingBody(room9, new Vector3(-1.6f, 0f, 0.3f), true);

            Vector3 camPos = room9.TransformPoint(new Vector3(1.15f, 1.75f, -0.1f));
            yield return MoveCamera(camPos, bedPoint, 3.4f);
            yield return Wait(1.2f);

            // 黑影从窗口翻进来，停在床边。
            GameObject shadow = SpawnShadowFigure(windowPos);
            yield return MoveObject(shadow, approach, 2.2f);
            yield return LookAtGradual(shadow, bedPoint, 0.8f);
            yield return LeanObject(shadow, 18f, 0.7f);

            // 刀落下。
            yield return Flash(new Color(0.45f, 0f, 0f), 0.55f, 0.12f);
            yield return ShakeCamera(0.55f, 0.09f);
            yield return Wait(0.9f);
            yield return Flash(new Color(0.45f, 0f, 0f), 0f, 0.9f);

            if (newCorpse != null)
            {
                // 尸体被翻动过：让床上的身体轻微侧过去。
                Quaternion baseRot = newCorpse.transform.rotation;
                yield return RotateObject(newCorpse, baseRot * Quaternion.Euler(0f, 0f, 12f), 0.8f);
            }

            // 黑影退回窗口，消失在雪光里。
            yield return MoveObject(shadow, windowPos + Vector3.up * 0.9f, 1.4f);
            if (shadow != null) shadow.SetActive(false);

            // 天亮了。门仍然锁着。
            yield return SetWorldLightMultiplier(0.8f, 2.2f);
            yield return SetAmbient(_ambientColor, 2.2f);
            yield return Flash(Color.white, 0.55f, 1.2f);
            yield return Flash(Color.white, 0f, 1.8f);

            Vector3 finalPos = room9.TransformPoint(new Vector3(0.9f, 2.05f, -0.4f));
            yield return MoveCamera(finalPos, bedPoint, 3f);
            yield return Wait(2.4f);
        }

        // ------------------------------------------------------------------
        // 结局 B：灯灭、遗忘、顺移，新的复制体在 1 号床上醒来
        // ------------------------------------------------------------------

        IEnumerator PlayEndingB()
        {
            Vector3 table = MeetingCenter;
            // 旧机位 z=-3.2 已经嵌进南墙里；收进大厅内部，并先定好镜头再让人入座。
            Vector3 overview = table + new Vector3(0f, 2.9f, -2.72f);
            Vector3 look = table + Vector3.up * 0.7f;

            SetCameraImmediate(overview, look);
            yield return Wait(0.4f);

            // 活下来的人被灯光的变化引回圆桌，一个接一个坐下。
            List<NPCController> alive = LivingNpcs();
            for (int i = 0; i < alive.Count; i++)
            {
                yield return MoveNpcToSeat(alive[i], 0.75f);
                yield return Wait(0.18f);
            }

            yield return Wait(1.2f);

            // 灯灭。
            yield return SetWorldLightMultiplier(0.28f, 2.8f);
            yield return SetAmbient(Color.Lerp(_ambientColor, Color.black, 0.78f), 2.8f);
            yield return Wait(1.2f);

            // 他们站起来，彼此看了一眼，却谁也想不起发生过什么。
            for (int i = 0; i < alive.Count; i++)
            {
                yield return StandFromSeat(alive[i], 0.55f);
                yield return Wait(0.2f);
            }

            yield return Wait(0.8f);

            // 一个接一个离开圆桌。
            for (int i = 0; i < alive.Count; i++)
            {
                StartCoroutine(WalkAwayFromTable(alive[i], 2.2f));
            }
            yield return Wait(2.4f);
            yield return SetWorldLightMultiplier(0.02f, 2.0f);

            // 镜头穿过黑暗，停在 1 号房。
            Transform room1 = FindRoom(1);
            if (room1 == null)
            {
                yield return Wait(2f);
                yield break;
            }

            HideNpcsNear(room1, new Vector3(-1.6f, 0f, 0.3f), 3.4f);
            GameObject clone = SpawnLyingBody(room1, new Vector3(-1.6f, 0f, 0.3f), true);
            Vector3 bedPoint = room1.TransformPoint(new Vector3(-1.6f, 0.55f, 0.3f));
            Vector3 camPos = room1.TransformPoint(new Vector3(0.85f, 1.7f, -0.2f));

            yield return MoveCamera(camPos, bedPoint, 4.2f);
            yield return Wait(1.6f);

            // 新的复制体睁开眼。循环重新开始。
            if (clone != null)
                StartCoroutine(Breathe(clone.transform));
            yield return Wait(2.8f);
        }

        // ------------------------------------------------------------------
        // 结局 C：大梦初醒——白光之后，在自己的床上睁开眼
        // ------------------------------------------------------------------

        IEnumerator PlayEndingC()
        {
            // 白光先把整个场景吞掉。
            yield return Flash(Color.white, 1f, 1.8f);
            yield return Wait(0.9f);

            // 只短暂压暗，制造「从梦里睁眼」的黑场；稍后必须把灯拉回来，
            // 否则白光散去后卧室是一间全黑的房间（旧版结局 C 的根因）。
            yield return SetWorldLightMultiplier(0.05f, 0.4f);

            Transform room1 = FindRoom(1);
            if (room1 == null)
            {
                Debug.LogWarning("[EndingDirector] 缺少 Room_1，结局 C 只能播放白光。");
                yield return Wait(2.2f);
                yield return Flash(Color.white, 0.25f, 1.6f);
                yield break;
            }

            // 白光退去，卧室显形。床上躺着「你」。
            // 本轮 1 号可能还站在床边，会整个人糊在镜头上；先把他清出画面。
            HideNpcsNear(room1, new Vector3(-1.6f, 0f, 0.3f), 4.5f);
            // 房间 1 里散落的绳子（可拾取绳、系好的攀爬绳、运行时生成的绳线）
            // 会破坏「卧室」的语义——结局 C 是「醒来」，床边不该垂着一根绳子。
            HideRopes();

            // 带骨骼的正式人体：真实贴图 + 仰卧躺姿 + 真正的起身动作。
            BedDreamer dreamer = SpawnBedDreamer(room1);
            GameObject fallback = null;
            if (dreamer == null)
            {
                fallback = SpawnLyingBody(room1, new Vector3(-1.6f, 0f, 0.3f), false);
                if (fallback != null) StartCoroutine(Breathe(fallback.transform));
            }

            // 取景中心放在身体上方一点，白色床单和枕头都要留在画面里。
            Vector3 bedPoint = room1.TransformPoint(new Vector3(-1.55f, 0.55f, 0.3f));
            Vector3 camPos = room1.TransformPoint(new Vector3(0.55f, 1.32f, -0.10f));
            SetCameraImmediate(camPos, bedPoint);

            // 白光淡出的同时把卧室灯恢复，并在床边补一盏暖色主光。
            StartCoroutine(SetWorldLightMultiplier(0.9f, 1.6f));
            StartCoroutine(SpawnRoomLight(room1, new Vector3(0.1f, 2.3f, -0.2f),
                new Color(1f, 0.90f, 0.76f), 4.6f, 0.8f));

            yield return Flash(Color.white, 0f, 2.2f);
            yield return Wait(1.4f);

            if (dreamer != null)
            {
                // 躺在床上的呼吸：极轻的整体起伏。
                StartCoroutine(Breathe(dreamer.visual));
                yield return Wait(1.2f);

                // 真正的起身：以髋部为支点，先把上半身从枕头上撑起来，
                // 再把重心挪到床边站直。镜头同步从床上摇到床边，全程收住全身。
                StartCoroutine(RiseFromBed(dreamer, 1.35f));
                yield return MoveCamera(
                    room1.TransformPoint(new Vector3(1.15f, 1.55f, -0.55f)),
                    room1.TransformPoint(new Vector3(-0.55f, 0.95f, 0.30f)), 2.2f);
                yield return Wait(2.0f);
            }
            else
            {
                yield return Wait(2.6f);
            }

            yield return Wait(0.8f);

            // 又一次白光：醒来，或者进入更深的一层。
            yield return Flash(Color.white, 0.86f, 2.4f);
            yield return Wait(1.2f);
            yield return Flash(Color.white, 0.30f, 1.8f);
            yield return Wait(0.6f);
        }

        // ------------------------------------------------------------------
        // 结局 D：活下来的人把证据拼在一起，然后摘下面具
        // ------------------------------------------------------------------

        IEnumerator PlayEndingD()
        {
            Vector3 table = MeetingCenter;
            Vector3 look = table + Vector3.up * 0.78f;

            // 圆桌正上方的监控屏会整个挡住桌面，摘面具时只能看到它的背面。
            DisableIfExists("MonitorDisplay");
            // 旧机位离桌心只有 1.35m，两侧站起来的 NPC 直接怼在镜头上；
            // 先在高处定好全景，再缓慢推进。
            SetCameraImmediate(table + new Vector3(0f, 2.45f, -2.7f), table + Vector3.up * 0.95f);
            yield return Wait(0.4f);

            List<NPCController> alive = LivingNpcs();

            // 从各自的房间回到圆桌旁，把死去的玩家留下的东西摆上桌面。
            for (int i = 0; i < alive.Count; i++)
            {
                yield return MoveNpcToSeat(alive[i], 0.8f);
                yield return Wait(0.15f);
            }

            PlaceEvidenceOnTable();
            yield return Wait(1.2f);

            yield return MoveCamera(table + new Vector3(0f, 2.15f, -2.3f), look, 2.4f);
            yield return SpawnTableLight(new Color(1f, 0.88f, 0.62f), 3.2f, 0.8f);
            yield return Wait(2.4f);

            // 有人先看见了那两张面具，然后抬头看向别人。
            NPCController first = FirstInformedNpc(alive);
            if (first != null)
            {
                yield return StandFromSeat(first, 0.6f);
                Quaternion baseRot = first.transform.rotation;
                yield return RotateObject(first.gameObject, baseRot * Quaternion.Euler(14f, 0f, 0f), 0.8f);
                yield return Wait(1.2f);
                yield return RotateObject(first.gameObject, baseRot, 0.7f);
            }

            // 第二个、第三个……一个接一个。
            for (int i = 0; i < alive.Count; i++)
            {
                NPCController npc = alive[i];
                if (npc == first) continue;
                yield return StandFromSeat(npc, 0.5f);
                yield return Wait(0.2f);
            }

            for (int i = 0; i < alive.Count; i++)
            {
                yield return UnmaskNpc(alive[i], unmaskSeconds);
                yield return Wait(0.25f);
            }

            // 面具全部摘下后先停一拍，让玩家看清每一张脸，再让白光吞掉一切。
            yield return Wait(1.8f);

            yield return SpawnTableLight(Color.white, 12f, 0.2f);
            yield return SetWorldLightMultiplier(1.8f, 2.2f);
            yield return SetAmbient(Color.white, 2.6f);
            yield return Flash(Color.white, 0.95f, 3.2f);
            yield return Wait(2.4f);

            // 镜头抬高：圆桌、证据、落了一地的面具。
            // 旧机位 y=7.2 已经穿出屋顶，现在收在吊顶下方。
            Vector3 top = table + new Vector3(0f, 3.05f, -2.35f);
            yield return MoveCamera(top, table + Vector3.up * 0.1f, 4.5f);
            yield return Wait(1.8f);
            yield return Flash(Color.white, 0.4f, 2.2f);
            yield return Wait(1.4f);
        }

        // ------------------------------------------------------------------
        // 场景道具
        // ------------------------------------------------------------------

        Transform FindRoom(int number)
        {
            GameObject go = GameObject.Find($"Room_{number}");
            return go != null ? go.transform : null;
        }

        Vector3 MeetingCenter => _meetingArea != null ? _meetingArea.position : Vector3.zero;

        List<NPCController> LivingNpcs()
        {
            var list = new List<NPCController>();
            for (int i = 0; i < NPCController.All.Count; i++)
            {
                NPCController npc = NPCController.All[i];
                if (npc != null && !npc.isDead && npc.gameObject.activeInHierarchy)
                    list.Add(npc);
            }
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
            return list;
        }

        /// <summary>
        /// 结局 B / C 的镜头会贴近 1 号床，本轮若还有 NPC 站在床边，
        /// 会整个人糊在镜头上。演出前把房间范围内的 NPC 隐藏掉。
        /// </summary>
        void HideNpcsNear(Transform room, Vector3 localPoint, float radius)
        {
            if (room == null) return;
            Vector3 center = room.TransformPoint(localPoint);
            NPCController[] npcs = FindObjectsOfType<NPCController>(true);
            for (int i = 0; i < npcs.Length; i++)
            {
                NPCController npc = npcs[i];
                if (npc == null) continue;
                Vector3 delta = npc.transform.position - center;
                delta.y = 0f;
                if (delta.sqrMagnitude <= radius * radius)
                    npc.gameObject.SetActive(false);
            }
        }

        NPCController FirstInformedNpc(List<NPCController> alive)
        {
            for (int i = 0; i < alive.Count; i++)
            {
                NPCBlackboard b = alive[i].blackboard;
                if (b != null && (b.knowsFacesIdentical || b.knownClues.Contains("faces_identical")))
                    return alive[i];
            }
            return alive.Count > 0 ? alive[0] : null;
        }

        Transform FindChair(int number)
        {
            if (_meetingArea == null) return null;
            Transform chair = _meetingArea.Find($"Chair_{number}");
            if (chair != null) return chair;

            Transform[] all = _meetingArea.GetComponentsInChildren<Transform>(true);
            string name = $"Chair_{number}";
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == name) return all[i];
            return null;
        }

        Vector3 SeatPosition(Transform chair)
        {
            Vector3 p = chair.position + chair.forward * 0.05f;
            p.y = chair.position.y + 0.9f;
            return p;
        }

        Vector3 StandPosition(Transform chair)
        {
            Vector3 p = chair.position - chair.forward * 0.62f;
            p.y = chair.position.y + 0.9f;
            return p;
        }

        IEnumerator MoveNpcToSeat(NPCController npc, float duration)
        {
            if (npc == null) yield break;
            Transform chair = FindChair(npc.Number);
            if (chair == null) yield break;
            yield return MoveObject(npc.gameObject, SeatPosition(chair), duration);
            npc.transform.rotation = chair.rotation;
        }

        IEnumerator StandFromSeat(NPCController npc, float duration)
        {
            if (npc == null) yield break;
            Transform chair = FindChair(npc.Number);
            if (chair == null) yield break;
            yield return MoveObject(npc.gameObject, StandPosition(chair), duration);
            Vector3 dir = MeetingCenter - npc.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f)
                npc.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        IEnumerator WalkAwayFromTable(NPCController npc, float duration)
        {
            if (npc == null) yield break;
            Transform chair = FindChair(npc.Number);
            if (chair == null) yield break;
            Vector3 target = chair.position - chair.forward * 3.4f;
            target.y = chair.position.y + 0.9f;
            yield return MoveObject(npc.gameObject, target, duration);
        }

        // ------------------------------------------------------------------
        // 摘面具：面具下落 + 露出同一张脸
        // ------------------------------------------------------------------

        public IEnumerator UnmaskNpc(NPCController npc, float duration)
        {
            if (npc == null || npc.isDead) yield break;

            // 正式模型：面具挂在 Head 骨骼下，不能再用 transform.Find("Mask")。
            // 先在面具当前世界位置生成掉落的实物，再交给视觉控制器露出里面的人形。
            NPCVisualController visual = npc.visual != null ? npc.visual : npc.GetComponent<NPCVisualController>();
            if (visual != null && visual.maskTransform != null)
            {
                SpawnFallenMask(visual.MaskWorldPosition, visual.MaskWorldRotation, visual.maskRenderer);
                yield return Wait(Mathf.Max(0.05f, duration * 0.45f));
                visual.Unmask();
                yield break;
            }

            Transform mask = npc.transform.Find("Mask");
            Transform number = npc.transform.Find("MaskNumber");
            Transform cape = npc.transform.Find("Cape");

            if (mask != null && mask.gameObject.activeSelf)
            {
                Vector3 maskWorld = mask.position;
                Quaternion maskRot = mask.rotation;
                SpawnFallenMask(maskWorld, maskRot, null);

                Vector3 from = mask.localPosition;
                Vector3 to = from + new Vector3(0f, -0.62f, 0.18f);
                Quaternion fromRot = mask.localRotation;
                Quaternion toRot = fromRot * Quaternion.Euler(75f, 12f, 26f);
                Vector3 fromScale = mask.localScale;

                float t = 0f;
                while (t < 1f)
                {
                    t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                    mask.localPosition = Vector3.Lerp(from, to, k);
                    mask.localRotation = Quaternion.Slerp(fromRot, toRot, k);
                    mask.localScale = Vector3.Lerp(fromScale, fromScale * 0.15f, k);
                    yield return null;
                }
                mask.gameObject.SetActive(false);
            }

            if (number != null) number.gameObject.SetActive(false);
            EnsureFace(npc.transform);

            if (cape != null && cape.gameObject.activeSelf)
            {
                Vector3 from = cape.localPosition;
                Vector3 to = from + new Vector3(0f, -0.75f, 0f);
                Vector3 fromScale = cape.localScale;
                float t = 0f;
                while (t < 1f)
                {
                    t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                    cape.localPosition = Vector3.Lerp(from, to, k);
                    cape.localScale = Vector3.Lerp(fromScale, fromScale * 0.05f, k);
                    yield return null;
                }
                cape.gameObject.SetActive(false);
            }
        }

        void EnsureFace(Transform npc)
        {
            if (npc.Find("EndingFace") != null) return;

            Material skin = CreateMaterial(new Color(0.82f, 0.66f, 0.55f));
            Material eye = CreateMaterial(new Color(0.08f, 0.07f, 0.06f));

            GameObject face = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            face.name = "EndingFace";
            Destroy(face.GetComponent<Collider>());
            face.transform.SetParent(npc, false);
            face.transform.localPosition = new Vector3(0f, 0.30f, 0.31f);
            face.transform.localScale = new Vector3(0.26f, 0.31f, 0.12f);
            face.GetComponent<MeshRenderer>().sharedMaterial = skin;

            for (int i = -1; i <= 1; i += 2)
            {
                GameObject pupil = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                pupil.name = "EndingEye";
                Destroy(pupil.GetComponent<Collider>());
                pupil.transform.SetParent(npc, false);
                pupil.transform.localPosition = new Vector3(0.06f * i, 0.33f, 0.37f);
                pupil.transform.localScale = Vector3.one * 0.032f;
                pupil.GetComponent<MeshRenderer>().sharedMaterial = eye;
            }
        }

        // ------------------------------------------------------------------
        // 尸体 / 黑影 / 复制体
        // ------------------------------------------------------------------

        static GameObject _endingHumanPrefab;
        static bool _endingHumanPrefabChecked;
        static Material _endingHumanMaterial;

        GameObject SpawnLyingBody(Transform room, Vector3 localPosition, bool withMask)
        {
            if (room == null) return null;

            // 床的实际躺面（床垫/毯子）在 0.30 左右，床架立柱才到 0.64；
            // 支点放在「躺面 + 人体半厚约 0.15」的髋部高度，人才能贴床而不是悬空。
            GameObject root = new GameObject("Ending_LyingBody");
            root.transform.SetParent(_propsRoot, false);
            root.transform.position = room.TransformPoint(localPosition + Vector3.up * 0.455f);
            root.transform.rotation = room.rotation;

            if (!_endingHumanPrefabChecked)
            {
                _endingHumanPrefabChecked = true;
                _endingHumanPrefab = Resources.Load<GameObject>("NPC/EndingHuman");
                if (_endingHumanPrefab == null)
                    Debug.LogWarning("[EndingDirector] 缺少 Resources/NPC/EndingHuman.prefab，临时退回占位人形。");
            }

            if (_endingHumanPrefab != null)
            {
                // 人.fbx 本身是仰卧姿态：脚在原点、头朝 +Z，长 1.18m、厚 0.21m。
                // 放大 1.45 倍后约 1.71m，脚放在 -0.855，头正好顶到 +0.856。
                GameObject human = Instantiate(_endingHumanPrefab, root.transform);
                human.name = "Body";
                human.transform.localPosition = new Vector3(0f, 0f, -0.855f);
                human.transform.localRotation = Quaternion.identity;
                human.transform.localScale = Vector3.one * 1.45f;

                // 人.fbx 自带的材质是接近纯白的，在卧室暖光下会过曝成一块白布。
                // 换成本机共享的浅肤色材质，尸体 / 梦者都更像一个人。
                Renderer humanRenderer = human.GetComponentInChildren<Renderer>(true);
                if (humanRenderer != null)
                {
                    if (_endingHumanMaterial == null)
                        _endingHumanMaterial = CreateMaterial(new Color(0.76f, 0.63f, 0.55f));
                    humanRenderer.sharedMaterial = _endingHumanMaterial;
                }
            }
            else
            {
                Material bodyMat = CreateMaterial(new Color(0.28f, 0.20f, 0.22f));
                Material clothMat = CreateMaterial(new Color(0.10f, 0.10f, 0.12f));
                Material skinMat = CreateMaterial(new Color(0.70f, 0.56f, 0.47f));

                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = new Vector3(0f, -0.135f, 0.05f);
                body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                body.transform.localScale = new Vector3(0.36f, 0.8f, 0.36f);
                body.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;

                GameObject cape = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                cape.name = "Cape";
                Destroy(cape.GetComponent<Collider>());
                cape.transform.SetParent(root.transform, false);
                cape.transform.localPosition = new Vector3(0f, -0.115f, -0.02f);
                cape.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                cape.transform.localScale = new Vector3(0.46f, 0.82f, 0.46f);
                cape.GetComponent<MeshRenderer>().sharedMaterial = clothMat;

                GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                head.name = "Head";
                Destroy(head.GetComponent<Collider>());
                head.transform.SetParent(root.transform, false);
                head.transform.localPosition = new Vector3(0f, -0.175f, 0.92f);
                head.transform.localScale = Vector3.one * 0.3f;
                head.GetComponent<MeshRenderer>().sharedMaterial = skinMat;
            }

            if (withMask)
            {
                GameObject mask = GameObject.CreatePrimitive(PrimitiveType.Cube);
                mask.name = "Mask";
                Destroy(mask.GetComponent<Collider>());
                mask.transform.SetParent(root.transform, false);
                mask.transform.localPosition = _endingHumanPrefab != null
                    ? new Vector3(0f, 0.19f, 0.67f)
                    : new Vector3(0f, 0.005f, 0.92f);
                mask.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                mask.transform.localScale = new Vector3(0.3f, 0.34f, 0.05f);
                mask.GetComponent<MeshRenderer>().sharedMaterial =
                    CreateMaterial(new Color(0.78f, 0.74f, 0.66f));
            }

            return root;
        }

        // ------------------------------------------------------------------
        // 结局 C 专用：带骨骼的「床上的人」
        // ------------------------------------------------------------------

        /// <summary>一只带 Animator 的人体，记录躺 / 站两套局部姿态与髋部锚点。</summary>
        class BedDreamer
        {
            public GameObject root;
            public Transform visual;
            public Animator animator;
            public Transform hips;
            public Vector3 lieLocal;
            public Quaternion lieRot;
            public Quaternion standRot;
            public Vector3 hipLie;
            public Vector3 hipStand;
        }

        /// <summary>
        /// 生成躺在床上的人：Resources/NPC/EndingHumanRig（人形骨骼 + HL_EndingHuman 贴图）。
        /// 躺姿 = 绕 X 轴 270°，头顶朝床头、正面朝上；缩放 1.4 后身高约 1.4 m，
        /// 根节点抬到 0.50 让背贴床面（0.44 会陷进床垫，0.56 会浮空）。
        /// </summary>
        BedDreamer SpawnBedDreamer(Transform room)
        {
            if (room == null) return null;

            GameObject prefab = Resources.Load<GameObject>("NPC/EndingHumanRig");
            if (prefab == null)
            {
                Debug.LogWarning("[EndingDirector] 缺少 Resources/NPC/EndingHumanRig，结局 C 退回静态人体。");
                return null;
            }

            GameObject rig = Instantiate(prefab, _propsRoot);
            rig.name = "Ending_Dreamer";
            SetLayerRecursively(rig, 0);

            Transform visual = rig.transform.Find("Visual");
            if (visual == null)
            {
                Destroy(rig);
                return null;
            }

            Animator animator = visual.GetComponent<Animator>();
            Transform hips = FindDeep(visual, "Hips");
            if (hips == null)
            {
                Destroy(rig);
                return null;
            }

            BedDreamer d = new BedDreamer();
            d.root = rig;
            d.visual = visual;
            d.animator = animator;
            d.hips = hips;
            d.lieLocal = new Vector3(0f, 0f, 0.85f);
            d.lieRot = Quaternion.Euler(270f, 0f, 0f);
            d.standRot = Quaternion.Euler(0f, 118f, 0f);

            Vector3 bedRoot = room.TransformPoint(new Vector3(-1.6f, 0.50f, 0.28f));
            Vector3 standPoint = room.TransformPoint(new Vector3(-0.55f, 0f, 0.30f));

            rig.transform.position = bedRoot;
            rig.transform.rotation = room.rotation;
            visual.localPosition = d.lieLocal;
            visual.localRotation = d.lieRot;
            visual.localScale = Vector3.one * 1.4f;

            // Idle 首帧：双臂自然下垂、双腿并拢。冻结在这一帧，躺/站都靠整体变换，
            // 骨骼不会自己乱动，起身过程才可控；站定后再解冻进入呼吸。
            if (animator != null)
            {
                animator.enabled = true;
                animator.speed = 0f;
                animator.Play("Idle", 0, 0f);
                animator.Update(0f);
            }

            d.hipLie = rig.transform.InverseTransformPoint(hips.position);

            // 量一次站立姿态的髋高：把 visual 归零、根节点挪到床边站位，读 Hips。
            rig.transform.position = standPoint;
            visual.localPosition = Vector3.zero;
            visual.localRotation = Quaternion.identity;
            float hipHeight = hips.position.y - standPoint.y;
            rig.transform.position = bedRoot;
            visual.localPosition = d.lieLocal;
            visual.localRotation = d.lieRot;

            d.hipStand = rig.transform.InverseTransformPoint(standPoint + Vector3.up * hipHeight);
            return d;
        }

        /// <summary>
        /// 从躺姿起身：髋部是锚点——先绕髋把上半身撑起来（脚还留在床上/床沿），
        /// 再逐渐把髋抬到站立高度、躯干转正。绕脚或绕根节点整体旋转都会变成
        /// 「一块木板翘起来」，只有髋部锚定才像人。
        /// </summary>
        IEnumerator RiseFromBed(BedDreamer d, float duration)
        {
            if (d == null || d.visual == null || d.hips == null || d.root == null) yield break;

            float t = 0f;
            while (t < duration)
            {
                if (d.visual == null || d.root == null) yield break;
                t += Time.unscaledDeltaTime;
                float time = Mathf.Max(0.01f, duration);
                float rotK = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.07f) / (time * 0.87f)));
                float posK = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.09f) / (time * 0.91f)));

                Quaternion q = Quaternion.Slerp(d.lieRot, d.standRot, rotK);
                d.visual.localRotation = q;

                // 髋骨当前在 visual 本地空间的位置（随骨骼缩放），旋到世界朝向后换算成偏移
                Vector3 hipLocal = d.visual.InverseTransformPoint(d.hips.position);
                Vector3 hipOffset = q * Vector3.Scale(hipLocal, d.visual.lossyScale);

                Vector3 hip = Vector3.Lerp(d.hipLie, d.hipStand, posK);
                // 高度走 1.6 次幂：先撑起上半身，最后一段才真正站起来
                hip.y = Mathf.Lerp(d.hipLie.y, d.hipStand.y, Mathf.Pow(posK, 1.6f));
                d.visual.localPosition = hip - hipOffset;

                // 脚底不要穿进地板（起身末段腿会短暂低于地面）
                float footY = d.root.transform.TransformPoint(d.visual.localPosition).y;
                if (footY < 0.012f)
                    d.visual.localPosition += Vector3.up * (0.012f - footY);

                yield return null;
            }

            if (d.animator != null)
            {
                d.animator.speed = 0.9f;
                d.animator.CrossFadeInFixedTime("Idle", 0.2f);
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].name == name) return all[i];
            return null;
        }

        static void SetLayerRecursively(GameObject go, int layer)
        {
            if (go == null) return;
            go.layer = layer;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i).gameObject, layer);
        }

        /// <summary>收掉场景里所有绳子视觉：可拾取绳、系好的攀爬绳、运行时绳线。</summary>
        int HideRopes()
        {
            int hidden = 0;
            Transform[] all = FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform tr = all[i];
                if (tr == null || !tr.gameObject.activeSelf) continue;
                string name = tr.name;
                if (name == "Item_rope" || name == "ClimbRope" || name.StartsWith("RopeLine_"))
                {
                    tr.gameObject.SetActive(false);
                    hidden++;
                }
            }
            return hidden;
        }

        GameObject SpawnShadowFigure(Vector3 worldPosition)
        {
            GameObject root = new GameObject("Ending_Shadow");
            root.transform.SetParent(_propsRoot, false);
            root.transform.position = worldPosition + Vector3.up * 0.9f;

            Material dark = CreateMaterial(new Color(0.025f, 0.025f, 0.03f));

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.62f, 0.95f, 0.62f);
            body.GetComponent<MeshRenderer>().sharedMaterial = dark;

            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            Destroy(head.GetComponent<Collider>());
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            head.transform.localScale = Vector3.one * 0.34f;
            head.GetComponent<MeshRenderer>().sharedMaterial = dark;

            GameObject knife = GameObject.CreatePrimitive(PrimitiveType.Cube);
            knife.name = "Knife";
            Destroy(knife.GetComponent<Collider>());
            knife.transform.SetParent(root.transform, false);
            knife.transform.localPosition = new Vector3(0.38f, 0.25f, 0.28f);
            knife.transform.localRotation = Quaternion.Euler(0f, 0f, -75f);
            knife.transform.localScale = new Vector3(0.035f, 0.72f, 0.02f);
            knife.GetComponent<MeshRenderer>().sharedMaterial =
                CreateMaterial(new Color(0.62f, 0.64f, 0.66f));

            return root;
        }

        IEnumerator Breathe(Transform target)
        {
            if (target == null) yield break;
            Vector3 baseScale = target.localScale;
            float t = 0f;
            while (t < 10f)
            {
                t += Time.unscaledDeltaTime;
                float k = 1f + Mathf.Sin(t * 1.5f) * 0.012f;
                target.localScale = baseScale * k;
                yield return null;
            }
            target.localScale = baseScale;
        }

        // ------------------------------------------------------------------
        // 证据桌面
        // ------------------------------------------------------------------

        // 结局桌面的实物证据：直接用房间里同款的 Resources/Items 模型，
        // 不再用纯白 Cube/Cylinder 占位（白光一打就像一排没做完的白模）。
        static GameObject _propMagnet;
        static GameObject _propNail;
        static GameObject _propRope;
        static GameObject _propDagger;
        static GameObject _propNote;
        static GameObject _propMask;
        static bool _propPrefabsLoaded;

        static void LoadPropPrefabs()
        {
            if (_propPrefabsLoaded) return;
            _propPrefabsLoaded = true;

            _propMagnet = Resources.Load<GameObject>("Items/magnet");
            _propNail = Resources.Load<GameObject>("Items/nail");
            _propRope = Resources.Load<GameObject>("Items/rope");
            _propDagger = Resources.Load<GameObject>("Items/dagger");
            _propNote = Resources.Load<GameObject>("Items/note");
            _propMask = Resources.Load<GameObject>("Props/MaskProp");

            if (_propMask == null)
                Debug.LogWarning("[EndingDirector] 缺少 Resources/Props/MaskProp.prefab，桌面面具退回简化模型。");
        }

        void PlaceEvidenceOnTable()
        {
            if (_meetingArea == null) return;
            LoadPropPrefabs();

            float tableY = MeetingCenter.y + 0.79f;
            Vector3 table = new Vector3(MeetingCenter.x, tableY, MeetingCenter.z);

            // 磁铁、长钉、绳子、匕首、纸条：都是玩家在房间里见过的那几件实物。
            SpawnItem(_propMagnet, "Evidence_Magnet",
                table + new Vector3(-0.55f, 0f, 0.12f), Quaternion.Euler(0f, 22f, 0f), tableY);
            SpawnItem(_propNail, "Evidence_Nail",
                table + new Vector3(-0.28f, 0f, 0.18f), Quaternion.Euler(0f, 30f, 90f), tableY);
            SpawnItem(_propRope, "Evidence_Rope",
                table + new Vector3(0f, 0f, 0.22f), Quaternion.Euler(0f, 18f, 0f), tableY);
            SpawnItem(_propDagger, "Evidence_Dagger",
                table + new Vector3(0.30f, 0f, 0.10f), Quaternion.Euler(0f, -20f, 0f), tableY);
            SpawnItem(_propNote, "Evidence_Note",
                table + new Vector3(0.52f, 0f, 0.04f), Quaternion.Euler(90f, -18f, 0f), tableY);

            // 两张面具：9 号的和玩家自己摘下来的，面朝上并排放在桌心。
            SpawnMaskProp("Evidence_Mask_9", table + new Vector3(-0.05f, 0f, -0.26f), 10f, tableY);
            SpawnMaskProp("Evidence_Mask_Player", table + new Vector3(0.30f, 0f, -0.28f), -14f, tableY);
        }

        void SpawnItem(GameObject prefab, string name, Vector3 position, Quaternion rotation, float tableY)
        {
            if (prefab == null) return;

            GameObject go = Instantiate(prefab, _propsRoot);
            go.name = name;
            go.transform.rotation = rotation;
            go.transform.position = position;

            // 道具枢轴在模型中心，按渲染包围盒落到桌面，避免半截埋进桌板。
            Renderer renderer = go.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                Bounds bounds = renderer.bounds;
                go.transform.position += Vector3.up * (tableY + 0.002f - bounds.min.y);
            }
        }

        void SpawnMaskProp(string name, Vector3 position, float yaw, float tableY)
        {
            if (_propMask == null) return;

            GameObject mask = Instantiate(_propMask, _propsRoot);
            mask.name = name;
            mask.transform.localScale = Vector3.one * 0.17f;
            // 面具正面（模型 +Z）朝上，平躺在桌面上；yaw 让两张脸稍微错开。
            mask.transform.rotation = Quaternion.AngleAxis(yaw, Vector3.up)
                * Quaternion.LookRotation(Vector3.up, Vector3.forward);
            mask.transform.position = position;

            Renderer renderer = mask.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                Bounds bounds = renderer.bounds;
                mask.transform.position += Vector3.up * (tableY + 0.002f - bounds.min.y);
            }
        }

        void SpawnFallenMask(Vector3 worldPosition, Quaternion worldRotation, Renderer sourceRenderer)
        {
            LoadPropPrefabs();

            GameObject mask;
            if (_propMask != null)
            {
                mask = Instantiate(_propMask, _propsRoot);
                mask.name = "FallenMask";
                mask.transform.localScale = Vector3.one * 0.17f;
                mask.transform.position = worldPosition;
                mask.transform.rotation = worldRotation;

                if (sourceRenderer != null)
                {
                    Renderer[] renderers = mask.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                        renderers[i].sharedMaterial = sourceRenderer.sharedMaterial;
                }
            }
            else
            {
                // 兜底：缺少面具预制体时仍保证掉落的不是空气。
                mask = GameObject.CreatePrimitive(PrimitiveType.Cube);
                mask.name = "FallenMask";
                Destroy(mask.GetComponent<Collider>());
                mask.transform.SetParent(_propsRoot, true);
                mask.transform.position = worldPosition;
                mask.transform.rotation = worldRotation;
                mask.transform.localScale = new Vector3(0.30f, 0.34f, 0.05f);
                mask.GetComponent<MeshRenderer>().sharedMaterial =
                    CreateMaterial(new Color(0.78f, 0.74f, 0.66f));
            }

            StartCoroutine(DropProp(mask.transform, 0.9f));
        }

        IEnumerator DropProp(Transform prop, float duration)
        {
            if (prop == null) yield break;
            Vector3 from = prop.position;
            Vector3 to = new Vector3(from.x, 0.035f, from.z);
            Quaternion fromRot = prop.rotation;
            Quaternion toRot = fromRot * Quaternion.Euler(72f, 48f, 30f);

            float t = 0f;
            while (t < 1f)
            {
                if (prop == null) yield break;
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.Clamp01(t);
                Vector3 p = Vector3.Lerp(from, to, k);
                p.y += Mathf.Sin(k * Mathf.PI) * 0.08f;
                prop.position = p;
                prop.rotation = Quaternion.Slerp(fromRot, toRot, k);
                yield return null;
            }
            if (prop == null) yield break;
            prop.position = to;
            prop.rotation = toRot;
        }

        // ------------------------------------------------------------------
        // 灯光 / 相机
        // ------------------------------------------------------------------

        IEnumerator SetWorldLightMultiplier(float multiplier, float duration)
        {
            float[] from = new float[_worldLights.Count];
            for (int i = 0; i < _worldLights.Count; i++)
                from[i] = _worldLights[i] != null ? _worldLights[i].intensity : 0f;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                for (int i = 0; i < _worldLights.Count; i++)
                {
                    if (_worldLights[i] == null) continue;
                    _worldLights[i].intensity = Mathf.Lerp(from[i], _worldLightIntensities[i] * multiplier, k);
                }
                yield return null;
            }
        }

        IEnumerator SetAmbient(Color target, float duration)
        {
            Color from = RenderSettings.ambientLight;
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                RenderSettings.ambientLight = Color.Lerp(from, target, k);
                yield return null;
            }
            RenderSettings.ambientLight = target;
        }

        IEnumerator SpawnTableLight(Color color, float intensity, float duration)
        {
            GameObject go = new GameObject("EndingKeyLight");
            go.transform.SetParent(_propsRoot, false);
            go.transform.position = MeetingCenter + Vector3.up * 1.7f;
            _keyLight = go.AddComponent<Light>();
            _keyLight.type = LightType.Point;
            _keyLight.color = color;
            _keyLight.range = 11f;
            _keyLight.intensity = 0f;
            _keyLight.shadows = LightShadows.None;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                _keyLight.intensity = Mathf.Lerp(0f, intensity, k);
                yield return null;
            }
            _keyLight.intensity = intensity;
        }

        /// <summary>在房间里放一盏逐渐亮起的暖色主光（结局 C 的卧室苏醒镜头）。</summary>
        IEnumerator SpawnRoomLight(Transform room, Vector3 localPosition, Color color,
            float intensity, float duration)
        {
            if (room == null) yield break;

            GameObject go = new GameObject("EndingRoomLight");
            go.transform.SetParent(_propsRoot, false);
            go.transform.position = room.TransformPoint(localPosition);

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = 9f;
            light.intensity = 0f;
            light.shadows = LightShadows.None;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                light.intensity = Mathf.Lerp(0f, intensity, k);
                yield return null;
            }
            light.intensity = intensity;
        }

        IEnumerator Flash(Color color, float alpha, float duration)
        {
            if (_flash == null) yield break;
            Color from = _flash.color;
            Color to = new Color(color.r, color.g, color.b, alpha);
            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                _flash.color = Color.Lerp(from, to, k);
                yield return null;
            }
            _flash.color = to;
        }

        void SetCameraImmediate(Vector3 position, Vector3 lookAt)
        {
            if (_camera == null) return;
            _camera.transform.position = position;
            Vector3 dir = lookAt - position;
            if (dir.sqrMagnitude > 0.0001f)
                _camera.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        IEnumerator MoveCamera(Vector3 position, Vector3 lookAt, float duration)
        {
            if (_camera == null) yield break;
            Vector3 fromPos = _camera.transform.position;
            Quaternion fromRot = _camera.transform.rotation;
            Vector3 dir = lookAt - position;
            Quaternion toRot = dir.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(dir.normalized, Vector3.up)
                : fromRot;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration * cameraSmooth);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                _camera.transform.position = Vector3.Lerp(fromPos, position, k);
                _camera.transform.rotation = Quaternion.Slerp(fromRot, toRot, k);
                yield return null;
            }
            _camera.transform.position = position;
            _camera.transform.rotation = toRot;
        }

        IEnumerator ShakeCamera(float duration, float magnitude)
        {
            if (_camera == null) yield break;
            Vector3 basePos = _camera.transform.position;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                Vector2 offset = Random.insideUnitCircle * magnitude;
                _camera.transform.position = basePos + new Vector3(offset.x, offset.y, 0f);
                yield return null;
            }
            _camera.transform.position = basePos;
        }

        IEnumerator MoveObject(GameObject target, Vector3 position, float duration)
        {
            if (target == null) yield break;
            Vector3 from = target.transform.position;
            float t = 0f;
            while (t < 1f)
            {
                if (target == null) yield break;
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                target.transform.position = Vector3.Lerp(from, position, k);
                yield return null;
            }
            if (target != null) target.transform.position = position;
        }

        IEnumerator RotateObject(GameObject target, Quaternion rotation, float duration)
        {
            if (target == null) yield break;
            Quaternion from = target.transform.rotation;
            float t = 0f;
            while (t < 1f)
            {
                if (target == null) yield break;
                t += Time.unscaledDeltaTime / Mathf.Max(0.01f, duration);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                target.transform.rotation = Quaternion.Slerp(from, rotation, k);
                yield return null;
            }
        }

        IEnumerator LookAtGradual(GameObject target, Vector3 lookAt, float duration)
        {
            if (target == null) yield break;
            Vector3 dir = lookAt - target.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) yield break;
            yield return RotateObject(target, Quaternion.LookRotation(dir.normalized, Vector3.up), duration);
        }

        IEnumerator LeanObject(GameObject target, float angle, float duration)
        {
            if (target == null) yield break;
            Quaternion baseRot = target.transform.rotation;
            yield return RotateObject(target, baseRot * Quaternion.Euler(angle, 0f, 0f), duration);
        }

        static IEnumerator Wait(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }

        static Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            Material material = new Material(shader);
            material.color = color;
            return material;
        }
    }
}
