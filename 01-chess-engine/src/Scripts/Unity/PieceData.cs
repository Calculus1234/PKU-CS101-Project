using UnityEngine;
using ChessDark.Core;

namespace ChessDark.Unity
{
    public class PieceData : MonoBehaviour
    {
        public int logicalX;
        public int logicalY;
        public PieceColor pieceColor;
        public PieceType visibleType;
        public PieceType trueType;

        public Position GetLogicalPosition() => new Position(logicalX, logicalY);
    }
}
