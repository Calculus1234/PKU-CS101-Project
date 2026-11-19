using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using ChessDark.Core;
using ChessDark.Unity;

namespace ChessDark.EditorTools
{
    public static class QuickBoardGenerator
    {
        const string AssetDir = "Assets/ChessPieces/";

        const float TileSize = 2.5f;
        const float TileGap = 0.02f;
        const float BaseFootprint = 34f;
        const float RecessFloorLocalY = 0.00133f;
        const string BoardRootName = "=== GIANT_CHESS_BOARD ===";

        static readonly string[] PieceFiles = { "Pawn", "Knight", "Bishop", "Rook", "Queen", "King" };
        static readonly float[] PieceHeights = { 1.75f, 2.10f, 2.20f, 2.10f, 2.60f, 2.70f };
        static readonly PieceType[] PieceTypes =
        {
            PieceType.Pawn, PieceType.Knight, PieceType.Bishop,
            PieceType.Rook, PieceType.Queen, PieceType.King
        };
        static readonly int[] BackRowFiles = { 3, 1, 2, 4, 5, 2, 1, 3 };

        [MenuItem("Tools/Chess Dark/Generate Giant Board (Quick)")]
        public static void Generate()
        {
            var whiteTile = Load("白格子.fbx");
            var blackTile = Load("黑格子.fbx");
            var baseFbx = Load("底座.fbx");
            var pieceFbx = new GameObject[PieceFiles.Length];
            for (int i = 0; i < PieceFiles.Length; i++) pieceFbx[i] = Load("Piece_" + PieceFiles[i] + ".fbx");

            if (whiteTile == null || blackTile == null || baseFbx == null)
            {
                Debug.LogError("QuickBoardGenerator: tile or base FBX missing under " + AssetDir);
                return;
            }
            for (int i = 0; i < pieceFbx.Length; i++)
            {
                if (pieceFbx[i] == null)
                {
                    Debug.LogError("QuickBoardGenerator: missing Piece_" + PieceFiles[i] + ".fbx");
                    return;
                }
            }

            ClearGenerated();
            float pitch = TileSize + TileGap;
            var root = new GameObject(BoardRootName);

            var baseObj = (GameObject)Object.Instantiate(baseFbx, root.transform);
            BuildBase(baseObj);

            float tileHeight = MeasureTileHeight(whiteTile);
            BuildTiles(root.transform, whiteTile, blackTile, pitch);
            BuildPieces(root.transform, pieceFbx, pitch, tileHeight);
            FitCamera();

            EditorSceneManager.MarkAllScenesDirty();
            Debug.Log("QuickBoardGenerator: board rebuilt (" + TileSize + "m tiles, " + BaseFootprint + "m base)");
        }

        static GameObject Load(string fileName)
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>(AssetDir + fileName);
        }

