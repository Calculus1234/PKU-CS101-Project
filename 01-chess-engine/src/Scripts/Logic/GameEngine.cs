using System;
using System.Collections.Generic;
using ChessDark.Core;
using ChessDark.Logic;

namespace ChessDark.Logic
{
    public enum MoveLeakType
    {
        Disguised,
        Suspicious
    }

    public enum GameEndReason
    {
        None,
        KingCaptured,
        ThreefoldRepetition
    }

    public class MoveResult
    {
        public Move Move { get; set; }
        public Piece MovingPiece { get; set; }
        public Piece CapturedPiece { get; set; }
        public MoveLeakType LeakType { get; set; }
        public bool IsKingCapture { get; set; }
        public bool IsThreefoldRepetition { get; set; }
        public bool GameOver { get; set; }
        public PieceColor? Winner { get; set; }
    }

    public class GameEngine
    {
        public Board Board { get; private set; }
        public PieceColor CurrentTurn { get; private set; }
        public bool IsGameOver { get; private set; }
        public PieceColor? Winner { get; private set; }
        public int MoveCount { get; private set; }
        public GameEndReason EndReason { get; private set; }

        private readonly BeliefState _whiteBelief;
        private readonly BeliefState _blackBelief;
        private readonly Random _rng;
        private readonly Dictionary<ulong, int> _positionCounts = new Dictionary<ulong, int>();
        private bool _visibleOnlyHashing;

        public GameEngine(int seed = -1)
        {
            _rng = seed >= 0 ? new Random(seed) : new Random();
            Board = new Board();
            _whiteBelief = new BeliefState(PieceColor.White);
            _blackBelief = new BeliefState(PieceColor.Black);
            CurrentTurn = PieceColor.White;
            IsGameOver = false;
            MoveCount = 0;
        }

        public void InitializeGame(bool standardLayout = true)
        {
            Board = new Board();
            CurrentTurn = PieceColor.White;
            IsGameOver = false;
            Winner = null;
            EndReason = GameEndReason.None;
            MoveCount = 0;
            if (standardLayout)
            {
                PlaceStandardPieces(PieceColor.White);
                PlaceStandardPieces(PieceColor.Black);
            }
            else
            {
                PlaceShuffledPieces(PieceColor.White);
                PlaceShuffledPieces(PieceColor.Black);
            }
            ResetBeliefs();
            ResetRepetitionTracking();
        }

        public void InitializeWithFormation(OpeningFormation formation, PieceColor aiColor)
        {
            Board = new Board();
            CurrentTurn = PieceColor.White;
            IsGameOver = false;
            Winner = null;
            EndReason = GameEndReason.None;
            MoveCount = 0;

            PlaceShuffledPieces(PieceColor.White);
            PlaceFormationPieces(formation, aiColor);
            ResetBeliefs();
            ResetRepetitionTracking();
        }

        public void ResetBeliefs()
        {
            _whiteBelief.ResetFromBoard(Board);
            _blackBelief.ResetFromBoard(Board);
        }

        // 联机双方只知道自己的本态，重复局面必须只按位置+伪装态判定，否则两端哈希不一致。
        public void EnableVisibleOnlyHashing(bool enabled)
        {
            _visibleOnlyHashing = enabled;
            ResetRepetitionTracking();
        }

        // 单机开局：把 AI 的本态按阵型重排到固定的两排伪装位，位置与伪装态保持不变。
        public void ApplyFormation(OpeningFormation formation, PieceColor color)
        {
            if (formation == null) return;

            int minRow = color == PieceColor.White ? 0 : 6;
            int maxRow = color == PieceColor.White ? 1 : 7;

            var occupied = new List<Position>();
            foreach (var (pos, piece) in Board.GetAllPieces())
                if (piece.Color == color && pos.Y >= minRow && pos.Y <= maxRow)
                    occupied.Add(pos);
            foreach (var pos in occupied) Board.ClearPiece(pos);

            PlaceFormationPieces(formation, color);
            ResetBeliefs();
            ResetRepetitionTracking();
        }

