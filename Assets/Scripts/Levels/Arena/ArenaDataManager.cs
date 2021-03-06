using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BasicTools.ButtonInspector;
using Bolt;
using UnityEngine;

public class ArenaDataManager : Bolt.EntityBehaviour<IArenaState>
{
    public const int UnassignedTeamId = -1;
    public const int InvalidArrayItemId = -2;

    public static ArenaDataManager Instance { get; private set; }
    private static readonly List<Action> OnReadyListeners = new List<Action>();
    private static readonly List<Action> OnReadyOneShotListeners = new List<Action>();

    /// <summary>
    /// Always valid on server. Valid in clients after ArenaLevelManager's creation.
    /// </summary>
    public ArenaSettingsAsset arenaSettingsAsset;

    [Serializable]
    public class ArenaTeamInfo
    {
        public int teamId = -1;
        public string teamName = "";
        public Color teamColor = Color.black;
        public int kills = 0;
        public int maxCapacity = -1;
        public List<ArenaPlayerInfo> arenaPlayerInfos = new List<ArenaPlayerInfo>();

        public int cumulativePlayerScore => arenaPlayerInfos.Sum(info => info.score);
    }

    [Serializable]
    public class ArenaPlayerInfo
    {
        public int teamId = -1;
        public int playerId = -1;
        public string playerName = "";
        public bool isServer = false;
        public int score = 0;
        public int kills = 0;
        public int assists = 0;
        public int deaths = 0;
        public float ping = 0;

        #region Server Fields

        /// <summary>
        /// [Only valid in Server]
        /// </summary>
        public readonly HashSet<int> attackers = new HashSet<int>();

        #endregion
    }

    public float pingRefreshInterval = 5f;
    public int killScore = 100;
    public int assistScore = 20;

    public List<ArenaTeamInfo> arenaTeamInfos = new List<ArenaTeamInfo>();
    public List<ArenaPlayerInfo> unassignedPlayers = new List<ArenaPlayerInfo>();

    public int localPlayerId { get; private set; } = -1;

    [Button("Refresh Team Infos", "RefreshTeamInfosFromState")]
    public bool refreshTeamInfos_Btn;

    private readonly Dictionary<int, ArenaPlayerInfo> arenaPlayerInfoDict = new Dictionary<int, ArenaPlayerInfo>();


    #region Server Fields

    /// <summary>
    /// Number of players connected to server. [Only valid in Server]
    /// </summary>
    public int connectedPlayersCount => BoltNetwork.Connections.Count() + 1;

    /// <summary>
    /// [Only valid in Server]
    /// </summary>
    public bool canAddPlayer => connectedPlayersCount < arenaSettingsAsset.arenaMaxCapacity;

    /// <summary>
    /// [Only valid in Server]
    /// </summary>
    private readonly Dictionary<int, BoltConnection> arenaPlayerConnectionDict = new Dictionary<int, BoltConnection>();

    /// <summary>
    /// [Only valid in Server]
    /// </summary>
    private IEnumerator pingRefresherCoroutine = null;

    /// <summary>
    /// [Only valid in Server]
    /// </summary>
    private IEnumerator countdownTimerCoroutine = null;

    /// <summary>
    /// [Only valid in Server]
    /// </summary>
    private Randomizer<ArenaTeamInfo> teamInfoRandomizer;

    /// <summary>
    /// [Only valid in Server]
    /// </summary>
    public event Action<int> OnTeamInfoChanged;

    /// <summary>
    /// [Only valid in Server]
    /// </summary>
    private float roundStartTime = float.MinValue;

    #endregion


    #region Client Fields

    /// <summary>
    /// [Only called in Client]
    /// </summary>
    public event Action OnAllTeamInfosRefreshed;

    /// <summary>
    /// [Only called in Client]
    /// </summary>
    public event Action OnUnassignedPlayersRefreshed;

    #endregion


    private static void CallOnReadyListeners()
    {
        foreach (var onReadyListener in OnReadyListeners)
        {
            onReadyListener();
        }

        foreach (var onReadyOneShotListener in OnReadyOneShotListeners)
        {
            onReadyOneShotListener();
        }

        OnReadyOneShotListeners.Clear();
    }

