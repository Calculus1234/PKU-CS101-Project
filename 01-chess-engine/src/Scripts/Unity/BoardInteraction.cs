using System;
using UnityEngine;
using UnityEngine.EventSystems;
using ChessDark.Core;

namespace ChessDark.Unity
{
    public class BoardInteraction : MonoBehaviour
    {
        [Header("引用")]
        public Camera mainCamera;

        [Header("调试")]
        public bool showDebugGizmos = false;
        public Color highlightColor = new Color(0f, 1f, 0f, 0.5f);
        public bool inputEnabled = true;

        [Header("布阵")]
        public bool arrangementMode = false;
        public PieceColor arrangementColor = PieceColor.White;

        private Position? hoveredPosition;
        private Position? selectedPosition;
        private float boardPlaneY = 0.5f;
        private bool dragging;
        private Position dragOrigin;
        private PieceData dragPiece;

        public delegate void OnTileClicked(Position pos);
        public event OnTileClicked TileClicked;

        public delegate void OnPieceSelected(Position pos, PieceData pieceData);
        public event OnPieceSelected PieceSelected;

        public delegate void OnArrangementDragStarted(Position pos, PieceData pieceData);
        public event OnArrangementDragStarted ArrangementDragStarted;

        public delegate void OnArrangementDragMoved(Vector3 boardPoint);
        public event OnArrangementDragMoved ArrangementDragMoved;

        public delegate void OnArrangementDragEnded(Position? target);
        public event OnArrangementDragEnded ArrangementDragEnded;

        public bool IsDragging => dragging;

        void Awake()
        {
            if (mainCamera == null)
                mainCamera = Camera.main;

            var tiles = FindObjectsOfType<TileData>();
            if (tiles.Length > 0)
            {
                var renderer = tiles[0].GetComponent<Renderer>();
                if (renderer != null) boardPlaneY = renderer.bounds.max.y;
            }
        }

        void Update()
        {
            if (!inputEnabled) return;
            if (arrangementMode) HandleArrangementDrag();
            else HandleMouseInteraction();
        }

        // 触摸屏必须用 fingerId 查询，否则手指按在按钮上仍会被判定为“没点到 UI”。
        static bool IsPointerOverUi()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null) return false;
            if (Input.touchCount > 0)
                return eventSystem.IsPointerOverGameObject(Input.GetTouch(0).fingerId);
            return eventSystem.IsPointerOverGameObject();
        }

        void HandleArrangementDrag()
        {
            if (!mainCamera) return;

            bool overUi = IsPointerOverUi();
            Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

            if (!dragging)
            {
                if (overUi || !Input.GetMouseButtonDown(0)) return;

                if (Physics.Raycast(ray, out RaycastHit hit, 1000f))
                {
                    var pieceData = hit.collider.GetComponentInParent<PieceData>();
                    if (pieceData != null && pieceData.pieceColor == arrangementColor)
                    {
                        dragging = true;
                        dragOrigin = pieceData.GetLogicalPosition();
                        dragPiece = pieceData;
                        ArrangementDragStarted?.Invoke(dragOrigin, pieceData);
                        Debug.Log($"[BoardInteraction] 开始拖动棋子 ({dragOrigin.X},{dragOrigin.Y})");
                    }
                }
                return;
            }

            var plane = new Plane(Vector3.up, new Vector3(0f, boardPlaneY, 0f));
            Vector3 boardPoint;
            if (plane.Raycast(ray, out float enter))
                boardPoint = ray.GetPoint(enter);
            else
                boardPoint = dragPiece != null ? dragPiece.transform.position : Vector3.zero;

            ArrangementDragMoved?.Invoke(boardPoint);

            if (Input.GetMouseButtonUp(0))
            {
                Position? target = null;
                if (!overUi)
                {
                    var hits = Physics.RaycastAll(ray, 1000f);
                    System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                    foreach (var hit in hits)
                    {
                        var hitPiece = hit.collider.GetComponentInParent<PieceData>();
                        if (hitPiece != null)
                        {
                            // 拖拽中的棋子本身就在指针下，必须跳过，否则永远只能落回原位。
                            if (hitPiece == dragPiece) continue;
                            target = hitPiece.GetLogicalPosition();
                            break;
                        }

                        var hitTile = hit.collider.GetComponentInParent<TileData>();
                        if (hitTile != null)
                        {
                            target = hitTile.GetLogicalPosition();
                            break;
                        }
                    }
                }

                dragging = false;
                dragPiece = null;
                ArrangementDragEnded?.Invoke(target);
            }
        }

        void HandleMouseInteraction()
        {
            if (dragging)
            {
                dragging = false;
                dragPiece = null;
            }

            if (!mainCamera) return;
            if (IsPointerOverUi()) return;

            Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit, 1000f))
            {
                var pieceData = hit.collider.GetComponentInParent<PieceData>();
                if (pieceData != null)
                {
                    Position pos = pieceData.GetLogicalPosition();
                    hoveredPosition = pos;

                    if (Input.GetMouseButtonDown(0))
                        SelectPiece(pos, pieceData);
                    return;
                }

                var tileData = hit.collider.GetComponentInParent<TileData>();
                if (tileData != null)
                {
                    Position pos = tileData.GetLogicalPosition();
                    hoveredPosition = pos;

                    if (Input.GetMouseButtonDown(0))
                        ClickTile(pos);
                    return;
                }
            }

            hoveredPosition = null;
        }

        void SelectPiece(Position pos, PieceData pieceData)
        {
            selectedPosition = pos;
            PieceSelected?.Invoke(pos, pieceData);
            Debug.Log($"[BoardInteraction] 选中棋子: ({pos.X},{pos.Y}) - {pieceData.visibleType} ({pieceData.pieceColor})");
        }

        void ClickTile(Position pos)
        {
            TileClicked?.Invoke(pos);
            Debug.Log($"[BoardInteraction] 点击格子: ({pos.X},{pos.Y})");
        }

        void OnDrawGizmos()
        {
            if (!showDebugGizmos) return;

            if (hoveredPosition.HasValue)
                DrawPositionHighlight(hoveredPosition.Value, highlightColor);

            if (selectedPosition.HasValue)
                DrawPositionHighlight(selectedPosition.Value, Color.yellow);
        }

        void DrawPositionHighlight(Position pos, Color color)
        {
            var tileObj = GameObject.Find($"Tile_{pos.X}_{pos.Y}");
            if (tileObj == null) return;

            var renderer = tileObj.GetComponent<Renderer>();
            if (renderer == null) return;

            Gizmos.color = color;
            Gizmos.DrawWireCube(renderer.bounds.center, renderer.bounds.size + Vector3.up * 0.05f);
        }

        public Position? GetHoveredPosition() => hoveredPosition;

        public Position? GetSelectedPosition() => selectedPosition;

        public void ClearSelection()
        {
            selectedPosition = null;
        }
    }
}
