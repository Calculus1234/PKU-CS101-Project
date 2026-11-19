using MBT;
using UnityEngine;

namespace HilbertLoop.BT
{
    /// <summary>
    /// 探头看窗外（3.3）：走到自己房间的窗边，探头看一会儿雪。
    /// 探头期间 IsLeaningOut = true —— 玩家同处一室时可以用鼠标左键将其推出窗外。
    /// 结束时写入下一次探头时间（45~75 秒后），保证每位住客低频、错峰探头。
    /// </summary>
    [AddComponentMenu("")]
    [MBTNode(name = "HilbertLoop/Tasks/Lean Out Window")]
    public class LeanOutWindow : NPCNode
    {
        [Tooltip("探头持续时长（秒）")]
        public float leanSeconds = 6f;

        [Tooltip("窗边站位到达判定距离")]
        public float arriveDistance = 0.6f;

        [Tooltip("下一次探头的最短间隔（秒）")]
        public float minCooldown = 45f;

        [Tooltip("下一次探头的最长间隔（秒）")]
        public float maxCooldown = 75f;

        Vector3 _standPoint;
        Vector3 _lookPoint;
        bool _failed;
        bool _arrived;
        float _leanEndTime;
        bool _leaning;

        WindowController _window;
        bool _openedWindow;

        public override void OnEnter()
        {
            _failed = false;
            _arrived = false;
            _leaning = false;
            _leanEndTime = 0f;

            NPCBlackboard b = Board;
            if (b != null)
                b.nextWindowLeanTime = Time.time + Random.Range(minCooldown, maxCooldown);

            Transform room = CurrentRoom;
            if (room == null)
            {
                Debug.LogWarning($"[NPC {(Npc != null ? Npc.Number : -1)}] 找不到当前房间，取消探头。");
                _failed = true;
                return;
            }

            _standPoint = room.TransformPoint(new Vector3(0f, 0f, 1.5f));
            Transform window = room.Find("Window");
            _lookPoint = window != null ? window.position : room.TransformPoint(new Vector3(0f, 1.5f, 2.4f));

            // 3.5 物理反馈：探头时窗户必须真的打开（玩家才可能把人推出窗外）；
            // 如果窗被反锁，NPC 打不开，放弃本次探头。
            _window = room.GetComponentInChildren<WindowController>(true);
            _openedWindow = false;
            if (_window != null && _window.IsLatchLocked)
            {
                Debug.Log($"[NPC {(Npc != null ? Npc.Number : -1)}] 窗户被反锁，放弃探头。");
                _failed = true;
                return;
            }

            SetWalking(true);
        }

        public override NodeResult Execute()
        {
            NPCController npc = Npc;
            NPCBlackboard b = Board;
            if (npc == null || b == null || _failed) return NodeResult.failure;

            if (!_arrived)
            {
                NodeResult move = MoveTowards(_standPoint, arriveDistance);
                if (move.status == Status.Running) return NodeResult.running;

                _arrived = true;
                SetWalking(false);
                if (Nav != null) Nav.Stop();

                if (_window != null && !_window.IsOpen && !_window.IsLatchLocked)
                {
                    _window.SetOpenInstant(true);
                    _openedWindow = true;
                }

                _leanEndTime = Time.time + leanSeconds;
                _leaning = true;
                npc.SetLeaningOut(true);
                Debug.Log($"[NPC {npc.Number}] 推开窗户探头看雪（IsLeaningOut=true，" +
                          $"窗开={_window != null && _window.IsOpen}）。");
            }

            FaceTowards(_lookPoint);

            if (Time.time >= _leanEndTime || npc.isDead)
            {
                npc.SetLeaningOut(false);
                _leaning = false;
                return NodeResult.success;
            }

            return NodeResult.running;
        }

        public override void OnExit()
        {
            if (_leaning && Npc != null) Npc.SetLeaningOut(false);
            _leaning = false;
            SetWalking(false);
            if (Nav != null) Nav.Stop();

            // 自己推开的窗，离开时顺手关上；若玩家在探头期间把窗反锁了，保持现状。
            if (_openedWindow && _window != null && !_window.IsLatchLocked)
                _window.SetOpenInstant(false);
            _openedWindow = false;
            _window = null;
        }
    }
}
