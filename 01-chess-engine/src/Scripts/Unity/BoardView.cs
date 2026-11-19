using UnityEngine;
using ChessDark.Core;
using ChessDark.Logic;
using System.Collections;
using System.Collections.Generic;

namespace ChessDark.Unity
{
    public class BoardView : MonoBehaviour
    {
        [Header("Debug")]
        public bool showPieceInfo = false;

        [Header("棋子模型")]
        [Tooltip("按 King、Queen、Rook、Bishop、Knight、Pawn 顺序填入模型 Prefab")]
        public GameObject[] pieceModels = new GameObject[6];

        const float TileSize = 2.5f;
        static readonly float[] PieceDisplayHeights = { 0f, 2.70f, 2.60f, 2.10f, 2.20f, 2.10f, 1.75f };

        class PieceSnapshot
        {
            public GameObject obj;
            public Position pos;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 localScale;
            public PieceType visibleType;
            public PieceType trueType;
        }

        class PieceVisual
        {
            public GameObject root;
            public PieceData data;
            public GameObject model;
            public PieceType shownType = PieceType.None;
            public Material factionMaterial;
        }

        private readonly Dictionary<PieceData, PieceVisual> pieceVisuals = new Dictionary<PieceData, PieceVisual>();
        private readonly float[] modelScales = new float[7];
        private PieceColor viewPlayerColor = PieceColor.White;
        private Dictionary<Position, GameObject> pieceCache;
        private Dictionary<Position, GameObject> tileCache;
        private readonly Dictionary<Renderer, Color> tileBaseColors = new Dictionary<Renderer, Color>();
        private readonly HashSet<Renderer> highlightedTiles = new HashSet<Renderer>();
        private readonly List<PieceSnapshot> initialPieces = new List<PieceSnapshot>();
        private GameObject draggedObject;
        private Position draggedOrigin;

        void Awake()
        {
            pieceCache = new Dictionary<Position, GameObject>();
            tileCache = new Dictionary<Position, GameObject>();
            BuildCaches();
            CaptureInitialPieces();
            PreparePieceVisuals();
        }

        void BuildCaches()
        {
            pieceCache.Clear();
            tileCache.Clear();

            var pieces = GameObject.FindGameObjectsWithTag("ChessPiece");
            foreach (var pieceObj in pieces)
            {
                var pieceData = pieceObj.GetComponent<PieceData>();
                if (pieceData != null)
                {
                    Position pos = pieceData.GetLogicalPosition();
                    pieceCache[pos] = pieceObj;
                }
            }

            var tiles = GameObject.FindGameObjectsWithTag("ChessTile");
            foreach (var tileObj in tiles)
            {
                var tileData = tileObj.GetComponent<TileData>();
                if (tileData != null)
                {
                    Position pos = tileData.GetLogicalPosition();
                    tileCache[pos] = tileObj;
                }
            }

            Debug.Log($"[BoardView] Cache built: {pieceCache.Count} pieces, {tileCache.Count} tiles");
        }

        void CaptureInitialPieces()
        {
            initialPieces.Clear();
            foreach (var kvp in pieceCache)
            {
                var data = kvp.Value.GetComponent<PieceData>();
                if (data == null) continue;
                initialPieces.Add(new PieceSnapshot
                {
                    obj = kvp.Value,
                    pos = kvp.Key,
                    position = kvp.Value.transform.position,
                    rotation = kvp.Value.transform.rotation,
                    localScale = kvp.Value.transform.localScale,
                    visibleType = data.visibleType,
                    trueType = data.trueType
                });
            }
        }

        void PreparePieceVisuals()
        {
            foreach (var pieceObj in pieceCache.Values)
            {
                if (pieceObj == null) continue;
                var data = pieceObj.GetComponent<PieceData>();
                if (data == null || pieceVisuals.ContainsKey(data)) continue;

                Material template = null;
                var renderers = pieceObj.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null) continue;
                    if (template == null) template = renderers[i].sharedMaterial;
                    renderers[i].enabled = false;
                }