        static void ClearGenerated()
        {
            var existing = GameObject.Find(BoardRootName);
            if (existing != null) Object.DestroyImmediate(existing);

            var legacyNames = new[] { "Base_Platform", "Tiles", "Pieces" };
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                for (int k = 0; k < legacyNames.Length; k++)
                    if (roots[i].name == legacyNames[k]) Object.DestroyImmediate(roots[i]);

            var all = Object.FindObjectsOfType<GameObject>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && (all[i].tag == "ChessTile" || all[i].tag == "ChessPiece"))
                    Object.DestroyImmediate(all[i]);
        }

        static Bounds GetBounds(GameObject obj)
        {
            var renderers = obj.GetComponentsInChildren<Renderer>();
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        static void BuildBase(GameObject baseObj)
        {
            baseObj.name = "Base_Platform";
            baseObj.transform.position = Vector3.zero;
            baseObj.transform.rotation = Quaternion.identity;

            Vector3 importScale = baseObj.transform.localScale;
            Bounds raw = GetBounds(baseObj);
            float localSizeX = raw.size.x / importScale.x;
            float localSizeZ = raw.size.z / importScale.z;
            float uniform = BaseFootprint / Mathf.Max(localSizeX, localSizeZ);

            baseObj.transform.localScale = new Vector3(uniform, uniform, uniform);

            Bounds scaled = GetBounds(baseObj);
            baseObj.transform.position = new Vector3(
                -scaled.center.x,
                -RecessFloorLocalY * uniform,
                -scaled.center.z);
        }

        static void FitCamera()
        {
            var camObj = GameObject.Find("Main Camera");
            if (camObj == null) return;
            var cam = camObj.GetComponent<Camera>();
            if (cam == null) return;

            var framer = camObj.GetComponent<GiantBoardCameraFramer>();
            if (framer == null) framer = camObj.AddComponent<GiantBoardCameraFramer>();

            framer.pitch = 40f;
            framer.fieldOfView = 60f;
            framer.targetHeight = 2f;
            framer.boardScreenWidth = 0.68f;
            framer.FrameForAspect(16f / 9f);
        }

        static float MeasureTileHeight(GameObject tileFbx)
        {
            var proto = (GameObject)Object.Instantiate(tileFbx);
            proto.transform.position = Vector3.zero;
            proto.transform.rotation = Quaternion.identity;
            proto.transform.localScale = Vector3.one;

            float scale = TileSize / Mathf.Max(GetBounds(proto).size.x, GetBounds(proto).size.z);
            proto.transform.localScale = new Vector3(scale, scale, scale);
            float height = GetBounds(proto).size.y;
            Object.DestroyImmediate(proto);
            return height;
        }

        static void BuildTiles(Transform parent, GameObject whiteTile, GameObject blackTile, float pitch)
        {
            var tilesRoot = new GameObject("Tiles").transform;
            tilesRoot.SetParent(parent, false);

            var proto = (GameObject)Object.Instantiate(whiteTile, tilesRoot);
            proto.transform.position = Vector3.zero;
            proto.transform.rotation = Quaternion.identity;
            proto.transform.localScale = Vector3.one;
            float scale = TileSize / Mathf.Max(GetBounds(proto).size.x, GetBounds(proto).size.z);
            Object.DestroyImmediate(proto);

            for (int row = 0; row < 8; row++)
            {
                for (int col = 0; col < 8; col++)
                {
                    // 标准棋盘：a1 黑、h1 白；旋转到黑方视角时 a8 也应为白。
                    bool isWhite = (row + col) % 2 == 1;
                    var tile = (GameObject)Object.Instantiate(isWhite ? whiteTile : blackTile, tilesRoot);
                    tile.name = "Tile_" + col + "_" + row;
                    tile.tag = "ChessTile";
                    tile.transform.rotation = Quaternion.identity;
                    tile.transform.localScale = new Vector3(scale, scale, scale);
                    tile.transform.position = Vector3.zero;

                    Bounds b = GetBounds(tile);
                    float x = (col - 3.5f) * pitch;
                    float z = (row - 3.5f) * pitch;
                    tile.transform.position = new Vector3(x - b.center.x, -b.min.y, z - b.center.z);
                    AddBoundsCollider(tile);

                    var data = tile.AddComponent<TileData>();
                    data.logicalX = col;
                    data.logicalY = row;
                }
            }
        }

        static void BuildPieces(Transform parent, GameObject[] pieceFbx, float pitch, float tileHeight)
        {
            var piecesRoot = new GameObject("Pieces").transform;
            piecesRoot.SetParent(parent, false);

            var scales = new float[pieceFbx.Length];
            for (int i = 0; i < pieceFbx.Length; i++)
            {
                var proto = (GameObject)Object.Instantiate(pieceFbx[i]);
                proto.transform.position = Vector3.zero;
                proto.transform.rotation = Quaternion.identity;
                proto.transform.localScale = Vector3.one;

                Bounds b = GetBounds(proto);
                float s = PieceHeights[i] / b.size.y;
                float width = Mathf.Max(b.size.x, b.size.z);
                if (width * s > TileSize * 0.72f) s = TileSize * 0.72f / width;
                scales[i] = s;
                Object.DestroyImmediate(proto);
            }

            float tileTop = tileHeight + 0.01f;
            for (int layout = 0; layout < 4; layout++)
            {
                bool isBlack = layout >= 2;
                bool isBackRow = layout == 0 || layout == 2;
                int row = isBlack ? (isBackRow ? 7 : 6) : (isBackRow ? 0 : 1);
                Color tint = isBlack ? new Color(0.45f, 0.42f, 0.42f) : new Color(2.40f, 2.38f, 2.30f);

                for (int col = 0; col < 8; col++)
                {
                    int type = isBackRow ? BackRowFiles[col] : 0;
                    var piece = (GameObject)Object.Instantiate(pieceFbx[type], piecesRoot);
                    piece.name = (isBlack ? "Black_" : "White_") + PieceFiles[type] + "_" + col;
                    piece.tag = "ChessPiece";
                    piece.transform.rotation = Quaternion.identity;
                    piece.transform.localScale = new Vector3(scales[type], scales[type], scales[type]);
                    piece.transform.position = Vector3.zero;

                    Bounds b = GetBounds(piece);
                    float x = (col - 3.5f) * pitch;
                    float z = (row - 3.5f) * pitch;
                    piece.transform.position = new Vector3(x - b.center.x, tileTop - b.min.y, z - b.center.z);

                    ApplyFactionTint(piece, tint, isBlack);
                    AddBoundsCollider(piece);

                    var data = piece.AddComponent<PieceData>();
                    data.logicalX = col;
                    data.logicalY = row;
                    data.pieceColor = isBlack ? PieceColor.Black : PieceColor.White;
                    data.visibleType = PieceTypes[type];
                    data.trueType = PieceTypes[type];
                }
            }
        }

        static void AddBoundsCollider(GameObject obj)
        {
            if (obj.GetComponentInChildren<Collider>() != null) return;

            Bounds b = GetBounds(obj);
            var box = obj.AddComponent<BoxCollider>();
            box.center = obj.transform.InverseTransformPoint(b.center);
            Vector3 s = obj.transform.lossyScale;
            box.size = new Vector3(
                b.size.x / Mathf.Abs(s.x),
                b.size.y / Mathf.Abs(s.y),
                b.size.z / Mathf.Abs(s.z));
        }

        static void ApplyFactionTint(GameObject piece, Color tint, bool isBlack)
        {
            var shared = AssetDatabase.LoadAssetAtPath<Material>(
                AssetDir + "Generated/" + (isBlack ? "FactionBlack.mat" : "FactionWhite.mat"));

            var renderers = piece.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                var src = renderers[i].sharedMaterials;
                var dst = new Material[src.Length];
                for (int m = 0; m < src.Length; m++)
                {
                    if (shared != null)
                    {
                        dst[m] = shared;
                        continue;
                    }
                    if (src[m] == null) continue;
                    var mat = new Material(src[m]);
                    mat.SetColor("_BaseColor", tint);
                    if (isBlack)
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", new Color(0.03f, 0.004f, 0.004f));
                        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    }
                    else
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", new Color(0.35f, 0.35f, 0.33f));
                        mat.SetFloat("_Smoothness", 0.35f);
                        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    }
                    dst[m] = mat;
                }
                renderers[i].sharedMaterials = dst;
            }
        }
    }
}
