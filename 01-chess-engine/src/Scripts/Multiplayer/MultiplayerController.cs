#if !DISABLE_MULTIPLAYER
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using ChessDark.Core;
using ChessDark.Unity;

namespace ChessDark.Multiplayer
{
    public class MultiplayerController : MonoBehaviourPunCallbacks, IOnlineMatchController
    {
        const int SyncViewId = 1;
        const string PropWhiteActor = "whiteActor";
        const string PropReady = "ready";
        const string PropClientId = "clientId";
        const string ClientIdPrefKey = "chess.multiplayer.client_id";

        // 固定同一区域，避免 PC/WebGL 自动选到不同集群后互相看不到房间。
        const string FixedRegion = "asia";

        private GameManager _gameManager;
        private OnlineLobbyUI _lobby;
        private PhotonView _view;
        private string _joinCode;
        private bool _createMode;
        private bool _ready;
        private bool _colorResolved;
        private bool _shuttingDown;
        private bool _localArrangementReady;
        private bool _remoteArrangementReady;
        private int _createAttempts;
        private PieceColor _localColor = PieceColor.White;
        private List<(Position pos, PieceType visibleType)> _remoteArrangement;
        private int _pendingPromotion;
        private bool _pendingCastling;
        private bool _randomJoinAttempted;
        private short _joinFailureCode;
        private string _joinFailureMessage;

        public void Initialize(GameManager gameManager, string joinCode)
        {
            _gameManager = gameManager;
            _joinCode = joinCode;
            _createMode = string.IsNullOrEmpty(joinCode);
            _randomJoinAttempted = false;

            _lobby = new OnlineLobbyUI(gameManager.uiCanvas, OnReadyClicked, OnLeaveClicked);
            _lobby.ShowConnecting();
            EnsureView();

            var appSettings = PhotonNetwork.PhotonServerSettings.AppSettings;
            appSettings.FixedRegion = FixedRegion;
            Debug.Log("[Photon] 固定区域: " + FixedRegion + " | AppID: " + appSettings.AppIdRealtime);

            if (PhotonNetwork.IsConnected)
                OnConnectedToMaster();
            else
                PhotonNetwork.ConnectUsingSettings();
        }

        private void EnsureView()
        {
            if (_view != null) return;
            _view = GetComponent<PhotonView>();
            if (_view == null) _view = gameObject.AddComponent<PhotonView>();
            if (_view.ViewID == 0) _view.ViewID = SyncViewId;
        }

        public override void OnConnectedToMaster()
        {
            if (_shuttingDown || _lobby == null) return;
            Debug.Log("[Photon] 连接成功！AppID: " + PhotonNetwork.PhotonServerSettings.AppSettings.AppIdRealtime
                + " | 服务器区域: " + PhotonNetwork.CloudRegion);
            _lobby.SetStatus("已连接服务器，正在进入房间…");

            if (_createMode)
                CreateRoomWithNewCode();
            else
                PhotonNetwork.JoinRoom(_joinCode);
        }

        private void CreateRoomWithNewCode()
        {
            int code = UnityEngine.Random.Range(100000, 1000000);
            _joinCode = code.ToString();
            var options = new RoomOptions
            {
                MaxPlayers = 2,
                IsVisible = true,
                IsOpen = true,
                PublishUserId = true,
                PlayerTtl = 0,
                EmptyRoomTtl = 0,
                CleanupCacheOnLeave = true
            };
            PhotonNetwork.CreateRoom(_joinCode, options, TypedLobby.Default);
        }

        public override void OnCreateRoomFailed(short returnCode, string message)
        {
            if (_shuttingDown || _lobby == null) return;
            _createAttempts++;
            if (_createAttempts < 5)
            {
                _lobby.SetStatus("房间号冲突，正在重新创建…");
                CreateRoomWithNewCode();
            }
            else
            {
                _lobby.ShowError("创建房间失败：" + message);
            }
        }

        public override void OnCreatedRoom()
        {
            string roomName = PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.Name : _joinCode;
            Debug.Log("[Photon] 创建房间: " + roomName + " 成功。");
        }

