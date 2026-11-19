using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ChessDark.Core;

namespace ChessDark.Logic
{
    public enum FormationStyle
    {
        Balanced,
        Conservative,
        Aggressive,
        Gambit,
        Unorthodox
    }

    public class OpeningFormation
    {
        public FormationStyle Style { get; }
        public PieceType[] BackRank { get; }
        public PieceType[] PawnRank { get; }

        // A/B/C 战术库名：布阵阶段抽定后随阵型一起锁定，行棋阶段绝不更换。
        public string Tactic { get; }

        public OpeningFormation(FormationStyle style, PieceType[] backRank, PieceType[] pawnRank,
            string tactic = null)
        {
            Style = style;
            BackRank = backRank;
            PawnRank = pawnRank;
            Tactic = tactic;
        }

        public OpeningFormation Clone()
        {
            return new OpeningFormation(Style, (PieceType[])BackRank.Clone(), (PieceType[])PawnRank.Clone(), Tactic);
        }

        public string Key
        {
            get
            {
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append((int)BackRank[i]);
                for (int i = 0; i < 8; i++) sb.Append((int)PawnRank[i]);
                return sb.ToString();
            }
        }

        public string Describe()
        {
            int frontKingX = Array.IndexOf(PawnRank, PieceType.King);
            int kingX = frontKingX >= 0 ? frontKingX : Array.IndexOf(BackRank, PieceType.King);
            string file = kingX >= 0 ? ((char)('A' + kingX)).ToString() : "?";
            int frontHeavy = PawnRank.Count(t =>
                t == PieceType.Queen || t == PieceType.Rook || t == PieceType.Bishop || t == PieceType.Knight);
            int frontRooks = PawnRank.Count(t => t == PieceType.Rook);
            int flankQueen = (PawnRank[0] == PieceType.Queen || PawnRank[7] == PieceType.Queen) ? 1 : 0;
            string kingNote = frontKingX >= 0 ? "（藏兵堆）" : string.Empty;
            string name = string.IsNullOrEmpty(Tactic) ? Style.ToString() : $"{Tactic}/{Style}";
            return $"{name} 王在{file}列{kingNote} 前排重子{frontHeavy} 前排车{frontRooks} 边线后{flankQueen}";
        }
    }