        public void ApplyArrangement(PieceColor color,
            IEnumerable<(Position pos, PieceType trueType, PieceType visibleType)> pieces,
            bool publicVisibleLabels = false)
        {
            int minRow = color == PieceColor.White ? 0 : 6;
            int maxRow = color == PieceColor.White ? 1 : 7;

            var occupied = new List<(Position pos, Piece piece)>();
            foreach (var (pos, piece) in Board.GetAllPieces())
                if (piece.Color == color && pos.Y >= minRow && pos.Y <= maxRow)
                    occupied.Add((pos, piece));

            foreach (var (pos, _) in occupied) Board.ClearPiece(pos);

            foreach (var (pos, trueType, visibleType) in pieces)
            {
                if (!pos.IsValid || pos.Y < minRow || pos.Y > maxRow) continue;
                PieceType shown = visibleType;
                if (publicVisibleLabels)
                {
                    // 对手只能看到“这个格子公开的布阵标签”，不能借标签读到摆放者的真型。
                    PieceType label = PublicVisibleLabel(color, pos);
                    if (label != PieceType.None) shown = label;
                }
                Board.SetPiece(pos, new Piece(trueType, shown, color));
            }

            ResetBeliefs();
            ResetRepetitionTracking();
        }

        private static readonly PieceType[] StandardBackRank =
        {
            PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
            PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook
        };

        private static PieceType PublicVisibleLabel(PieceColor color, Position pos)
        {
            int backY = color == PieceColor.White ? 0 : 7;
            int pawnY = color == PieceColor.White ? 1 : 6;
            if (pos.Y == backY && pos.X >= 0 && pos.X < StandardBackRank.Length)
                return StandardBackRank[pos.X];
            if (pos.Y == pawnY) return PieceType.Pawn;
            return PieceType.None;
        }

        public void ResetRepetitionTracking()
        {
            _positionCounts.Clear();
            _positionCounts[HashPosition(CurrentTurn)] = 1;
        }

        private void RecordPosition()
        {
            ulong hash = HashPosition(CurrentTurn);
            int count;
            _positionCounts.TryGetValue(hash, out count);
            count++;
            _positionCounts[hash] = count;

            if (count >= 3)
            {
                // 国际象棋是判和；这里按怪谈规则让“走出第三次重复”的一方判负。
                IsGameOver = true;
                EndReason = GameEndReason.ThreefoldRepetition;
                Winner = CurrentTurn;
            }
        }

        // 规则层查询：走出这步是否会形成第三次重复（走出者判负）。AI 靠它避免自己把和局下成负局。
        public bool WouldCauseThreefold(Move move)
        {
            if (Board.GetPiece(move.From) == null) return false;

            var probe = Board.Clone();
            probe.ExecuteMove(move);
            PieceColor nextSide = CurrentTurn == PieceColor.White ? PieceColor.Black : PieceColor.White;
            int count;
            return _positionCounts.TryGetValue(HashBoard(probe, nextSide), out count) && count >= 2;
        }

        private ulong HashPosition(PieceColor side)
        {
            return HashBoard(Board, side);
        }

        private ulong HashBoard(Board board, PieceColor side)
        {
            ulong hash = 1469598103934665603UL;

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    var piece = board.GetPiece(x, y);
                    if (piece == null) continue;

                    PieceType hashedType = _visibleOnlyHashing ? piece.VisibleType : piece.TrueType;
                    ulong value = (ulong)((int)piece.Color + 1);
                    value = value * 7UL + (ulong)((int)hashedType + 1);
                    value = value * 7UL + (ulong)((int)piece.VisibleType + 1);
                    value = value * 2UL + (piece.HasMoved ? 1UL : 0UL);
                    value = value * 64UL + (ulong)(y * 8 + x);

                    hash ^= value;
                    hash *= 1099511628211UL;
                }
            }

