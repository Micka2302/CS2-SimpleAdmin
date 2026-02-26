using System.Data.Common;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using CS2_SimpleAdminApi;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Data.SQLite;

namespace CS2_SimpleAdmin_BanCheckModule;

public class CS2_SimpleAdmin_BanCheckModule : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName => "[CS2-SimpleAdmin] BanCheck Module";
    public override string ModuleVersion => "v1.0.0";
    public override string ModuleAuthor => "micka";

    public PluginConfig Config { get; set; } = new();

    private readonly PluginCapability<ICS2_SimpleAdminApi?> _pluginCapability = new("simpleadmin:api");
    private ICS2_SimpleAdminApi? _api;

    private string _connectionString = string.Empty;
    private int? _serverId;
    private DatabaseKind _databaseKind = DatabaseKind.MySql;

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        if (!TryResolveApi())
        {
            Unload(false);
            return;
        }

        var api = _api!;
        _connectionString = api.GetConnectionString();
        _serverId = api.GetServerId();
        _databaseKind = DetectDatabaseKind(_connectionString);

        RegisterListener<Listeners.OnClientConnect>(OnClientConnect);

        Logger.LogInformation(
            "[BanCheck] Module enabled. Database={DatabaseKind}, ServerScope={ServerScope}, ServerId={ServerId}",
            _databaseKind,
            Config.UseServerIdScope,
            _serverId?.ToString() ?? "null");
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnClientConnect>(OnClientConnect);
    }

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
    }

    private bool TryResolveApi()
    {
        try
        {
            _api = _pluginCapability.Get();
        }
        catch (KeyNotFoundException ex)
        {
            Logger.LogError(ex,
                "[BanCheck] CS2-SimpleAdmin API capability 'simpleadmin:api' is missing. Ensure CS2-SimpleAdmin is loaded before this module.");
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BanCheck] Failed to resolve CS2-SimpleAdmin API capability.");
            return false;
        }

        if (_api == null)
        {
            Logger.LogError("[BanCheck] CS2-SimpleAdmin API capability returned null.");
            return false;
        }

        return true;
    }

    private void OnClientConnect(int playerSlot, string _, string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            Logger.LogError("[BanCheck] Missing database connection string, skipping check.");
            return;
        }

        var initialIp = ExtractIp(ipAddress);
        ValidatePlayerOnConnect(playerSlot, initialIp, 0);
    }

    private void ValidatePlayerOnConnect(int playerSlot, string? initialIp, int attempt)
    {
        Server.NextWorldUpdate(() =>
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || !player.IsValid || player.IsBot)
            {
                if (attempt < Config.ResolvePlayerMaxAttempts)
                {
                    AddTimer(Math.Max(0.01f, Config.ResolveRetryDelaySeconds),
                        () => ValidatePlayerOnConnect(playerSlot, initialIp, attempt + 1));
                }

                return;
            }

            var steamId = player.SteamID.ToString();
            if (string.IsNullOrWhiteSpace(steamId) || steamId == "0")
            {
                if (attempt < Config.ResolvePlayerMaxAttempts)
                {
                    AddTimer(Math.Max(0.01f, Config.ResolveRetryDelaySeconds),
                        () => ValidatePlayerOnConnect(playerSlot, initialIp, attempt + 1));
                }

                return;
            }

            var playerIp = ExtractIp(player.IpAddress) ?? initialIp;
            ClearNativeBanIdList(player.SteamID);

            _ = Task.Run(async () =>
            {
                BanCheckResult result;
                try
                {
                    result = await EvaluateBanStateAsync(steamId, playerIp);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[BanCheck] Failed to validate bans for {SteamId}.", steamId);
                    return;
                }

                Server.NextWorldUpdate(() =>
                {
                    var currentPlayer = Utilities.GetPlayerFromSlot(playerSlot);
                    if (currentPlayer == null || !currentPlayer.IsValid || currentPlayer.IsBot)
                        return;

                    if (currentPlayer.SteamID.ToString() != steamId)
                        return; // Slot reused by another player while async check was running.

                    Logger.LogInformation(
                        "[BanCheck] Status for {PlayerName} ({SteamId}) - IsBanned={IsBanned}, SteamBanned={SteamBanned}, IpBanned={IpBanned}, IpBanBypassedBySteamUnban={IpBanBypassedBySteamUnban}, LatestSteamUnbanId={LatestSteamUnbanId}, Ip={PlayerIp}, ExpiredUpdated={ExpiredUpdated}, ActiveBanId={ActiveBanId}, ActiveBanReason={ActiveBanReason}, SteamBanId={SteamBanId}, SteamBanReason={SteamBanReason}, IpBanId={IpBanId}, IpBanReason={IpBanReason}",
                        currentPlayer.PlayerName,
                        steamId,
                        result.IsBlocked,
                        result.SteamBanned,
                        result.IpBanned,
                        result.IpBanBypassedBySteamUnban,
                        result.LatestSteamUnbanId,
                        playerIp ?? "Unknown",
                        result.ExpiredBanUpdated,
                        result.ActiveBanId,
                        result.ActiveBanReason ?? "None",
                        result.SteamBanId,
                        result.SteamBanReason ?? "None",
                        result.IpBanId,
                        result.IpBanReason ?? "None");

                    if (result.IsBlocked)
                    {
                        Logger.LogInformation("[BanCheck] Blocking {PlayerName} ({SteamId}) due to active ban.",
                            currentPlayer.PlayerName, steamId);
                        DisconnectBlockedPlayer(currentPlayer);
                        return;
                    }

                    if (!Config.SendJoinMessage)
                        return;

                    var message = result.ExpiredBanUpdated
                        ? Translate("bancheck_join_expired",
                            "{green}[BanCheck]{default} Votre ban est expire, connexion autorisee.")
                        : Translate("bancheck_join_enabled",
                            "{green}[BanCheck]{default} Verification BanCheck activee.");

                    currentPlayer.PrintToChat(message);
                });
            });
        });
    }

    private async Task<BanCheckResult> EvaluateBanStateAsync(string steamId, string? playerIp)
    {
        var checkIp = Config.CheckIpBans && !string.IsNullOrWhiteSpace(playerIp);
        var playerCondition = checkIp
            ? "(player_steamid = @PlayerSteamID OR player_ip = @PlayerIP)"
            : "player_steamid = @PlayerSteamID";
        var serverCondition = Config.UseServerIdScope && _serverId.HasValue ? "AND server_id = @ServerId" : string.Empty;
        var parameters = new
        {
            PlayerSteamID = steamId,
            PlayerIP = checkIp ? playerIp : null,
            ServerId = _serverId
        };

        var expireSql = $"""
            UPDATE sa_bans
            SET status = 'EXPIRED'
            WHERE status = 'ACTIVE'
              AND duration > 0
              AND ends <= CURRENT_TIMESTAMP
              AND {playerCondition}
              {serverCondition};
            """;

        var steamActiveSql = $"""
            SELECT COUNT(*)
            FROM sa_bans
            WHERE status = 'ACTIVE'
              AND (duration = 0 OR ends IS NULL OR ends > CURRENT_TIMESTAMP)
              AND player_steamid = @PlayerSteamID
              {serverCondition};
            """;

        var ipActiveSql = $"""
            SELECT COUNT(*)
            FROM sa_bans
            WHERE status = 'ACTIVE'
              AND (duration = 0 OR ends IS NULL OR ends > CURRENT_TIMESTAMP)
              AND player_ip = @PlayerIP
              {serverCondition};
            """;

        var activeDetailSql = $"""
            SELECT id AS Id, reason AS Reason
            FROM sa_bans
            WHERE status = 'ACTIVE'
              AND (duration = 0 OR ends IS NULL OR ends > CURRENT_TIMESTAMP)
              AND {playerCondition}
              {serverCondition}
            ORDER BY id DESC
            LIMIT 1;
            """;

        var steamDetailSql = $"""
            SELECT id AS Id, reason AS Reason
            FROM sa_bans
            WHERE status = 'ACTIVE'
              AND (duration = 0 OR ends IS NULL OR ends > CURRENT_TIMESTAMP)
              AND player_steamid = @PlayerSteamID
              {serverCondition}
            ORDER BY id DESC
            LIMIT 1;
            """;

        var ipDetailSql = $"""
            SELECT id AS Id, reason AS Reason
            FROM sa_bans
            WHERE status = 'ACTIVE'
              AND (duration = 0 OR ends IS NULL OR ends > CURRENT_TIMESTAMP)
              AND player_ip = @PlayerIP
              {serverCondition}
            ORDER BY id DESC
            LIMIT 1;
            """;

        var latestSteamUnbanSql = $"""
            SELECT MAX(id)
            FROM sa_bans
            WHERE status = 'UNBANNED'
              AND player_steamid = @PlayerSteamID
              {serverCondition};
            """;

        await using var connection = await OpenConnectionAsync();
        var expiredRows = await connection.ExecuteAsync(expireSql, parameters);
        var steamActiveRows = await connection.ExecuteScalarAsync<int>(steamActiveSql, parameters);
        var ipActiveRows = checkIp
            ? await connection.ExecuteScalarAsync<int>(ipActiveSql, parameters)
            : 0;
        var activeBan = await connection.QueryFirstOrDefaultAsync<BanDetail>(activeDetailSql, parameters);
        var steamBan = await connection.QueryFirstOrDefaultAsync<BanDetail>(steamDetailSql, parameters);
        var ipBan = checkIp
            ? await connection.QueryFirstOrDefaultAsync<BanDetail>(ipDetailSql, parameters)
            : null;
        var latestSteamUnbanId = await connection.ExecuteScalarAsync<int?>(latestSteamUnbanSql, parameters);

        var steamBanned = steamActiveRows > 0;
        var ipBanned = checkIp && ipActiveRows > 0;
        var ipBanBypassedBySteamUnban =
            checkIp &&
            ipBanned &&
            !steamBanned &&
            latestSteamUnbanId.HasValue &&
            (ipBan?.Id is null || ipBan.Id <= latestSteamUnbanId.Value);

        var isBlocked = steamBanned || (ipBanned && !ipBanBypassedBySteamUnban);

        return new BanCheckResult(
            isBlocked,
            steamBanned,
            ipBanned,
            ipBanBypassedBySteamUnban,
            latestSteamUnbanId,
            expiredRows > 0,
            activeBan?.Id,
            activeBan?.Reason,
            steamBan?.Id,
            steamBan?.Reason,
            ipBan?.Id,
            ipBan?.Reason);
    }

    private async Task<DbConnection> OpenConnectionAsync()
    {
        if (_databaseKind == DatabaseKind.MySql)
        {
            var mysql = new MySqlConnection(_connectionString);
            await mysql.OpenAsync();
            return mysql;
        }

        var sqlite = new SQLiteConnection(_connectionString);
        await sqlite.OpenAsync();
        return sqlite;
    }

    private static DatabaseKind DetectDatabaseKind(string connectionString)
    {
        var normalized = connectionString.ToLowerInvariant();
        if (normalized.Contains("server=") ||
            normalized.Contains("uid=") ||
            normalized.Contains("user id=") ||
            normalized.Contains("port="))
        {
            return DatabaseKind.MySql;
        }

        return DatabaseKind.Sqlite;
    }

    private static string? ExtractIp(string? rawIp)
    {
        if (string.IsNullOrWhiteSpace(rawIp))
            return null;

        var cleanIp = rawIp.Split(':')[0];
        return string.IsNullOrWhiteSpace(cleanIp) ? null : cleanIp;
    }

    private void ClearNativeBanIdList(ulong steamId64)
    {
        try
        {
            var steamId3 = new SteamID(steamId64).SteamId3;
            Server.ExecuteCommand($"removeid {steamId3}");
            Server.ExecuteCommand("writeid");

            Logger.LogInformation("[BanCheck] Native banid/listid cleared on connect for {SteamId3}.", steamId3);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BanCheck] Failed to clear native banid/listid for {SteamId64}.", steamId64);
        }
    }

    private void DisconnectBlockedPlayer(CCSPlayerController player)
    {
        var steamId64 = player.SteamID;
        ClearNativeBanIdList(steamId64);
        player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED);

        // Safety pass: some server setups recreate listid after disconnect handling.
        AddTimer(0.10f, () => ClearNativeBanIdList(steamId64));
    }

    private string Translate(string key, string fallback)
    {
        var value = Localizer?[key]?.Value;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private sealed class BanDetail
    {
        public int Id { get; set; }
        public string? Reason { get; set; }
    }

    private readonly record struct BanCheckResult(
        bool IsBlocked,
        bool SteamBanned,
        bool IpBanned,
        bool IpBanBypassedBySteamUnban,
        int? LatestSteamUnbanId,
        bool ExpiredBanUpdated,
        int? ActiveBanId,
        string? ActiveBanReason,
        int? SteamBanId,
        string? SteamBanReason,
        int? IpBanId,
        string? IpBanReason
    );

    private enum DatabaseKind
    {
        MySql,
        Sqlite
    }
}