    // 玩家跨局行为画像：只统计玩家自己的走法，不包含任何本局真实棋子信息。
    public class PlayerTendency
    {
        public int Games { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Moves { get; set; }
        public int ForwardMoves { get; set; }
        public int Captures { get; set; }
        public int CenterMoves { get; set; }
        public int FlankMoves { get; set; }
        public int DefensiveMoves { get; set; }
        public int PawnRushes { get; set; }

        // 玩家被 AI 吃掉的棋子数；与 Captures 一起刻画“换子/弃子”风格。
        public int PiecesLost { get; set; }

        public float Confidence => Moves <= 0 ? 0f : Math.Min(1f, Moves / 60f);

        // 本局内前 10 步就逐步生效，不等跨局样本积累。
        public float LiveConfidence => Moves <= 0 ? 0f : Math.Min(1f, Moves / 10f);

        public float TradeWillingness
        {
            get
            {
                int trades = Captures + PiecesLost;
                return trades <= 0 ? 0.5f : Captures / (float)trades;
            }
        }

        // 只挨打不回手的比例越高，越像“弃子诱饵”型玩家。
        public float SacrificeRate
        {
            get
            {
                if (Moves <= 0) return 0f;
                return Clamp01(PiecesLost * 2f / (Moves + Captures + 1f));
            }
        }

        public float Aggression => Moves <= 0
            ? 0f
            : Clamp01((ForwardMoves + Captures * 1.5f + PawnRushes * 1.2f) / (Moves * 1.5f));

        public float Defensiveness => Moves <= 0 ? 0f : Clamp01(DefensiveMoves / (float)Moves);

        public float CenterBias => Moves <= 0 ? 0f : Clamp01(CenterMoves / (float)Moves);

        public float FlankBias => Moves <= 0 ? 0f : Clamp01(FlankMoves / (float)Moves);

        public float WinRate => Games <= 0 ? 0.5f : Wins / (float)Games;

        public bool HasHistory => Moves >= 8;

        public string Describe()
        {
            return $"对局{Games} 步数{Moves} 进攻{Aggression:P0} 防守{Defensiveness:P0} " +
                   $"中路{CenterBias:P0} 边路{FlankBias:P0} 换子{TradeWillingness:P0} " +
                   $"弃子{SacrificeRate:P0} 胜率{WinRate:P0}";
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }

    public sealed class FormationCandidate
    {
        public OpeningFormation Formation { get; }
        public float StaticScore { get; }
        public double Points;
        public int Duels;

        public FormationCandidate(OpeningFormation formation, float staticScore)
        {
            Formation = formation;
            StaticScore = staticScore;
        }

        public double WinRate => Duels <= 0 ? 0.5 : Points / Duels;

        // 自博弈只有 2~4 局，噪声大；用 300 分振幅，保证阵型质量与玩家针对权重不被两局胜负淹没。
        public double Composite => StaticScore + (WinRate - 0.5) * 300.0;
    }

    // 约束生成候选阵型 + 分批快棋自博弈。全程分帧推进，WebGL 主线程不会被卡住。
    public sealed class FormationPlanner
    {
        public const int CandidateCount = 50;
        private const int MaxGenerateAttempts = 6000;

        private static readonly PieceType[] VisibleBackRank =
        {
            PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
            PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook
        };

        private readonly PieceColor _color;
        private readonly PlayerTendency _tendency;
        private readonly Random _rng;
        private readonly int _duelGamesPerCandidate;
        private readonly int _duelPlies;
        private readonly List<FormationCandidate> _candidates = new List<FormationCandidate>();
        private readonly HashSet<string> _keys = new HashSet<string>();

        private bool _generated;
        private bool _complete;
        private OpeningFormation _chosen;
        private int _nextDuel;

        private bool _duelActive;
        private Board _duelBoard;
        private int _duelCandidateIndex;
        private PieceColor _duelSide;
        private int _duelPly;

        public bool IsComplete => _complete;
        public int CandidatesEvaluated => _candidates.Count;
        public int DuelsCompleted { get; private set; }
        public int PlannedDuels => _candidates.Count * _duelGamesPerCandidate;
        public OpeningFormation Chosen => _chosen;
        public string LastSummary { get; private set; }

        public FormationPlanner(PieceColor color, PlayerTendency tendency, int seed,
            int duelGamesPerCandidate = 2)
        {
            _color = color;
            _tendency = tendency ?? new PlayerTendency();
            _rng = new Random(seed);
            _duelGamesPerCandidate = Math.Max(1, duelGamesPerCandidate);
            _duelPlies = _duelGamesPerCandidate >= 4 ? 32 : 24;
        }

        // 布阵阶段每帧调用；budgetMs 只是软上限，至少推进一小步。
        public void Step(int budgetMs)
        {
            EnsureGenerated();
            if (_complete) return;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool didWork = false;
            int safe = Math.Max(1, budgetMs);

            while (!_complete && (!didWork || clock.ElapsedMilliseconds < safe))
            {
                if (!_duelActive && !StartNextDuel())
                {
                    Complete();
                    break;
                }

                PlayDuelPly();
                didWork = true;
            }
        }

        public OpeningFormation GetBestAvailable()
        {
            EnsureGenerated();
            if (_chosen != null) return _chosen;
            if (_complete)
            {
                PickChosen();
                return _chosen;
            }

            var ranked = RankedCandidates();
            int take = Math.Min(3, ranked.Count);
            _chosen = ranked[_rng.Next(take)].Formation;
            return _chosen;
        }

        // 对玩家当前布阵复核：只按玩家可见的伪装态推算威胁，绝不读取真实本态。
        // 若实时分析判定对手是懂棋的高手，则优先从反常规阵型池中随机取一套，破坏其开局预判。
        public OpeningFormation GetBestAvailableAgainst(Board actualBoard)
        {
            EnsureGenerated();
            if (_chosen != null) return _chosen;

            return SelectSignatureFormation(actualBoard);
        }

        // 三套战术阵型在布阵阶段按画像抽取；一旦 _chosen 落定，整局不再更换。
        private OpeningFormation SelectSignatureFormation(Board actualBoard)
        {
            var options = new List<OpeningFormation>
            {
                CreateIronBucket(),
                CreateDoubleQueenTrap(),
                CreateSecretPassage()
            };

            var safe = options.FindAll(f => IsKingSafeAgainst(actualBoard, f));
            if (safe.Count == 0) safe = options;

            float expertise = AssessExpertise(actualBoard);
            float aggression = _tendency.Aggression;
            float defense = _tendency.Defensiveness;
            float sacrifice = _tendency.SacrificeRate;
            float greed = 1f - _tendency.TradeWillingness;
            float flank = _tendency.FlankBias;

            // 画像只调权重，不做硬编码套用：没有历史时就近似均匀三选一。
            double weightA = 0.6 + aggression * 1.6 + defense * 0.3;
            double weightB = 0.6 + sacrifice * 1.5 + greed * 1.0;
            double weightC = 0.6 + defense * 1.2 + flank * 0.8 + (expertise >= 0.55f ? 0.7 : 0.0);

            double total = 0.0;
            foreach (var formation in safe)
            {
                if (formation.Tactic != null && formation.Tactic.StartsWith("A")) total += weightA;
                else if (formation.Tactic != null && formation.Tactic.StartsWith("B")) total += weightB;
                else total += weightC;
            }

            double roll = _rng.NextDouble() * total;
            double acc = 0.0;
            _chosen = safe[safe.Count - 1];
            foreach (var formation in safe)
            {
                double weight = formation.Tactic != null && formation.Tactic.StartsWith("A") ? weightA
                    : formation.Tactic != null && formation.Tactic.StartsWith("B") ? weightB
                    : weightC;
                acc += weight;
                if (roll <= acc)
                {
                    _chosen = formation;
                    break;
                }
            }

            LastSummary = $"布阵锁定[{_chosen.Describe()}] 安全{safe.Count}/3 高手评分{expertise:F2} " +
                          $"权重 A{weightA:F2}/B{weightB:F2}/C{weightC:F2} 玩家画像[{_tendency.Describe()}]";
            return _chosen;
        }

        public static OpeningFormation CreateIronBucket()
        {
            var back = new[]
            {
                PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook
            };
            var front = new PieceType[8];
            for (int i = 0; i < 8; i++) front[i] = PieceType.Pawn;
            return new OpeningFormation(FormationStyle.Conservative, back, front, "A 铁桶反击");
        }

        // 前排两个“兵”其实是真后与真车；后排 Q 位也只是普通兵。
        public static OpeningFormation CreateDoubleQueenTrap()
        {
            var back = new[]
            {
                PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Pawn,
                PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Pawn
            };
            var front = new[]
            {
                PieceType.Pawn, PieceType.Pawn, PieceType.Queen, PieceType.Pawn,
                PieceType.Pawn, PieceType.Rook, PieceType.Pawn, PieceType.Pawn
            };
            return new OpeningFormation(FormationStyle.Gambit, back, front, "B 双后陷阱");
        }

        // 真王藏在中路后将位（伪装成后），a 线“车”是真后，前排 8 个全是真兵。
        // 王不放角落：d8 被自己的马/车/兵围住时仍有左右两条逃路，
        // 而 a8 只有 a7/b8/b7 三个邻格（两个是自己子），敌方象一旦贴脸就无路可走（G25 7 步被吃）。
        public static OpeningFormation CreateSecretPassage()
        {
            var back = new[]
            {
                PieceType.Queen, PieceType.Bishop, PieceType.Knight, PieceType.King,
                PieceType.Rook, PieceType.Bishop, PieceType.Knight, PieceType.Rook
            };
            var front = new PieceType[8];
            for (int i = 0; i < 8; i++) front[i] = PieceType.Pawn;
            return new OpeningFormation(FormationStyle.Unorthodox, back, front, "C 暗度陈仓");
        }

        private static bool IsUnorthodox(OpeningFormation formation)
        {
            return formation != null && formation.Style == FormationStyle.Unorthodox;
        }

        // 只依据玩家可见的布阵痕迹与跨局画像判断“懂棋”：保持标准伪装、少白送、有中路边路意识、有胜绩。
        private float AssessExpertise(Board actualBoard)
        {
            float score = 0f;
            if (_tendency.Games >= 1 && _tendency.WinRate >= 0.5f) score += 0.30f;
            if (_tendency.Moves >= 20 && _tendency.SacrificeRate < 0.35f) score += 0.15f;
            if (_tendency.CenterBias >= 0.18f) score += 0.10f;
            if (_tendency.TradeWillingness >= 0.45f) score += 0.08f;

            if (actualBoard != null)
            {
                int frontY = _color == PieceColor.White ? 6 : 1;
                int backY = _color == PieceColor.White ? 7 : 0;
                int frontPawns = 0;
                int standardBack = 0;
                for (int x = 0; x < 8; x++)
                {
                    var front = actualBoard.GetPiece(x, frontY);
                    if (front != null && front.Color != _color && front.VisibleType == PieceType.Pawn)
                        frontPawns++;

                    var back = actualBoard.GetPiece(x, backY);
                    if (back != null && back.Color != _color && back.VisibleType == VisibleBackRank[x])
                        standardBack++;
                }

                if (frontPawns == 8) score += 0.10f;
                if (standardBack == 8) score += 0.12f;
                else if (standardBack <= 4) score -= 0.08f;
            }

            return score;
        }

        public bool IsKingSafeAgainst(Board actualBoard, OpeningFormation formation)
        {
            if (actualBoard == null || formation == null) return true;

            var board = new Board();
            foreach (var (pos, piece) in actualBoard.GetAllPieces())
            {
                if (piece.Color == _color) continue;
                board.SetPiece(pos, new Piece(piece.VisibleType, piece.VisibleType, piece.Color));
            }
            PlaceFormation(board, formation, _color);

            var kingPos = board.FindKing(_color);
            if (!kingPos.HasValue) return false;

            foreach (var (from, piece) in board.GetPiecesOfColor(Opponent(_color)))
            {
                if (MoveGenerator.CanPieceAttackSquare(board, from, piece.TrueType, piece.Color, kingPos.Value))
                    return false;
            }
            return true;
        }

        public static OpeningFormation CreateFallback()
        {
            var back = new[]
            {
                PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook
            };
            var front = new PieceType[8];
            for (int i = 0; i < 8; i++) front[i] = PieceType.Pawn;
            return new OpeningFormation(FormationStyle.Balanced, back, front);
        }

        private void EnsureGenerated()
        {
            if (_generated) return;
            _generated = true;

            AddUnorthodoxCandidates();

            int attempts = 0;
            while (_candidates.Count < CandidateCount && attempts++ < MaxGenerateAttempts)
            {
                var candidate = TryCreateCandidate();
                if (candidate == null) continue;
                if (_keys.Add(candidate.Formation.Key))
                    _candidates.Add(candidate);
            }

            if (_candidates.Count == 0)
            {
                var fallback = CreateFallback();
                _candidates.Add(new FormationCandidate(fallback, EvaluateStatic(fallback)));
                _keys.Add(fallback.Key);
            }
        }

        // 反常规阵型池：前排双车、王藏兵堆、后顶边线等。所有棋子仍按 1王1后2车2象2马8兵配置。
        private void AddUnorthodoxCandidates()
        {
            foreach (var formation in CreateUnorthodoxPool())
            {
                if (!_keys.Add(formation.Key)) continue;
                _candidates.Add(new FormationCandidate(formation, EvaluateStatic(formation)));
            }
        }

        private static IEnumerable<OpeningFormation> CreateUnorthodoxPool()
        {
            var pool = new List<OpeningFormation>
            {
                // 前排双车，王留守后排中路。
                MakeUnorthodox(
                    new[] { PieceType.Knight, PieceType.Bishop, PieceType.Queen, PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Pawn, PieceType.Pawn },
                    new[] { PieceType.Rook, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Rook }),

                // 王藏在兵堆里：前排 D 列其实是真王，后排 D 列只是普通兵。
                MakeUnorthodox(
                    new[] { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen, PieceType.Pawn, PieceType.Bishop, PieceType.Knight, PieceType.Rook },
                    new[] { PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.King, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn }),

                // 后直接顶在左翼边线。
                MakeUnorthodox(
                    new[] { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.King, PieceType.Pawn, PieceType.Bishop, PieceType.Knight, PieceType.Rook },
                    new[] { PieceType.Queen, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn }),

                // 后直接顶在右翼边线。
                MakeUnorthodox(
                    new[] { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Pawn, PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook },
                    new[] { PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Queen }),

                // 前排双车 + 王藏兵堆，彻底打乱常规开局预判。
                MakeUnorthodox(
                    new[] { PieceType.Knight, PieceType.Bishop, PieceType.Queen, PieceType.Pawn, PieceType.Bishop, PieceType.Knight, PieceType.Pawn, PieceType.Pawn },
                    new[] { PieceType.Rook, PieceType.Pawn, PieceType.Pawn, PieceType.King, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Rook }),

                // 前排双车（左） + 右翼边后。
                MakeUnorthodox(
                    new[] { PieceType.Knight, PieceType.Bishop, PieceType.King, PieceType.Rook, PieceType.Bishop, PieceType.Knight, PieceType.Pawn, PieceType.Pawn },
                    new[] { PieceType.Rook, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Queen }),

                // 王藏兵堆 + 左翼边后。
                MakeUnorthodox(
                    new[] { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Pawn, PieceType.Bishop, PieceType.Knight, PieceType.Pawn, PieceType.Rook },
                    new[] { PieceType.Queen, PieceType.Pawn, PieceType.Pawn, PieceType.King, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn }),

                // 王藏兵堆 + 右翼边后。
                MakeUnorthodox(
                    new[] { PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Pawn, PieceType.Bishop, PieceType.Knight, PieceType.Pawn, PieceType.Rook },
                    new[] { PieceType.Pawn, PieceType.Pawn, PieceType.King, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Queen }),

                // 前排双车 + 王藏兵堆 + 左翼边后（极端反常）。
                MakeUnorthodox(
                    new[] { PieceType.Knight, PieceType.Bishop, PieceType.Pawn, PieceType.Bishop, PieceType.Knight, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn },
                    new[] { PieceType.Queen, PieceType.Rook, PieceType.Pawn, PieceType.King, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Rook }),

                // 前排双车 + 王藏兵堆 + 右翼边后（镜像）。
                MakeUnorthodox(
                    new[] { PieceType.Pawn, PieceType.Knight, PieceType.Bishop, PieceType.Pawn, PieceType.Bishop, PieceType.Knight, PieceType.Pawn, PieceType.Pawn },
                    new[] { PieceType.Rook, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.King, PieceType.Pawn, PieceType.Queen, PieceType.Rook })
            };

            return pool;
        }

        private static OpeningFormation MakeUnorthodox(PieceType[] back, PieceType[] front)
        {
            return new OpeningFormation(FormationStyle.Unorthodox, back, front);
        }

        private FormationCandidate TryCreateCandidate()
        {
            var pool = new List<PieceType>(16)
            {
                PieceType.King, PieceType.Queen,
                PieceType.Rook, PieceType.Rook,
                PieceType.Bishop, PieceType.Bishop,
                PieceType.Knight, PieceType.Knight,
                PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn,
                PieceType.Pawn, PieceType.Pawn, PieceType.Pawn, PieceType.Pawn
            };
            Shuffle(pool);

            int kingIndex = pool.IndexOf(PieceType.King);
            if (kingIndex >= 8)
            {
                int swap = _rng.Next(8);
                (pool[kingIndex], pool[swap]) = (pool[swap], pool[kingIndex]);
            }

            var back = new PieceType[8];
            var front = new PieceType[8];
            for (int i = 0; i < 8; i++)
            {
                back[i] = pool[i];
                front[i] = pool[8 + i];
            }

            // 兵不能全挤在无法展开的位置：前排放置至少 4 个真兵。
            int frontPawns = front.Count(t => t == PieceType.Pawn);
            if (frontPawns < 4) return null;

            int kingX = Array.IndexOf(back, PieceType.King);
            CountKingGuards(back, front, kingX, out int guards, out int frontGuards);
            if (guards < 2 || frontGuards < 1) return null;

            // 王的正前方必须是兵：重子先动会直接打开王的纵线。
            if (front[kingX] != PieceType.Pawn) return null;

            int frontHeavy = front.Count(IsHeavy);
            FormationStyle style = frontHeavy >= 3
                ? FormationStyle.Gambit
                : (kingX >= 2 && kingX <= 5 && guards >= 3
                    ? FormationStyle.Conservative
                    : frontHeavy >= 1 ? FormationStyle.Aggressive : FormationStyle.Balanced);

            var formation = new OpeningFormation(style, back, front);
            return new FormationCandidate(formation, EvaluateStatic(formation));
        }

        private float EvaluateStatic(OpeningFormation formation)
        {
            var back = formation.BackRank;
            var front = formation.PawnRank;
            int kingBackX = Array.IndexOf(back, PieceType.King);
            int kingFrontX = Array.IndexOf(front, PieceType.King);
            int kingX = kingBackX >= 0 ? kingBackX : kingFrontX;
            if (kingX < 0) return -10000f;
            bool kingInFront = kingBackX < 0;

            float confidence = _tendency.Confidence;
            float aggression = _tendency.Aggression * confidence;
            float defense = _tendency.Defensiveness * confidence;
            float flank = _tendency.FlankBias * confidence;

            float kingWeight = 1f + 0.35f * aggression;
            float trapWeight = 16f + 34f * aggression;       // 玩家爱冲锋：前排埋雷
            float pressureWeight = 14f + 26f * defense;      // 玩家爱龟缩：大子前置施压
            float centerWeight = 18f + 20f * Math.Max(defense, _tendency.CenterBias * confidence);

            CountKingGuards(back, front, kingX, out int guards, out int frontGuards, kingInFront);
            float score = guards * 42f * kingWeight + frontGuards * 18f * kingWeight;
            score += kingX >= 2 && kingX <= 5 ? 18f : -22f;
            if (kingBackX >= 0 && front[kingX] == PieceType.Queen) score -= 18f;
            if (kingInFront) score += 30f; // 王藏兵堆：牺牲显性王位换取不可预测性。

            int frontPawns = front.Count(t => t == PieceType.Pawn);
            score += frontPawns * 9f;
            if (frontPawns < 5) score -= 35f;
            if (frontPawns >= 6) score += 18f;

            for (int x = 0; x < 8; x++)
            {
                score += CenterValue(back[x], x, false, 1f);
                score += CenterValue(front[x], x, true, 1f + 0.15f * defense);
            }

            int frontHeavy = front.Count(IsHeavy);
            score += frontHeavy * trapWeight;
            for (int x = 0; x < 8; x++)
            {
                if (!IsHeavy(front[x])) continue;
                if (x == 3 || x == 4) score += pressureWeight + centerWeight;
                else if (x == 2 || x == 5) score += pressureWeight * 0.5f;
            }

            int edgePawns = (front[0] == PieceType.Pawn ? 1 : 0) + (front[7] == PieceType.Pawn ? 1 : 0);
            score += edgePawns * (6f + 18f * flank);

            return score;
        }

        private static float CenterValue(PieceType type, int x, bool frontRow, float weight)
        {
            float file = 24f - Math.Abs(x - 3.5f) * 6.5f;
            switch (type)
            {
                case PieceType.Queen:
                    return (26f + file) * (frontRow ? 1.1f : 1f) * weight;
                case PieceType.Rook:
                    return (18f + file) * (frontRow ? 1.05f : 1f) * weight;
                case PieceType.Bishop:
                case PieceType.Knight:
                    return (file * 0.55f + (x >= 2 && x <= 5 ? 8f : 0f)) * weight;
                default:
                    return 0f;
            }
        }

        private static void CountKingGuards(PieceType[] back, PieceType[] front, int kingX,
            out int guards, out int frontGuards, bool kingInFront = false)
        {
            guards = 0;
            frontGuards = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = kingX + dx;
                if (x < 0 || x > 7) continue;

                if (IsGuard(front[x]))
                {
                    guards++;
                    frontGuards++;
                }

                if (x != kingX && IsGuard(back[x]))
                    guards++;
            }

            // 王在前排时，正后方同列的棋子也算贴身护卫。
            if (kingInFront && IsGuard(back[kingX]))
                guards++;
        }

        private static bool IsGuard(PieceType type)
        {
            return type == PieceType.Rook || type == PieceType.Knight ||
                   type == PieceType.Bishop || type == PieceType.Pawn;
        }

        private static bool IsHeavy(PieceType type)
        {
            return type == PieceType.Queen || type == PieceType.Rook ||
                   type == PieceType.Bishop || type == PieceType.Knight;
        }

        private void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private bool StartNextDuel()
        {
            int total = _candidates.Count * _duelGamesPerCandidate;
            if (_nextDuel >= total) return false;

            int candidateIndex = _nextDuel / _duelGamesPerCandidate;
            int gameIndex = _nextDuel % _duelGamesPerCandidate;
            int otherIndex = (candidateIndex + 7 + gameIndex * 11) % _candidates.Count;
            if (_candidates.Count > 1 && otherIndex == candidateIndex)
                otherIndex = (otherIndex + 1) % _candidates.Count;

            _duelCandidateIndex = candidateIndex;
            _duelBoard = BuildDuelBoard(
                _candidates[candidateIndex].Formation,
                _candidates[otherIndex].Formation);
            _duelSide = PieceColor.White;
            _duelPly = 0;
            _duelActive = true;
            return true;
        }

        private Board BuildDuelBoard(OpeningFormation mine, OpeningFormation theirs)
        {
            var board = new Board();
            PlaceFormation(board, mine, _color);
            PlaceFormation(board, theirs, Opponent(_color));
            return board;
        }

        private static void PlaceFormation(Board board, OpeningFormation formation, PieceColor color)
        {
            int backY = color == PieceColor.White ? 0 : 7;
            int frontY = color == PieceColor.White ? 1 : 6;
            for (int x = 0; x < 8; x++)
            {
                board.SetPiece(x, backY, new Piece(formation.BackRank[x], VisibleBackRank[x], color));
                board.SetPiece(x, frontY, new Piece(formation.PawnRank[x], PieceType.Pawn, color));
            }
        }

        private void PlayDuelPly()
        {
            if (_duelBoard == null)
            {
                _duelActive = false;
                return;
            }

            if (_duelPly >= _duelPlies)
            {
                FinishDuel(ScoreDuel());
                return;
            }

            var moves = MoveGenerator.GeneratePseudoLegalMoves(_duelBoard, _duelSide);
            if (moves.Count == 0)
            {
                _duelSide = Opponent(_duelSide);
                _duelPly++;
                if (_duelPly >= _duelPlies) FinishDuel(ScoreDuel());
                return;
            }

            Move chosen = moves[0];
            int bestScore = int.MinValue;
            foreach (var move in moves)
            {
                var undo = _duelBoard.ExecuteMoveWithUndo(move);
                bool kingCapture = undo.Captured != null && undo.Captured.TrueType == PieceType.King;
                int score = kingCapture
                    ? 1000000
                    : EvaluateDuel(_duelBoard, _duelSide) + _rng.Next(-12, 13);
                _duelBoard.UndoMove(move, undo);

                if (score > bestScore)
                {
                    bestScore = score;
                    chosen = move;
                }
            }

            var result = _duelBoard.ExecuteMoveWithUndo(chosen);
            bool won = result.Captured != null && result.Captured.TrueType == PieceType.King;
            _duelPly++;

            if (won)
            {
                FinishDuel(_duelSide == _color ? 1.0 : 0.0);
                return;
            }

            _duelSide = Opponent(_duelSide);
            if (_duelPly >= _duelPlies)
                FinishDuel(ScoreDuel());
        }

        private int EvaluateDuel(Board board, PieceColor side)
        {
            int score = 0;
            Position? ownKing = null;
            Position? enemyKing = null;

            foreach (var (pos, piece) in board.GetAllPieces())
            {
                bool own = piece.Color == side;
                if (piece.TrueType == PieceType.King)
                {
                    if (own) ownKing = pos;
                    else enemyKing = pos;
                    continue;
                }

                int value = piece.TrueType.GetValue();
                int fileCenter = (int)(Math.Abs(pos.X - 3.5f) * -8f);
                score += own ? value + fileCenter : -(value + fileCenter);
            }

            if (!ownKing.HasValue) return -100000;
            if (!enemyKing.HasValue) return 100000;

            score += ShieldCount(board, ownKing.Value, side) * 8;
            score -= ShieldCount(board, enemyKing.Value, Opponent(side)) * 8;
            return score;
        }

        private static int ShieldCount(Board board, Position king, PieceColor color)
        {
            int count = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var pos = new Position(king.X + dx, king.Y + dy);
                    if (!pos.IsValid) continue;
                    var piece = board.GetPiece(pos);
                    if (piece != null && piece.Color == color && piece.TrueType != PieceType.King)
                        count++;
                }
            }
            return count;
        }

