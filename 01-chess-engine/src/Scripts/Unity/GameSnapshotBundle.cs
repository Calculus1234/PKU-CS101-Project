using ChessDark.Logic;

namespace ChessDark.Unity
{
    // 剧情回溯用完整快照：引擎侧棋盘/双方信念/回合数 + AI 私有信念。
    // 仅供代码调用，Unity 界面不得绑定任何“悔棋”按钮或快捷键。
    public sealed class GameSnapshotBundle
    {
        public GameSnapshot Engine { get; internal set; }
        public BeliefState AiBelief { get; internal set; }
        public int TurnNumber => Engine != null ? Engine.TurnNumber : 0;

        internal GameSnapshotBundle()
        {
        }
    }
}
