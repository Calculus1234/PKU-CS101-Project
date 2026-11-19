using UnityEngine;

namespace HilbertLoop
{
    /// <summary>
    /// 场景中的一次性证据点（E 键检查）：
    /// - 圆桌监控的公开回放；
    /// - 9号 / 1号 / 2号窗外墙上的钉痕；
    /// - 以后新增的墙面划痕、窗台痕迹等。
    /// 证据进入 EvidenceLedger，会议 / 私聊的证据面板会自动列出。
    /// </summary>
    public class WorldEvidence : MonoBehaviour, IInteractable
    {
        [Header("证据")]
        public string evidenceId = "";
        public string displayName = "线索";
        [TextArea] public string reason = "";
        [Range(0, 100)] public int weight = 20;

        [Header("交互")]
        public string prompt = "检查";
        public bool oneShot = true;
        public bool isVideoLog = false;

        [Header("检查后的提示")]
        [TextArea] public string foundMessage = "";

        bool _used;

        /// <summary>墙钉类证据在窗外，玩家没上檐台就不显示交互提示。</summary>
        bool RequiresExterior
        {
            get { return !string.IsNullOrEmpty(evidenceId) && evidenceId.StartsWith("wall_nails"); }
        }

        bool CanInspect(PlayerController player)
        {
            if (!RequiresExterior) return true;
            if (player == null) return false;

            // 必须站在这块墙钉所属房间的檐台踏板上，且离得够近；
            // 旧版只查“是否在任意房间窗外”，大厅北侧等位置会被误判。
            RoomArea ledgeRoom = RoomArea.FindLedgeRoom(player.transform.position);
            if (ledgeRoom == null) return false;
            RoomArea wallRoom = RoomArea.FindExteriorRoom(transform.position);
            if (wallRoom != null && wallRoom != ledgeRoom) return false;

            return Vector3.Distance(player.transform.position, transform.position) <= 3.2f;
        }

        public string GetInteractionPrompt(PlayerController player)
        {
            if (oneShot && _used) return string.Empty;
            if (!CanInspect(player)) return string.Empty;
            return prompt + displayName;
        }

        public void Interact(PlayerController player)
        {
            if (oneShot && _used) return;
            if (!CanInspect(player))
            {
                if (player != null)
                    player.ShowMessage("隔着窗玻璃，你看不清墙上的痕迹。得站到窗外去。", 3f);
                return;
            }
            _used = true;

            EvidenceLedger.Record(evidenceId, displayName, reason, weight, ResolveOrigin());
            if (isVideoLog)
            {
                CycleState.NotifyVideoLogWatched();
                RecordVideoClips();
            }

            if (player != null)
            {
                string message = string.IsNullOrEmpty(foundMessage)
                    ? $"你记下了{displayName}。{reason}"
                    : foundMessage;
                player.ShowMessage(message, 4.5f);
            }

            Debug.Log($"[WorldEvidence] 玩家检查了{displayName}（{evidenceId}）。");
        }

        /// <summary>证据出处：墙钉按所在房间外墙，监控按圆桌，其余留空。</summary>
        string ResolveOrigin()
        {
            if (isVideoLog) return "圆桌监控";

            RoomArea room = RoomArea.FindExteriorRoom(transform.position);
            if (room != null && room.RoomNumber > 0)
                return $"{room.RoomNumber}号房窗外";

            if (!string.IsNullOrEmpty(evidenceId) && evidenceId.StartsWith("wall_nails_"))
            {
                // 兜底：从 id（wall_nails_2_L）里解析房间号。
                string[] parts = evidenceId.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int number))
                    return $"{number}号房窗外";
            }
            return string.Empty;
        }

        /// <summary>
        /// 监控只拍走廊和大厅。看完回放后，把这一轮真实拍到的每一次外出
        /// 拆成可单独引用的片段；没有任何人被拍到时不生成片段。
        /// </summary>
        void RecordVideoClips()
        {
            var clips = SurveillanceLog.BuildClips();
            for (int i = 0; i < clips.Count; i++)
            {
                SurveillanceLog.Clip clip = clips[i];
                EvidenceLedger.Record(clip.id, clip.name, clip.reason, clip.weight);
            }

            if (clips.Count == 0)
            {
                Debug.Log("[WorldEvidence] 监控回放：这一轮没有拍到任何人出房。");
            }
        }
    }
}