        private double ScoreDuel()
        {
            if (_duelBoard == null) return 0.5;

            int diff = 0;
            foreach (var (_, piece) in _duelBoard.GetAllPieces())
            {
                if (piece.TrueType == PieceType.King) continue;
                diff += piece.Color == _color ? piece.TrueType.GetValue() : -piece.TrueType.GetValue();
            }

            if (diff >= 100) return 1.0;
            if (diff <= -100) return 0.0;
            return 0.5;
        }

        private void FinishDuel(double points)
        {
            var candidate = _candidates[_duelCandidateIndex];
            candidate.Points += points;
            candidate.Duels++;
            DuelsCompleted++;
            _nextDuel++;
            _duelActive = false;
            _duelBoard = null;
        }

        private void Complete()
        {
            _complete = true;
            PickChosen();
        }

        private void PickChosen()
        {
            var ranked = RankedCandidates();
            int take = Math.Min(3, ranked.Count);
            int index = 0;

            // 玩家近期赢得多时更偏向自博弈第一名，否则前三随机，保持不可预测性。
            if (_tendency.Games >= 2 && _tendency.WinRate >= 0.6f && _rng.NextDouble() < 0.7)
                index = 0;
            else
                index = _rng.Next(take);

            _chosen = ranked[index].Formation;
            var selected = ranked[index];
            LastSummary = $"候选{_candidates.Count} 自博弈{DuelsCompleted}/{PlannedDuels} " +
                          $"选中[{_chosen.Describe()} 静态{selected.StaticScore:F0} 自博弈胜率{selected.WinRate:P0}] " +
                          $"玩家画像[{_tendency.Describe()}]";
        }

        private List<FormationCandidate> RankedCandidates()
        {
            return _candidates
                .OrderByDescending(c => c.Composite)
                .ThenByDescending(c => c.StaticScore)
                .ToList();
        }

        private static PieceColor Opponent(PieceColor color)
        {
            return color == PieceColor.White ? PieceColor.Black : PieceColor.White;
        }
    }
}