        public override void OnJoinRoomFailed(short returnCode, string message)
        {
            if (_shuttingDown || _lobby == null) return;

            Debug.LogWarning("[Photon] 加入房间失败，错误码: " + returnCode + ", 信息: " + message);

            if (returnCode == (int)ErrorCode.GameDoesNotExist && !_randomJoinAttempted)
            {
                _randomJoinAttempted = true;
                _joinFailureCode = returnCode;
                _joinFailureMessage = message;
                _lobby.SetStatus("未找到该房间，正在尝试随机匹配…");
                PhotonNetwork.JoinRandomRoom();
                return;
            }

            ShowJoinFailure(returnCode, message);
        }

        public override void OnJoinRandomFailed(short returnCode, string message)
        {
            if (_shuttingDown || _lobby == null) return;
            Debug.LogWarning("[Photon] 随机匹配失败，错误码: " + returnCode + ", 信息: " + message);
            ShowJoinFailure(_joinFailureCode, _joinFailureMessage);
        }

        private void ShowJoinFailure(short returnCode, string message)
        {
            int code = returnCode;
            if (code == (int)ErrorCode.GameFull)
                _lobby.ShowError("该房间人数已满");
            else if (code == (int)ErrorCode.GameDoesNotExist)
                _lobby.ShowError("房间号不存在");
            else if (code == (int)ErrorCode.GameClosed)
                _lobby.ShowError("该房间已关闭");
            else
                _lobby.ShowError("加入失败：" + message);
        }

        public override void OnJoinedRoom()
        {
            if (_shuttingDown || _lobby == null) return;
            EnsureView();
            ApplyClientIdentity();
            if (PhotonNetwork.CurrentRoom != null) _joinCode = PhotonNetwork.CurrentRoom.Name;
            _randomJoinAttempted = false;
            Debug.Log("[Photon] 加入房间成功: " + _joinCode + " | 区域: " + PhotonNetwork.CloudRegion);
            _lobby.ShowRoom(_joinCode);
            RefreshLobby();
            RemoveDuplicateConnections();
        }

        public override void OnPlayerEnteredRoom(Player newPlayer)
        {
            if (_shuttingDown || _lobby == null) return;
            RefreshLobby();
            RemoveDuplicateConnections();
        }

        public override void OnPlayerLeftRoom(Player otherPlayer)
        {
            if (_shuttingDown || _lobby == null) return;

            if (PhotonNetwork.PlayerList.Length < 2)
            {
                if (_gameManager != null && _gameManager.IsOnlineMatchActive)
                    _gameManager.OnOnlineOpponentLeft();
                else
                    _lobby.SetStatus("对手已离开，等待新的对手加入…");
            }
        }

        public override void OnPlayerPropertiesUpdate(Player targetPlayer, Hashtable changedProps)
        {
            if (_shuttingDown || _lobby == null) return;
            RefreshLobby();
            TryAssignColors();
            RemoveDuplicateConnections();
        }

        public override void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
        {
            if (_shuttingDown || _lobby == null) return;
            object value;
            if (propertiesThatChanged != null && propertiesThatChanged.TryGetValue(PropWhiteActor, out value))
                ResolveColor(Convert.ToInt32(value));
        }

        public override void OnDisconnected(DisconnectCause cause)
        {
            if (_shuttingDown || _lobby == null) return;
            _lobby.ShowError("与服务器断开连接（" + cause + "）");
        }

        private static bool GetReady(Player player)
        {
            if (player == null || player.CustomProperties == null) return false;
            object value;
            if (!player.CustomProperties.TryGetValue(PropReady, out value)) return false;
            return value is bool && (bool)value;
        }

        private bool BothReady()
        {
            var players = PhotonNetwork.PlayerList;
            if (players.Length != 2) return false;
            for (int i = 0; i < players.Length; i++)
                if (!GetReady(players[i])) return false;
            return true;
        }

        private void RefreshLobby()
        {
            if (_lobby == null || !PhotonNetwork.InRoom) return;

            Player opponent = null;
            var players = PhotonNetwork.PlayerList;
            for (int i = 0; i < players.Length; i++)
            {
                if (!players[i].IsLocal) opponent = players[i];
            }

            _ready = GetReady(PhotonNetwork.LocalPlayer);
            _lobby.SetPlayers(_ready, opponent != null, opponent != null && GetReady(opponent));
        }

        private void OnReadyClicked()
        {
            if (!PhotonNetwork.InRoom) return;
            _ready = !_ready;
            var props = new Hashtable { { PropReady, _ready } };
            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
            RefreshLobby();
            TryAssignColors();
        }

