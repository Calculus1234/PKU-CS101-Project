using System.Collections.Generic;
using ChessDark.Core;

namespace ChessDark.Multiplayer
{
    // 单机构建也必须能编译这个接口；它不引用任何 Photon 类型。
    public interface IOnlineMatchController
    {
        void SubmitArrangement(IEnumerable<(Position pos, PieceType trueType, PieceType visibleType)> pieces);
        void SendMove(Move move);
        void SendPass();
        void SendGameOver(PieceColor winner);
        void Shutdown();
    }
}