    public static void AddOnReadyListener(Action action, bool oneShot = false)
    {
        if (Instance != null)
        {
            action();
            if (oneShot)
            {
                return;
            }
        }

        if (oneShot)
        {
            OnReadyOneShotListeners.Add(action);
        }
        else
        {
            OnReadyListeners.Add(action);
        }
    }

    public static void RemoveOnReadyListener(Action action, bool oneShot = false)
    {
        if (oneShot)
        {
            OnReadyOneShotListeners.Remove(action);
        }
        else
        {
            OnReadyListeners.Remove(action);
        }
    }

    private void Awake()
    {
        if (Instance != null)
        {
            DestroyImmediate(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(this);
        CallOnReadyListeners();
    }

    private void OnDestroy()
    {
        if (BoltNetwork.IsServer)
        {
            pingRefresherCoroutine.Reset();
            pingRefresherCoroutine = null;
        }
    }

    public override void Attached()
    {
        base.Attached();
        SetupState();
    }

    void SetupState()
    {
        if (entity.IsOwner) // Server is always the owner for ArenaDataManager
        {
            ApplyTeamInfosToState();
            ApplyUnassignedPlayersToState();
        }
        else
        {
            state.Timer = 0;
            state.AddCallback("ArenaTeamInfo[]", ((newState, path, indices) => { RefreshTeamInfosFromState(); }));
            state.AddCallback("UnassignedPlayers[]",
                ((newState, path, indices) => { RefreshUnassignedPlayersFromState(); }));
        }
    }

    public void SetLocalPlayerId(int playerId)
    {
        Debug.Log("Setting Local Player ID: " + playerId);
        localPlayerId = playerId;
    }

    public ArenaPlayerInfo GetLocalArenaPlayerInfo()
    {
        return GetArenaPlayerInfo(localPlayerId);
    }

    public ArenaPlayerInfo GetArenaPlayerInfo(int playerId)
    {
        return arenaPlayerInfoDict.ContainsKey(playerId) ? arenaPlayerInfoDict[playerId] : null;
    }

    public ArenaTeamInfo GetArenaTeamInfoForPlayer(int playerId)
    {
        var playerInfo = GetArenaPlayerInfo(playerId);
        if (playerInfo != null && playerInfo.playerId >= 0)
        {
            return arenaTeamInfos[playerInfo.playerId];
        }

        return null;
    }

    public ArenaTeamInfo GetArenaTeamInfo(int teamId)
    {
        return teamId >= 0 && teamId < arenaTeamInfos.Count ? arenaTeamInfos[teamId] : null;
    }

    public ArenaSettingsAsset.TeamSettings GetArenaTeamSettings(int teamId)
    {
        return (teamId >= 0 && teamId < arenaSettingsAsset.teams.Length) ? arenaSettingsAsset.teams[teamId] : null;
    }

    public void PlayerAttacked(int attackerPlayerId, int victimPlayerId, float damage)
    {
        Debug.Log("AttackerPlayerId: " + attackerPlayerId);
        Debug.Log("VictimPlayerId: " + victimPlayerId);
        if (BoltNetwork.IsServer)
        {
            var attackerPlayerInfo = GetArenaPlayerInfo(attackerPlayerId);
            var victimPlayerInfo = GetArenaPlayerInfo(victimPlayerId);

            if (attackerPlayerInfo != null)
            {
                attackerPlayerInfo.score += (int) damage;
                victimPlayerInfo?.attackers.Add(attackerPlayerId);

                OnTeamInfoChanged?.Invoke(attackerPlayerInfo.teamId);

                // TODO: Apply Player Infos Individually
                ApplyTeamInfosToState();
            }
        }
        else
        {
            var playerAttackedEvent = PlayerAttackedEvent.Create(entity.Source, ReliabilityModes.ReliableOrdered);
            playerAttackedEvent.AttackerPlayerId = attackerPlayerId;
            playerAttackedEvent.VictimPlayerId = victimPlayerId;
            playerAttackedEvent.Damage = damage;
            playerAttackedEvent.Send();
        }
    }

    public void PlayerDied(int killerPlayerId, int victimPlayerId)
    {
        if (BoltNetwork.IsServer)
        {
            var killerPlayerInfo = GetArenaPlayerInfo(killerPlayerId);
            var victimPlayerInfo = GetArenaPlayerInfo(victimPlayerId);

            if (victimPlayerInfo != null)
            {
                ArenaTeamInfo killerTeamInfo = null;

                if (killerPlayerInfo != null)
                {
                    killerPlayerInfo.kills++;
                    killerPlayerInfo.score += killScore;
                    OnTeamInfoChanged?.Invoke(killerPlayerInfo.teamId);

                    killerTeamInfo = GetArenaTeamInfo(killerPlayerInfo.teamId);
                }

                if (killerTeamInfo != null)
                {
                    killerTeamInfo.kills++;
                    OnTeamInfoChanged?.Invoke(killerTeamInfo.teamId);
                }
                else
                {
                    foreach (var arenaTeamInfo in arenaTeamInfos)
                    {
                        if (arenaTeamInfo.teamId == victimPlayerInfo.teamId)
                        {
                            continue;
                        }

                        arenaTeamInfo.kills++;
                        OnTeamInfoChanged?.Invoke(arenaTeamInfo.teamId);
                    }
                }

                victimPlayerInfo.deaths++;
                OnTeamInfoChanged?.Invoke(victimPlayerInfo.teamId);

                foreach (var assistPlayerId in victimPlayerInfo.attackers)
                {
                    if (assistPlayerId == killerPlayerId)
                    {
                        continue;
                    }

                    var assistPlayerInfo = GetArenaPlayerInfo(assistPlayerId);
                    if (assistPlayerInfo != null)
                    {
                        assistPlayerInfo.assists++;
                        assistPlayerInfo.score += assistScore;
                        OnTeamInfoChanged?.Invoke(assistPlayerInfo.teamId);
                    }
                }

                victimPlayerInfo.attackers.Clear();
            }

            // TODO: Apply Player Infos and Team Infos Individually
            ApplyTeamInfosToState();
        }
        else
        {
            var playerDeathEvent = PlayerDeathEvent.Create(entity.Source, ReliabilityModes.ReliableOrdered);
            playerDeathEvent.KillerPlayerId = killerPlayerId;
            playerDeathEvent.VictimPlayerId = victimPlayerId;
            playerDeathEvent.Send();
        }
    }

    public ArenaTeamInfo GetCurrentWinningTeamInfo()
    {
        List<ArenaTeamInfo> winningTeamInfos;

        var teamsWithMaxKills = new List<ArenaTeamInfo>();
        int maxKills = 0;
        foreach (var arenaTeamInfo in arenaTeamInfos)
        {
            if (arenaTeamInfo.kills > maxKills)
            {
                teamsWithMaxKills.Clear();

                maxKills = arenaTeamInfo.kills;
                teamsWithMaxKills.Add(arenaTeamInfo);
            }
            else if (arenaTeamInfo.kills == maxKills)
            {
                teamsWithMaxKills.Add(arenaTeamInfo);
            }
        }

        if (teamsWithMaxKills.Count > 1)
        {
            var teamsWithMaxScore = new List<ArenaTeamInfo>();
            int maxScore = 0;
            foreach (var arenaTeamInfo in teamsWithMaxKills)
            {
                int teamScore = arenaTeamInfo.cumulativePlayerScore;
                if (teamScore > maxScore)
                {
                    teamsWithMaxScore.Clear();

                    maxScore = teamScore;
                    teamsWithMaxScore.Add(arenaTeamInfo);
                }
                else if (teamScore == maxScore)
                {
                    teamsWithMaxScore.Add(arenaTeamInfo);
                }
            }

            winningTeamInfos = teamsWithMaxScore;
        }
        else
        {
            winningTeamInfos = teamsWithMaxKills;
        }

        if (winningTeamInfos.Count > 1)
        {
            HelperUtilities.Rearrange(winningTeamInfos);
        }

        if (winningTeamInfos.Count > 0)
        {
            return winningTeamInfos[0];
        }

        return null;
    }


    #region Server Methods

    public void Initialize(ArenaSettingsAsset arenaSettings)
    {
        arenaSettingsAsset = arenaSettings;

        for (int i = 0; i < arenaSettingsAsset.teams.Length; i++)
        {
            arenaTeamInfos.Add(new ArenaTeamInfo
            {
                teamId = i,
                teamName = arenaSettingsAsset.teams[i].teamName,
                teamColor = arenaSettingsAsset.teams[i].teamColor,
                maxCapacity = arenaSettingsAsset.teams[i].maxCapacity
            });
        }

        teamInfoRandomizer = new Randomizer<ArenaTeamInfo>(arenaTeamInfos);

        if (pingRefresherCoroutine != null)
        {
            pingRefresherCoroutine.Reset();
            pingRefresherCoroutine = null;
        }

        pingRefresherCoroutine = RefreshPingInIntervals();
        StartCoroutine(pingRefresherCoroutine);
    }

    IEnumerator RefreshPingInIntervals()
    {
        while (gameObject.activeSelf)
        {
            foreach (var keyValuePair in arenaPlayerInfoDict)
            {
                var arenaPlayerInfo = keyValuePair.Value;

                float ping = -1;
                if (arenaPlayerInfo.isServer)
                {
                    ping = 0;
                }
                else if (arenaPlayerConnectionDict.ContainsKey(arenaPlayerInfo.playerId))
                {
//                    Debug.Log("Checking Ping...");
//                    Debug.Log("Ping Network: " + arenaPlayerConnectionDict[arenaPlayerInfo.playerId].PingNetwork);
//                    Debug.Log("Ping Aliased: " + arenaPlayerConnectionDict[arenaPlayerInfo.playerId].PingAliased);
                    ping = arenaPlayerConnectionDict[arenaPlayerInfo.playerId].PingNetwork;
                }

                arenaPlayerInfo.ping = ping;
            }

            ApplyTeamInfosToState();

            yield return new WaitForSecondsRealtime(pingRefreshInterval);
        }
    }

    public void ResetTimer()
    {
        if (BoltNetwork.IsServer)
        {
            roundStartTime = Time.time;

            if (countdownTimerCoroutine != null)
            {
                countdownTimerCoroutine.Reset();
                countdownTimerCoroutine = null;
            }

            countdownTimerCoroutine = CountdownTimer();
            StartCoroutine(countdownTimerCoroutine);
        }
    }

    IEnumerator CountdownTimer()
    {
        do
        {
            int secsElapsed = (int) (Time.time - roundStartTime);
            state.Timer = Math.Max(arenaSettingsAsset.roundDuration - secsElapsed, 0);

            yield return new WaitForSecondsRealtime(1);
        } while (state.Timer > 0);


        var winningTeamInfo = GetCurrentWinningTeamInfo();

        var arenaRoundEndedEvent =
            ArenaRoundEndedEvent.Create(GlobalTargets.Everyone, ReliabilityModes.ReliableOrdered);
        arenaRoundEndedEvent.WinnerTeamID = winningTeamInfo?.teamId ?? -1;
        arenaRoundEndedEvent.Send();

        countdownTimerCoroutine = null;
    }

    public void OnBoltPlayerConnected(BoltConnection boltConnection, ArenaLobby.JoinResult joinResult)
    {
        int playerId = GetPlayerIdFromConnection(boltConnection);

        if (arenaPlayerInfoDict.ContainsKey(playerId))
        {
            return;
        }

        arenaPlayerConnectionDict[playerId] = boltConnection;

        ArenaPlayerInfo arenaPlayerInfo = new ArenaPlayerInfo()
        {
            teamId = UnassignedTeamId,
            playerId = joinResult.arenaPlayerId,
            playerName = joinResult.account.name,
            isServer = boltConnection == null
        };
        unassignedPlayers.Add(arenaPlayerInfo);
        OnTeamInfoChanged?.Invoke(UnassignedTeamId);

        arenaPlayerInfoDict[playerId] = arenaPlayerInfo;

        PlacePlayerInRandomTeam(arenaPlayerInfo);

        ApplyTeamInfosToState();
        ApplyUnassignedPlayersToState();
    }

    public void OnBoltPlayerDisconnected(BoltConnection boltConnection)
    {
        int playerId = GetPlayerIdFromConnection(boltConnection);

        if (!arenaPlayerInfoDict.ContainsKey(playerId))
        {
            return;
        }

        arenaPlayerConnectionDict.Remove(playerId);

        var arenaPlayerInfo = arenaPlayerInfoDict[playerId];
        if (arenaPlayerInfo.teamId >= 0)
        {
            arenaTeamInfos[arenaPlayerInfo.teamId].arenaPlayerInfos.Remove(arenaPlayerInfo);
            OnTeamInfoChanged?.Invoke(arenaPlayerInfo.teamId);

            ApplyTeamInfosToState();
        }
        else
        {
            unassignedPlayers.Remove(arenaPlayerInfo);
            OnTeamInfoChanged?.Invoke(UnassignedTeamId);

            ApplyUnassignedPlayersToState();
        }

        arenaPlayerInfoDict.Remove(playerId);
    }

    public void PlacePlayerInTeam(ArenaPlayerInfo arenaPlayerInfo, ArenaTeamInfo teamInfo)
    {
        if (arenaPlayerInfo.teamId >= 0)
        {
            arenaTeamInfos[arenaPlayerInfo.teamId].arenaPlayerInfos.Remove(arenaPlayerInfo);
            OnTeamInfoChanged?.Invoke(arenaPlayerInfo.teamId);
        }
        else
        {
            unassignedPlayers.Remove(arenaPlayerInfo);
            OnTeamInfoChanged?.Invoke(UnassignedTeamId);
        }

        teamInfo.arenaPlayerInfos.Add(arenaPlayerInfo);
        arenaPlayerInfo.teamId = teamInfo.teamId;

        OnTeamInfoChanged?.Invoke(teamInfo.teamId);
    }

    public void PlacePlayerInRandomTeam(ArenaPlayerInfo arenaPlayerInfo)
    {
        ArenaTeamInfo randomTeam = null;

        for (int i = 0; i < teamInfoRandomizer.items.Count * 2; i++)
        {
            var tmpTeamInfo = teamInfoRandomizer.GetRandomItem();

            var teamSettings = arenaSettingsAsset.teams[tmpTeamInfo.teamId];
            if (teamSettings.maxCapacity < 0 ||
                tmpTeamInfo.arenaPlayerInfos.Count < teamSettings.maxCapacity)
            {
                randomTeam = tmpTeamInfo;
                break;
            }
        }

        if (randomTeam != null)
        {
            PlacePlayerInTeam(arenaPlayerInfo, randomTeam);
        }
    }

    public int GetPlayerIdFromConnection(BoltConnection boltConnection)
    {
        if (boltConnection != null)
        {
            var joinResult = boltConnection.AcceptToken as ArenaLobby.JoinResult;
            if (joinResult != null)
            {
                return joinResult.arenaPlayerId;
            }
        }
        else
        {
            return localPlayerId;
        }

        return -1;
    }

    public void DisconnectUnassignedPlayers()
    {
        foreach (var unassignedPlayer in unassignedPlayers)
        {
            arenaPlayerConnectionDict[unassignedPlayer.playerId].Disconnect();
        }
    }

    void ApplyPlayerInfoToState(ArenaPlayerInfo arenaPlayerInfo)
    {
        // TODO: Only apply the modified player info
        ApplyTeamInfosToState();
    }

    void ApplyTeamInfosToState()
    {
        for (int i = 0; i < state.ArenaTeamInfo.Length; i++)
        {
            var stateTeamInfo = state.ArenaTeamInfo[i];

            if (i < arenaTeamInfos.Count)
            {
                var teamInfo = arenaTeamInfos[i];
                CopyToState(teamInfo, stateTeamInfo);
            }
            else
            {
                stateTeamInfo.TeamId = InvalidArrayItemId;
            }
        }
    }

    void ApplyUnassignedPlayersToState()
    {
        for (int i = 0;
            i < state.UnassignedPlayers.Length;
            i++)
        {
            var statePlayerInfo = state.UnassignedPlayers[i];

            if (i < unassignedPlayers.Count)
            {
                var playerInfo = unassignedPlayers[i];
                CopyToState(playerInfo, statePlayerInfo);
            }
            else
            {
                statePlayerInfo.TeamId = InvalidArrayItemId;
            }
        }
    }

    void CopyToState(ArenaPlayerInfo playerInfo, ArenaPlayerInfoStateObj statePlayerInfo)
    {
        statePlayerInfo.TeamId = playerInfo.teamId;
        statePlayerInfo.PlayerId = playerInfo.playerId;
        statePlayerInfo.PlayerName = playerInfo.playerName;
        statePlayerInfo.IsServer = playerInfo.isServer;
        statePlayerInfo.Score = playerInfo.score;
        statePlayerInfo.Kills = playerInfo.kills;
        statePlayerInfo.Assists = playerInfo.assists;
        statePlayerInfo.Deaths = playerInfo.deaths;
        statePlayerInfo.Ping = playerInfo.ping;
    }

    void CopyToState(ArenaTeamInfo teamInfo, ArenaTeamInfoStateObj stateTeamInfo)
    {
        stateTeamInfo.TeamId = teamInfo.teamId;
        stateTeamInfo.TeamName = teamInfo.teamName;
        stateTeamInfo.TeamColor = teamInfo.teamColor;
        stateTeamInfo.Kills = teamInfo.kills;
        stateTeamInfo.MaxCapacity = teamInfo.maxCapacity;

        for (int j = 0;
            j < stateTeamInfo.ArenaPlayerInfos.Length;
            j++)
        {
            var statePlayerInfo = stateTeamInfo.ArenaPlayerInfos[j];

            if (j < teamInfo.arenaPlayerInfos.Count)
            {
                var playerInfo = teamInfo.arenaPlayerInfos[j];
                CopyToState(playerInfo, statePlayerInfo);
            }
            else
            {
                statePlayerInfo.TeamId = InvalidArrayItemId;
            }
        }
    }

    #endregion


    #region Client Methods

    void RefreshTeamInfosFromState()
    {
        Debug.Log("Refreshing team infos");
        arenaTeamInfos.Clear();

        foreach (var stateTeamInfo in state.ArenaTeamInfo)
        {
            if (stateTeamInfo.TeamId == InvalidArrayItemId)
            {
                break;
            }

            arenaTeamInfos.Add(CreateFromState(stateTeamInfo));
        }

        OnAllTeamInfosRefreshed?.Invoke();
    }

    void RefreshUnassignedPlayersFromState()
    {
        Debug.Log("Refreshing unassigned players");
        unassignedPlayers.Clear();

        foreach (var statePlayerInfo in state.UnassignedPlayers)
        {
            if (statePlayerInfo.TeamId == InvalidArrayItemId)
            {
                break;
            }

            unassignedPlayers.Add(CreateFromState(statePlayerInfo));
        }

        OnUnassignedPlayersRefreshed?.Invoke();
    }


    ArenaPlayerInfo CreateFromState(ArenaPlayerInfoStateObj statePlayerInfo)
    {
        ArenaPlayerInfo playerInfo = new ArenaPlayerInfo()
        {
            teamId = statePlayerInfo.TeamId,
            playerId = statePlayerInfo.PlayerId,
            playerName = statePlayerInfo.PlayerName,
            isServer = statePlayerInfo.IsServer,
            score = statePlayerInfo.Score,
            kills = statePlayerInfo.Kills,
            assists = statePlayerInfo.Assists,
            deaths = statePlayerInfo.Deaths,
            ping = statePlayerInfo.Ping
        };

        arenaPlayerInfoDict[playerInfo.playerId] = playerInfo;

        return playerInfo;
    }

    ArenaTeamInfo CreateFromState(ArenaTeamInfoStateObj stateTeamInfo)
    {
        ArenaTeamInfo teamInfo = new ArenaTeamInfo()
        {
            teamId = stateTeamInfo.TeamId,
            teamName = stateTeamInfo.TeamName,
            teamColor = stateTeamInfo.TeamColor,
            kills = stateTeamInfo.Kills,
            maxCapacity = stateTeamInfo.MaxCapacity
        };

        foreach (var statePlayerInfo in stateTeamInfo.ArenaPlayerInfos)
        {
            if (statePlayerInfo.TeamId == InvalidArrayItemId)
            {
                break;
            }

            teamInfo.arenaPlayerInfos.Add(CreateFromState(statePlayerInfo));
        }

        return teamInfo;
    }

    #endregion
}