        private void OnLeaveClicked()
        {
            if (PhotonNetwork.InRoom) PhotonNetwork.LeaveRoom();
            if (_gameManager != null) _gameManager.ExitOnlineToMenu();
        }

        private static string LocalClientId
        {
            get
            {
                string id = PlayerPrefs.GetString(ClientIdPrefKey, string.Empty);
                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString(ClientIdPrefKey, id);
                    PlayerPrefs.Save();
                }
                return id;
            }
        }

        private void ApplyClientIdentity()
        {
            var props = new Hashtable { { PropClientId, LocalClientId } };
            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        }

        private static string GetClientId(Player player)
        {
            if (player == null || player.CustomProperties == null) return null;
            object value;
            if (!player.CustomProperties.TryGetValue(PropClientId, out value)) return null;
            return value as string;
        }

        private void RemoveDuplicateConnections()
        {
            if (_shuttingDown || !PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient) return;

            var players = PhotonNetwork.PlayerList;
            for (int i = 0; i < players.Length; i++)
            {
                string id = GetClientId(players[i]);
                if (string.IsNullOrEmpty(id)) continue;

                for (int j = i + 1; j < players.Length; j++)
                {
                    if (id != GetClientId(players[j])) continue;

                    Player oldPlayer = players[i].ActorNumber < players[j].ActorNumber ? players[i] : players[j];
                    Player newPlayer = ReferenceEquals(oldPlayer, players[i]) ? players[j] : players[i];

                    if (!oldPlayer.IsLocal)
                    {
                        PhotonNetwork.CloseConnection(oldPlayer);
                        if (_lobby != null) _lobby.SetStatus("检测到同一账号的旧连接，已清理");
                    }
                    else if (!newPlayer.IsLocal)
                    {
                        PhotonNetwork.CloseConnection(newPlayer);
                        if (_lobby != null) _lobby.SetStatus("检测到重复连接，已保留当前房间");
                    }
                    return;
                }
            }
        }