            hash ^= (ulong)(side == PieceColor.Black ? 0x9E3779B9UL : 0UL);
            hash *= 1099511628211UL;
            return hash;
        }

        private void PlaceFormationPieces(OpeningFormation formation, PieceColor color)
        {
            int[] yRows = color == PieceColor.White ? new[] { 0, 1 } : new[] { 7, 6 };
            PieceType[] visibleBackRank = { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                                            PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };

            for (int x = 0; x < 8; x++)
            {
                Board.SetPiece(x, yRows[0], new Piece(formation.BackRank[x], visibleBackRank[x], color));
                Board.SetPiece(x, yRows[1], new Piece(formation.PawnRank[x], PieceType.Pawn, color));
            }
        }

        private void PlaceStandardPieces(PieceColor color)
        {
            PieceType[] backRank = { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                                     PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };
            int backY = color == PieceColor.White ? 0 : 7;
            int pawnY = color == PieceColor.White ? 1 : 6;

            for (int x = 0; x < 8; x++)
            {
                PieceType type = backRank[x];
                Board.SetPiece(x, backY, new Piece(type, type, color));
                Board.SetPiece(x, pawnY, new Piece(PieceType.Pawn, PieceType.Pawn, color));
            }
        }

        private void PlaceShuffledPieces(PieceColor color)
        {
            PieceType[] allPieces = { PieceType.King, PieceType.Queen,
                                      PieceType.Rook, PieceType.Rook,
                                      PieceType.Bishop, PieceType.Bishop,
                                      PieceType.Knight, PieceType.Knight,
                                      PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn,
                                      PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn };

            ShuffleArray(allPieces, _rng);

            int[] yRows = color == PieceColor.White ? new[] { 0, 1 } : new[] { 7, 6 };
            PieceType[] visibleBackRank = { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                                            PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook };

            for (int rowIdx = 0; rowIdx < 2; rowIdx++)
            {
                int y = yRows[rowIdx];
                for (int x = 0; x < 8; x++)
                {
                    int pieceIdx = rowIdx * 8 + x;
                    PieceType trueType = allPieces[pieceIdx];
                    PieceType visibleType = rowIdx == 0 ? visibleBackRank[x] : PieceType.Pawn;
                    Board.SetPiece(x, y, new Piece(trueType, visibleType, color));
                }
            }
        }

        private void ShuffleArray<T>(T[] array, Random rng)
        {
            for (int i = array.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (array[i], array[j]) = (array[j], array[i]);
            }
        }

        public MoveLeakType DetectLeak(Move move)
        {
            var piece = Board.GetPiece(move.From);
            if (piece == null) return MoveLeakType.Disguised;

            if (move.IsCastling)
            {
                if (piece.TrueType != PieceType.King || piece.HasMoved)
                    throw new InvalidOperationException($"Move {move} is not a legal castling move");
                return MoveLeakType.Disguised;
            }

            bool validForTrue = MoveGenerator.IsValidMoveForPieceType(Board, move.From, move.To, piece.TrueType, piece.Color);
            bool validForVisible = MoveGenerator.IsValidMoveForPieceType(Board, move.From, move.To, piece.VisibleType, piece.Color);

            if (!validForTrue)
                throw new InvalidOperationException($"Move {move} is illegal for TrueType {piece.TrueType}");

            return validForVisible ? MoveLeakType.Disguised : MoveLeakType.Suspicious;
        }

        public MoveResult MakeMove(Move move)
        {
            if (IsGameOver)
                throw new InvalidOperationException("Game is already over");

            var piece = Board.GetPiece(move.From);
            if (piece == null)
                throw new InvalidOperationException($"No piece at {move.From}");
            if (piece.Color != CurrentTurn)
                throw new InvalidOperationException($"It's {CurrentTurn}'s turn, not {piece.Color}'s");

            var leakType = DetectLeak(move);
            var capturedPiece = Board.GetPiece(move.To);

            var result = new MoveResult
            {
                Move = move,
                MovingPiece = piece,
                CapturedPiece = capturedPiece,
                LeakType = leakType
            };

            var moverColor = CurrentTurn;
            var opponentColor = moverColor == PieceColor.White ? PieceColor.Black : PieceColor.White;
            var moverBelief = moverColor == PieceColor.White ? _whiteBelief : _blackBelief;
            var opponentBelief = moverColor == PieceColor.White ? _blackBelief : _whiteBelief;

            opponentBelief.NotifyMove(move, piece.VisibleType, leakType, Board);

            if (capturedPiece != null)
            {
                if (capturedPiece.TrueType == PieceType.King)
                {
                    result.IsKingCapture = true;
                    result.GameOver = true;
                    result.Winner = moverColor;
                    IsGameOver = true;
                    Winner = moverColor;
                    EndReason = GameEndReason.KingCaptured;
                }

                moverBelief.NotifyCapture(move.To);
            }

            Board.ExecuteMove(move);

            if (move.PromotionType.HasValue)
                opponentBelief.NotifyPromotion(move.To, move.PromotionType);

            MoveCount++;
            CurrentTurn = opponentColor;

            if (!IsGameOver)
                RecordPosition();

            if (IsGameOver)
            {
                result.GameOver = true;
                result.Winner = Winner;
                result.IsThreefoldRepetition = EndReason == GameEndReason.ThreefoldRepetition;
            }

            return result;
        }

        // 联机模式：网络只传伪装态，无法验证对方本态。
        // moverColor 的走法由其本地负责合法性；viewerColor 只在本方棋子被吃时才能确认是否真王。
        public MoveResult ApplyOnlineMove(Move move, PieceColor moverColor, PieceColor viewerColor)
        {
            if (IsGameOver)
                throw new InvalidOperationException("Game is already over");

            var piece = Board.GetPiece(move.From);
            if (piece == null)
                throw new InvalidOperationException($"No piece at {move.From}");
            if (piece.Color != moverColor)
                throw new InvalidOperationException($"Piece at {move.From} does not belong to {moverColor}");
            if (CurrentTurn != moverColor)
                throw new InvalidOperationException($"It's {CurrentTurn}'s turn, not {moverColor}'s");

            var leakType = MoveLeakType.Disguised;
            if (moverColor == viewerColor)
                leakType = DetectLeak(move);

            var capturedPiece = Board.GetPiece(move.To);
            var result = new MoveResult
            {
                Move = move,
                MovingPiece = piece,
                CapturedPiece = capturedPiece,
                LeakType = leakType
            };

            if (capturedPiece != null && capturedPiece.Color == viewerColor &&
                capturedPiece.TrueType == PieceType.King)
            {
                result.IsKingCapture = true;
                result.GameOver = true;
                result.Winner = moverColor;
                IsGameOver = true;
                Winner = moverColor;
                EndReason = GameEndReason.KingCaptured;
            }

            Board.ExecuteMove(move);
            MoveCount++;
            CurrentTurn = moverColor == PieceColor.White ? PieceColor.Black : PieceColor.White;

            if (!IsGameOver)
                RecordPosition();

            if (IsGameOver)
            {
                result.GameOver = true;
                result.Winner = Winner;
                result.IsThreefoldRepetition = EndReason == GameEndReason.ThreefoldRepetition;
            }

            return result;
        }

        public void SkipTurn()
        {
            if (IsGameOver) return;
            CurrentTurn = CurrentTurn == PieceColor.White ? PieceColor.Black : PieceColor.White;
            MoveCount++;
        }

        public List<Move> GetLegalMoves()
        {
            return MoveGenerator.GenerateLegalMoves(Board, CurrentTurn);
        }

        public BeliefState GetBeliefState(PieceColor forColor)
        {
            return forColor == PieceColor.White ? _whiteBelief.Clone() : _blackBelief.Clone();
        }

        // 时光回溯接口：只给代码调用，Unity 层不得绑定任何按钮或快捷键。
        public GameSnapshot SaveStateSnapshot()
        {
            var counts = new Dictionary<ulong, int>(_positionCounts);
            return new GameSnapshot(Board.Clone(), CurrentTurn, MoveCount, IsGameOver, Winner, EndReason,
                _whiteBelief.Clone(), _blackBelief.Clone(), counts);
        }

        public void RollbackToSnapshot(GameSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            Board = snapshot.BoardState.Clone();
            CurrentTurn = snapshot.CurrentTurn;
            MoveCount = snapshot.TurnNumber;
            IsGameOver = snapshot.IsGameOver;
            Winner = snapshot.Winner;
            EndReason = snapshot.EndReason;
            _whiteBelief.CopyFrom(snapshot.WhiteBelief);
            _blackBelief.CopyFrom(snapshot.BlackBelief);

            _positionCounts.Clear();
            foreach (var pair in snapshot.PositionCounts)
                _positionCounts[pair.Key] = pair.Value;
        }

        public void PrintState(bool showAll = false)
        {
            Console.WriteLine($"\n=== 回合 {MoveCount + 1}, {CurrentTurn} 走棋 ===");
            Console.WriteLine("棋盘 (显示伪装态):");
            Board.PrintBoard(showTrueTypes: false);
            if (showAll)
            {
                Console.WriteLine("\n棋盘 (显示真实态):");
                var tempBoard = new Board();
                foreach (var (pos, piece) in Board.GetAllPieces())
                    tempBoard.SetPiece(pos, new Piece(piece.TrueType, piece.TrueType, piece.Color));
                tempBoard.PrintBoard(showTrueTypes: true);
            }
        }
    }
}
