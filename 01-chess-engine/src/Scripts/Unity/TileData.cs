using UnityEngine;
using ChessDark.Core;

namespace ChessDark.Unity
{
    public class TileData : MonoBehaviour
    {
        public int logicalX;
        public int logicalY;

        public Position GetLogicalPosition() => new Position(logicalX, logicalY);
    }
}