        private void TryAssignColors()
        {
            if (_shuttingDown || _colorResolved || !PhotonNetwork.InRoom) return;
            if (PhotonNetwork.CurrentRoom == null) return;

            object existing;
            if (PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(PropWhiteActor, out existing))
            {
                ResolveColor(Convert.ToInt32(existing));
                return;
            }

            if (!PhotonNetwork.IsMasterClient || !BothReady()) return;

            var players = PhotonNetwork.PlayerList;
            if (players.Length != 2) return;

            int white = UnityEngine.Random.value < 0.5f
                ? players[0].ActorNumber
                : players[1].ActorNumber;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { PropWhiteActor, white } });
            ResolveColor(white);
        }

        private void ResolveColor(int whiteActor)
        {
            if (_shuttingDown || _colorResolved) return;
            _colorResolved = true;
            _localColor = PhotonNetwork.LocalPlayer.ActorNumber == whiteActor ? PieceColor.White : PieceColor.Black;
            StartCoroutine(CoinFlipRoutine());
        }

        private IEnumerator CoinFlipRoutine()
        {
            if (_lobby == null) yield break;
            _lobby.ShowCoin();

            float elapsed = 0f;
            bool face = false;
            while (elapsed < 1.4f)
            {
                face = !face;
                _lobby.SetCoinText(face ? "正" : "反");
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }

            _lobby.SetCoinText(_localColor == PieceColor.White ? "你执白 · 先行" : "你执黑 · 后行");
            yield return new WaitForSeconds(0.6f);

            _lobby.Hide();
            if (_gameManager != null)
                _gameManager.BeginOnlineGame(_localColor, this);
        }

        public void SubmitArrangement(IEnumerable<(Position pos, PieceType trueType, PieceType visibleType)> pieces)
        {
            if (_view == null || _shuttingDown) return;

            var packedPositions = new List<int>(16);
            var visibleTypes = new List<byte>(16);
            foreach (var piece in pieces)
            {
                packedPositions.Add(piece.pos.Y * 8 + piece.pos.X);
                visibleTypes.Add((byte)piece.visibleType);
            }

            _localArrangementReady = true;
            _view.RPC("OnArrangementReceived", RpcTarget.Others,
                packedPositions.ToArray(), visibleTypes.ToArray());
            TryForwardArrangements();
        }

        [PunRPC]
        public void OnArrangementReceived(int[] packedPositions, byte[] visibleTypes)
        {
            if (_shuttingDown) return;
            var list = new List<(Position pos, PieceType visibleType)>();
            if (packedPositions != null && visibleTypes != null)
            {
                int count = Mathf.Min(packedPositions.Length, visibleTypes.Length);
                for (int i = 0; i < count; i++)
                {
                    int packed = packedPositions[i];
                    list.Add((new Position(packed % 8, packed / 8), (PieceType)visibleTypes[i]));
                }
            }

            _remoteArrangement = list;
            _remoteArrangementReady = true;
            TryForwardArrangements();
        }

        private void TryForwardArrangements()
        {
            if (!_localArrangementReady || !_remoteArrangementReady) return;
            if (_gameManager != null)
                _gameManager.OnRemoteArrangementReceived(_remoteArrangement);
        }

        public void SendMove(Move move)
        {
            if (_view == null || _shuttingDown) return;

            _view.RPC("OnMoveInfo", RpcTarget.Others,
                move.PromotionType.HasValue ? (int)move.PromotionType.Value : 0,
                move.IsCastling ? 1 : 0);
            _view.RPC("OnMoveReceived", RpcTarget.Others,
                move.From.X, move.From.Y, move.To.X, move.To.Y);
        }

        [PunRPC]
        public void OnMoveInfo(int promotionType, int castling)
        {
            _pendingPromotion = promotionType;
            _pendingCastling = castling != 0;
        }

        [PunRPC]
        public void OnMoveReceived(int fromX, int fromY, int toX, int toY)
        {
            if (_shuttingDown) return;

            PieceType? promotion = _pendingPromotion > 0 ? (PieceType?)(PieceType)_pendingPromotion : null;
            bool castling = _pendingCastling;
            _pendingPromotion = 0;
            _pendingCastling = false;

            if (_gameManager != null)
                _gameManager.OnRemoteMoveReceived(new Position(fromX, fromY), new Position(toX, toY), promotion, castling);
        }

        public void SendPass()
        {
            if (_view != null && !_shuttingDown)
                _view.RPC("OnPassReceived", RpcTarget.Others);
        }

        [PunRPC]
        public void OnPassReceived()
        {
            if (_gameManager != null) _gameManager.OnRemotePassReceived();
        }

        public void SendGameOver(PieceColor winner)
        {
            if (_view == null || _shuttingDown) return;

            int winnerActor = winner == _localColor
                ? PhotonNetwork.LocalPlayer.ActorNumber
                : OpponentActorNumber();
            _view.RPC("OnGameOverReceived", RpcTarget.Others, winnerActor);
        }

        private int OpponentActorNumber()
        {
            var players = PhotonNetwork.PlayerList;
            for (int i = 0; i < players.Length; i++)
                if (!players[i].IsLocal) return players[i].ActorNumber;
            return -1;
        }

        [PunRPC]
        public void OnGameOverReceived(int winnerActor)
        {
            if (_shuttingDown || _gameManager == null) return;
            PieceColor winner = winnerActor == PhotonNetwork.LocalPlayer.ActorNumber
                ? _localColor
                : (_localColor == PieceColor.White ? PieceColor.Black : PieceColor.White);
            _gameManager.OnRemoteGameOverReceived(winner);
        }

        public void Shutdown()
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            StopAllCoroutines();

            if (_lobby != null)
            {
                _lobby.Destroy();
                _lobby = null;
            }

            if (_view != null && _view.ViewID != 0)
                _view.ViewID = 0;

            if (PhotonNetwork.InRoom) PhotonNetwork.LeaveRoom();
            if (PhotonNetwork.IsConnected) PhotonNetwork.Disconnect();

            Destroy(gameObject);
        }
    }

    public class OnlineLobbyUI
    {
        private readonly GameObject root;
        private readonly GameObject normalGroup;
        private readonly GameObject coinGroup;
        private readonly Text roomText;
        private readonly Text statusText;
        private readonly Text selfText;
        private readonly Text opponentText;
        private readonly Text coinText;
        private readonly Text readyLabel;
        private readonly Button readyButton;

        private static readonly Color TitleColor = new Color(0.98f, 0.9f, 0.7f, 1f);
        private static readonly Color BodyColor = new Color(0.88f, 0.86f, 0.8f, 1f);

        public OnlineLobbyUI(Canvas canvas, Action onReady, Action onLeave)
        {
            Font font = RuntimeUI.ResolveFont(canvas);

            root = RuntimeUI.CreatePanel(canvas.transform, "OnlineLobby", new Color(0.012f, 0.016f, 0.024f, 0.95f));
            root.transform.SetAsLastSibling();

            normalGroup = new GameObject("Normal", typeof(RectTransform));
            var normalRect = normalGroup.GetComponent<RectTransform>();
            normalRect.SetParent(root.transform, false);
            RuntimeUI.Stretch(normalRect);

            var title = RuntimeUI.CreateText(normalGroup.transform, "Title", "联 机 对 战", 64, TitleColor,
                TextAnchor.MiddleCenter, font);
            title.rectTransform.anchoredPosition = new Vector2(0f, 250f);

            roomText = RuntimeUI.CreateText(normalGroup.transform, "RoomCode", string.Empty, 40, TitleColor,
                TextAnchor.MiddleCenter, font);
            roomText.rectTransform.anchoredPosition = new Vector2(0f, 165f);

            statusText = RuntimeUI.CreateText(normalGroup.transform, "Status", string.Empty, 24, BodyColor,
                TextAnchor.MiddleCenter, font);
            statusText.rectTransform.anchoredPosition = new Vector2(0f, 105f);

            selfText = RuntimeUI.CreateText(normalGroup.transform, "SelfState", string.Empty, 26, BodyColor,
                TextAnchor.MiddleCenter, font);
            selfText.rectTransform.anchoredPosition = new Vector2(0f, 40f);

            opponentText = RuntimeUI.CreateText(normalGroup.transform, "OpponentState", string.Empty, 26, BodyColor,
                TextAnchor.MiddleCenter, font);
            opponentText.rectTransform.anchoredPosition = new Vector2(0f, 0f);

            var readyButtonObject = RuntimeUI.CreateButton(normalGroup.transform, "ReadyButton", "准备",
                new Vector2(0f, -105f), new Vector2(300f, 72f), font, onReady);
            readyButton = readyButtonObject;
            readyLabel = readyButtonObject.GetComponentInChildren<Text>();

            RuntimeUI.CreateButton(normalGroup.transform, "LeaveButton", "退出房间",
                new Vector2(0f, -220f), new Vector2(220f, 58f), font, onLeave);

            coinGroup = new GameObject("Coin", typeof(RectTransform));
            var coinRect = coinGroup.GetComponent<RectTransform>();
            coinRect.SetParent(root.transform, false);
            RuntimeUI.Stretch(coinRect);

            coinText = RuntimeUI.CreateText(coinGroup.transform, "CoinText", "正", 84, TitleColor,
                TextAnchor.MiddleCenter, font);
            coinText.rectTransform.anchoredPosition = Vector2.zero;
            coinGroup.SetActive(false);
        }

        public void ShowConnecting()
        {
            root.SetActive(true);
            normalGroup.SetActive(true);
            coinGroup.SetActive(false);
            roomText.text = string.Empty;
            SetStatus("正在连接 Photon 服务器…");
            selfText.text = string.Empty;
            opponentText.text = string.Empty;
            readyButton.gameObject.SetActive(false);
        }

        public void ShowRoom(string roomCode)
        {
            root.SetActive(true);
            normalGroup.SetActive(true);
            coinGroup.SetActive(false);
            roomText.text = "房间号  " + roomCode;
            SetStatus("把房间号告诉好友，双方都点「准备」后猜先");
            readyButton.gameObject.SetActive(true);
        }

        public void SetStatus(string message)
        {
            statusText.text = message;
            statusText.color = BodyColor;
        }

        public void ShowError(string message)
        {
            statusText.text = message;
            statusText.color = new Color(1f, 0.55f, 0.45f, 1f);
            readyButton.gameObject.SetActive(false);
        }

        public void SetPlayers(bool selfReady, bool opponentPresent, bool opponentReady)
        {
            selfText.text = selfReady ? "你：已准备" : "你：未准备";
            if (!opponentPresent)
                opponentText.text = "对手：等待加入…";
            else
                opponentText.text = opponentReady ? "对手：已准备" : "对手：未准备";
            if (readyLabel != null)
                readyLabel.text = selfReady ? "取消准备" : "准备";
        }

        public void ShowCoin()
        {
            normalGroup.SetActive(false);
            coinGroup.SetActive(true);
        }

        public void SetCoinText(string text)
        {
            coinText.text = text;
        }

        public void Hide()
        {
            root.SetActive(false);
        }

        public void Destroy()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }
}
#endif