                pieceVisuals[data] = new PieceVisual
                {
                    root = pieceObj,
                    data = data,
                    factionMaterial = template
                };
            }
        }

        void RefreshPieceModel(PieceData data)
        {
            if (data == null) return;
            PieceVisual visual;
            if (!pieceVisuals.TryGetValue(data, out visual)) return;

            PieceType type = data.pieceColor == viewPlayerColor ? data.trueType : data.visibleType;
            if (type == PieceType.None || visual.shownType == type) return;

            if (visual.model != null)
            {
                visual.model.SetActive(false);
                Destroy(visual.model);
                visual.model = null;
            }

            GameObject prefab = GetModelPrefab(type);
            if (prefab == null)
            {
                Debug.LogWarning($"[BoardView] 未绑定 {type} 的棋子模型");
                visual.shownType = PieceType.None;
                return;
            }

            GameObject model = Instantiate(prefab, visual.root.transform, false);
            model.name = "Model_" + type;
            model.transform.localRotation = Quaternion.identity;

            float rootScale = Mathf.Abs(visual.root.transform.lossyScale.x);
            model.transform.localScale = Vector3.one * (GetModelScale(type) / Mathf.Max(rootScale, 0.0001f));
            ApplyFactionMaterial(model, visual.factionMaterial);

            // 根节点只当锚点：模型底面贴住根节点、水平居中，吸附和拖拽逻辑保持不变。
            Bounds modelBounds = WorldBounds(model);
            Vector3 anchor = visual.root.transform.position;
            model.transform.position += new Vector3(
                anchor.x - modelBounds.center.x,
                anchor.y - modelBounds.min.y,
                anchor.z - modelBounds.center.z);

            visual.model = model;
            visual.shownType = type;
            UpdatePieceCollider(visual.root);
        }

        GameObject GetModelPrefab(PieceType type)
        {
            int index = (int)type - 1;
            if (pieceModels == null || index < 0 || index >= pieceModels.Length) return null;
            return pieceModels[index];
        }

        float GetModelScale(PieceType type)
        {
            int index = (int)type;
            if (index <= 0 || index >= modelScales.Length) return 1f;
            if (modelScales[index] > 0f) return modelScales[index];

            GameObject prefab = GetModelPrefab(type);
            if (prefab == null) return 1f;

            GameObject probe = Instantiate(prefab);
            probe.transform.position = Vector3.zero;
            probe.transform.rotation = Quaternion.identity;
            probe.transform.localScale = Vector3.one;

            Bounds b = WorldBounds(probe);
            float scale = b.size.y > 0.0001f ? PieceDisplayHeights[index] / b.size.y : 1f;
            float width = Mathf.Max(b.size.x, b.size.z);
            if (width > 0.0001f && width * scale > TileSize * 0.72f)
                scale = TileSize * 0.72f / width;

            probe.SetActive(false);
            Destroy(probe);

            modelScales[index] = scale;
            return scale;
        }

        static void ApplyFactionMaterial(GameObject model, Material material)
        {
            if (material == null) return;
            var renderers = model.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
                renderers[i].sharedMaterial = material;
        }

        static void UpdatePieceCollider(GameObject root)
        {
            var box = root.GetComponent<BoxCollider>();
            if (box == null) return;

            Bounds b = WorldBounds(root);
            if (b.size.y <= 0f) return;

            box.center = root.transform.InverseTransformPoint(b.center);
            Vector3 s = root.transform.lossyScale;
            box.size = new Vector3(
                b.size.x / Mathf.Max(Mathf.Abs(s.x), 0.0001f),
                b.size.y / Mathf.Max(Mathf.Abs(s.y), 0.0001f),
                b.size.z / Mathf.Max(Mathf.Abs(s.z), 0.0001f));
        }

        public GameObject GetPieceObject(Position pos)
        {
            if (pieceCache.TryGetValue(pos, out GameObject pieceObj))
                return pieceObj;
            return null;
        }

        public GameObject GetTileObject(Position pos)
        {
            if (tileCache.TryGetValue(pos, out GameObject tileObj))
                return tileObj;
            return null;
        }

        public Dictionary<Position, GameObject> GetAllPieceObjects()
        {
            return new Dictionary<Position, GameObject>(pieceCache);
        }

        public void HighlightPosition(Position pos, Color color, float duration = 0f)
        {
            var tileObj = GetTileObject(pos);
            if (tileObj == null) return;

            var renderer = tileObj.GetComponent<Renderer>();
            if (renderer == null) return;

            if (!tileBaseColors.TryGetValue(renderer, out Color baseColor))
            {
                baseColor = ReadBaseColor(renderer);
                tileBaseColors[renderer] = baseColor;
            }

            SetTileColor(renderer, color);
            highlightedTiles.Add(renderer);

            if (duration > 0f)
                StartCoroutine(FlashHighlight(renderer, baseColor, duration));
        }

        public void HighlightPositions(List<Position> positions, Color? color = null, float duration = 0f)
        {
            Color highlightColor = color ?? new Color(0.25f, 0.95f, 0.35f, 1f);

            foreach (var pos in positions)
            {
                HighlightPosition(pos, highlightColor, duration);
            }
        }

        public void ClearHighlights()
        {
            foreach (var renderer in highlightedTiles)
            {
                if (renderer == null) continue;
                Color baseColor = tileBaseColors.TryGetValue(renderer, out Color c)
                    ? c
                    : ReadBaseColor(renderer);
                SetTileColor(renderer, baseColor);
            }
            highlightedTiles.Clear();
        }

        public IEnumerator AnimateMove(Move move, MoveResult result)
        {
            const float SlideDuration = 0.25f;
            const float CaptureDuration = 0.3f;

            GameObject captured = null;
            Vector3 capturedScale = Vector3.one;
            if (result != null && result.CapturedPiece != null &&
                pieceCache.TryGetValue(move.To, out captured) && captured != null)
            {
                capturedScale = captured.transform.localScale;
                pieceCache.Remove(move.To);
            }

            // 易位车必须先认出来：等王写进 move.To 之后，角落查询会命中王自己。
            GameObject rook = null;
            Vector3 rookStart = Vector3.zero;
            Vector3 rookTarget = Vector3.zero;
            Position rookTo = default(Position);
            if (move.IsCastling)
            {
                int y = move.From.Y;
                int rookFromX = -1;
                int dir = move.To.X > move.From.X ? 1 : -1;
                for (int x = move.From.X + dir; x >= 0 && x < 8; x += dir)
                {
                    if (pieceCache.TryGetValue(new Position(x, y), out var candidate) && candidate != null)
                    {
                        rook = candidate;
                        rookFromX = x;
                        break;
                    }
                }

                if (rook != null)
                {
                    int rookToX = move.To.X > move.From.X ? move.To.X - 1 : move.To.X + 1;
                    rookTo = new Position(rookToX, y);
                    rookStart = rook.transform.position;
                    rookTarget = rookStart + GetSnapOffset(rook, rookTo);
                    if (rookFromX != rookToX)
                        pieceCache.Remove(new Position(rookFromX, y));
                    SetDataPosition(rook, rookTo);
                }
            }

            GameObject mover = null;
            Vector3 moverStart = Vector3.zero;
            Vector3 moverTarget = Vector3.zero;
            if (pieceCache.TryGetValue(move.From, out mover) && mover != null)
            {
                moverStart = mover.transform.position;
                moverTarget = moverStart + GetSnapOffset(mover, move.To);
                pieceCache.Remove(move.From);
                SetDataPosition(mover, move.To);
                pieceCache[move.To] = mover;
            }

            if (rook != null)
                pieceCache[rookTo] = rook;

            float duration = captured != null ? CaptureDuration : SlideDuration;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                // 卡顿帧里 deltaTime 可能直接超过整段动画时长，钳住单步避免棋子瞬移。
                float step = Mathf.Min(Time.deltaTime, 0.05f);
                elapsed += step;
                float slideT = Mathf.Clamp01(elapsed / SlideDuration);
                float smooth = slideT * slideT * (3f - 2f * slideT);

                if (mover != null)
                    mover.transform.position = Vector3.Lerp(moverStart, moverTarget, smooth);
                if (rook != null)
                    rook.transform.position = Vector3.Lerp(rookStart, rookTarget, smooth);
                if (captured != null)
                {
                    float shrinkT = Mathf.Clamp01(elapsed / CaptureDuration);
                    float scale = Mathf.Max(0.001f, 1f - shrinkT * shrinkT);
                    captured.transform.localScale = capturedScale * scale;
                }

                yield return null;
            }

            if (mover != null)
            {
                mover.transform.position = moverTarget;
                SnapToTile(mover, move.To);
            }

            if (rook != null)
                SnapToTile(rook, rookTo);

            if (captured != null)
            {
                captured.transform.localScale = capturedScale;
                captured.SetActive(false);
            }

            if (move.PromotionType.HasValue && mover != null)
            {
                var data = mover.GetComponent<PieceData>();
                if (data != null)
                {
                    data.trueType = move.PromotionType.Value;
                    RefreshPieceModel(data);
                }
            }

            ClearHighlights();
        }

        void SnapToTile(GameObject obj, Position pos)
        {
            if (obj == null) return;
            obj.transform.position += GetSnapOffset(obj, pos);
        }

        Vector3 GetSnapOffset(GameObject obj, Position pos)
        {
            var tileObj = GetTileObject(pos);
            if (tileObj == null || obj == null) return Vector3.zero;

            Bounds tileBounds = WorldBounds(tileObj);
            Bounds pieceBounds = WorldBounds(obj);
            return new Vector3(
                tileBounds.center.x - pieceBounds.center.x,
                tileBounds.max.y + 0.01f - pieceBounds.min.y,
                tileBounds.center.z - pieceBounds.center.z);
        }

        public void SyncPieceDataFromBoard(Board board, PieceColor playerColor)
        {
            viewPlayerColor = playerColor;

            foreach (var (pos, piece) in board.GetAllPieces())
            {
                var obj = GetPieceObject(pos);
                if (obj == null) continue;
                var data = obj.GetComponent<PieceData>();
                if (data == null) continue;

                data.visibleType = piece.VisibleType;
                if (piece.Color == playerColor) data.trueType = piece.TrueType;
            }

            foreach (var visual in pieceVisuals.Values)
                RefreshPieceModel(visual.data);
        }

        // 时光回溯整盘重绘：所有物理棋子先归位清态，再按棋盘真值重新分配类型与格子。
        public void RenderBoardState(Board board, PieceColor playerColor)
        {
            viewPlayerColor = playerColor;
            draggedObject = null;
            ClearHighlights();

            if (board == null)
            {
                RestoreInitialPieces();
                foreach (var visual in pieceVisuals.Values) RefreshPieceModel(visual.data);
                return;
            }

            var used = new HashSet<GameObject>();
            foreach (var snapshot in initialPieces)
            {
                if (snapshot.obj == null) continue;
                snapshot.obj.SetActive(true);
                snapshot.obj.transform.SetPositionAndRotation(snapshot.position, snapshot.rotation);
                snapshot.obj.transform.localScale = snapshot.localScale;

                var data = snapshot.obj.GetComponent<PieceData>();
                if (data == null) continue;
                data.logicalX = snapshot.pos.X;
                data.logicalY = snapshot.pos.Y;
                data.visibleType = snapshot.visibleType;
                data.trueType = snapshot.trueType;
            }

            pieceCache.Clear();

            foreach (var (pos, piece) in board.GetAllPieces())
            {
                PieceType required = piece.Color == playerColor ? piece.TrueType : piece.VisibleType;
                var chosen = TakePooledPiece(used, piece.Color, required);
                if (chosen == null) continue;

                var data = chosen.obj.GetComponent<PieceData>();
                used.Add(chosen.obj);

                data.visibleType = piece.VisibleType;
                if (piece.Color == playerColor) data.trueType = piece.TrueType;
                RefreshPieceModel(data);

                SnapToTile(chosen.obj, pos);
                SetDataPosition(chosen.obj, pos);
                pieceCache[pos] = chosen.obj;
            }

            foreach (var snapshot in initialPieces)
            {
                if (snapshot.obj == null || used.Contains(snapshot.obj)) continue;
                snapshot.obj.SetActive(false);
            }

            RebuildCaches();
        }

        PieceSnapshot TakePooledPiece(HashSet<GameObject> used, PieceColor color, PieceType required)
        {
            PieceSnapshot fallback = null;
            foreach (var snapshot in initialPieces)
            {
                if (snapshot.obj == null || used.Contains(snapshot.obj)) continue;
                var data = snapshot.obj.GetComponent<PieceData>();
                if (data == null || data.pieceColor != color) continue;

                PieceType shown = color == viewPlayerColor ? data.trueType : data.visibleType;
                if (shown == required) return snapshot;
                if (fallback == null) fallback = snapshot;
            }
            return fallback;
        }

        // 联机布阵同步：对方只发位置+伪装态，这里按伪装态把敌方模型搬到对应格子。
        public void SyncEnemyFormationFromBoard(Board board, PieceColor enemyColor)
        {
            var byType = new Dictionary<PieceType, List<PieceData>>();
            foreach (var data in GetAllPieceData())
            {
                if (data == null || data.pieceColor != enemyColor) continue;
                List<PieceData> list;
                if (!byType.TryGetValue(data.visibleType, out list))
                {
                    list = new List<PieceData>();
                    byType[data.visibleType] = list;
                }
                list.Add(data);
            }

            var oldKeys = new List<Position>();
            foreach (var kvp in pieceCache)
            {
                var data = kvp.Value != null ? kvp.Value.GetComponent<PieceData>() : null;
                if (data != null && data.pieceColor == enemyColor) oldKeys.Add(kvp.Key);
            }
            foreach (var pos in oldKeys) pieceCache.Remove(pos);

            foreach (var (pos, piece) in board.GetAllPieces())
            {
                if (piece.Color != enemyColor) continue;

                List<PieceData> candidates;
                if (!byType.TryGetValue(piece.VisibleType, out candidates) || candidates.Count == 0)
                    continue;

                var chosen = candidates[0];
                candidates.RemoveAt(0);

                var obj = chosen.gameObject;
                obj.SetActive(true);
                SnapToTile(obj, pos);
                SetDataPosition(obj, pos);
                chosen.visibleType = piece.VisibleType;
                chosen.trueType = piece.VisibleType;
                RefreshPieceModel(chosen);
                pieceCache[pos] = obj;
            }

            foreach (var list in byType.Values)
            {
                foreach (var data in list)
                {
                    if (data != null) data.gameObject.SetActive(false);
                }
            }
        }

        public List<PieceData> GetAllPieceData()
        {
            var result = new List<PieceData>();
            foreach (var obj in pieceCache.Values)
            {
                if (obj == null) continue;
                var data = obj.GetComponent<PieceData>();
                if (data != null) result.Add(data);
            }
            return result;
        }

        public void BeginArrangementDrag(Position pos)
        {
            if (!pieceCache.TryGetValue(pos, out GameObject obj) || obj == null) return;
            draggedObject = obj;
            draggedOrigin = pos;
            pieceCache.Remove(pos);
        }

        public void UpdateArrangementDrag(Vector3 boardPoint)
        {
            if (draggedObject == null) return;
            float halfHeight = WorldBounds(draggedObject).extents.y;
            draggedObject.transform.position = new Vector3(boardPoint.x, boardPoint.y + halfHeight, boardPoint.z);
        }

        public void EndArrangementDrag(Position from, Position to)
        {
            if (draggedObject == null) return;
            var obj = draggedObject;
            draggedObject = null;

            if (to.IsValid && to != from && pieceCache.TryGetValue(to, out GameObject other) && other != null)
            {
                SnapToTile(obj, to);
                SetDataPosition(obj, to);
                SnapToTile(other, from);
                SetDataPosition(other, from);
                pieceCache[from] = other;
                pieceCache[to] = obj;
            }
            else
            {
                SnapToTile(obj, from);
                SetDataPosition(obj, from);
                pieceCache[from] = obj;
            }

            ClearHighlights();
        }

        public void CancelArrangementDrag(Position from)
        {
            if (draggedObject == null) return;
            var obj = draggedObject;
            draggedObject = null;
            SnapToTile(obj, from);
            SetDataPosition(obj, from);
            pieceCache[from] = obj;
            ClearHighlights();
        }

        static void SetDataPosition(GameObject obj, Position pos)
        {
            var data = obj.GetComponent<PieceData>();
            if (data == null) return;
            data.logicalX = pos.X;
            data.logicalY = pos.Y;
        }

        public void RestoreInitialPieces()
        {
            draggedObject = null;
            foreach (var snapshot in initialPieces)
            {
                if (snapshot.obj == null) continue;
                snapshot.obj.SetActive(true);
                snapshot.obj.transform.SetPositionAndRotation(snapshot.position, snapshot.rotation);
                snapshot.obj.transform.localScale = snapshot.localScale;

                var data = snapshot.obj.GetComponent<PieceData>();
                if (data == null) continue;
                data.logicalX = snapshot.pos.X;
                data.logicalY = snapshot.pos.Y;
                data.visibleType = snapshot.visibleType;
                data.trueType = snapshot.trueType;
            }
            ClearHighlights();
            RebuildCaches();
        }

        public void ApplyFactionColor(GameObject pieceObj, PieceColor color)
        {
            var renderers = pieceObj.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);

                if (color == PieceColor.White)
                {
                    block.SetColor("_BaseColor", new Color(0.75f, 0.78f, 0.82f, 1f));
                    block.SetFloat("_Smoothness", 0.6f);
                }
                else
                {
                    block.SetColor("_BaseColor", new Color(0.12f, 0.1f, 0.09f, 1f));
                    block.SetColor("_EmissionColor", new Color(0.25f, 0f, 0f, 1f) * 0.2f);
                    block.SetFloat("_Smoothness", 0.4f);
                }

                renderer.SetPropertyBlock(block);
            }
        }

        public void ResetPieceMaterial(GameObject pieceObj)
        {
            var renderers = pieceObj.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                renderer.SetPropertyBlock(null);
            }
        }

        public void RebuildCaches()
        {
            BuildCaches();
        }

        static Color ReadBaseColor(Renderer renderer)
        {
            var mat = renderer.sharedMaterial;
            if (mat != null && mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor");
            if (mat != null) return mat.color;
            return Color.white;
        }

        static void SetTileColor(Renderer renderer, Color color)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(block);
        }

        static Bounds WorldBounds(GameObject obj)
        {
            var renderers = obj.GetComponentsInChildren<Renderer>();
            Bounds bounds = new Bounds(obj.transform.position, Vector3.zero);
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || !renderers[i].enabled) continue;
                if (!hasBounds)
                {
                    bounds = renderers[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }
            return bounds;
        }

        System.Collections.IEnumerator FlashHighlight(Renderer renderer, Color originalColor, float duration)
        {
            yield return new WaitForSeconds(duration);
            if (renderer == null) yield break;
            SetTileColor(renderer, originalColor);
            highlightedTiles.Remove(renderer);
        }

        void OnDrawGizmosSelected()
        {
            if (!showPieceInfo) return;

            Gizmos.color = Color.cyan;
            foreach (var kvp in pieceCache)
            {
                Position pos = kvp.Key;
                GameObject pieceObj = kvp.Value;

                if (pieceObj != null)
                {
                    Vector3 center = pieceObj.transform.position + Vector3.up * 0.5f;
                    Gizmos.DrawWireSphere(center, 0.3f);

#if UNITY_EDITOR
                    UnityEditor.Handles.Label(center + Vector3.up * 0.5f,
                        $"({pos.X},{pos.Y})\n{pieceObj.name}");
#endif
                }
            }
        }
    }
}
