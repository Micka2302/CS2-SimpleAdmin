using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CS2_SimpleAdmin.Managers;
using CS2_SimpleAdmin.Models;
using CS2_SimpleAdminApi;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using CounterStrikeSharp.API.Modules.Utils;

namespace CS2_SimpleAdmin;

public partial class CS2_SimpleAdmin
{
    private const int MaxBanConnectAttempts = 10;

    private bool _serverLoading;
    
    private void BanCheckLog(LogLevel level, string message)
    {
        // Mirror BanCheck messages to console even if log level is higher
        switch (level)
        {
            case LogLevel.Error:
                _logger?.LogError(message);
                break;
            case LogLevel.Warning:
                _logger?.LogWarning(message);
                break;
            default:
                _logger?.LogInformation(message);
                break;
        }

        Server.PrintToConsole(message);
    }

    private void RegisterEvents()
    {
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientConnect>(OnClientConnect);
        RegisterListener<Listeners.OnClientConnected>(OnClientConnected);
        RegisterListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
        if (Config.OtherSettings.UserMessageGagChatType)
            HookUserMessage(118, HookUmChat);

        AddCommandListener(null, ComamndListenerHandler);
        // AddCommandListener("callvote", OnCommandCallVote);
        // AddCommandListener("say", OnCommandSay);
        // AddCommandListener("say_team", OnCommandTeamSay);
    }

    private void UnregisterEvents()
    {
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        RemoveListener<Listeners.OnClientConnect>(OnClientConnect);
        RemoveListener<Listeners.OnClientConnected>(OnClientConnected);
        RemoveListener<Listeners.OnGameServerSteamAPIActivated>(OnGameServerSteamAPIActivated);
        if (Config.OtherSettings.UserMessageGagChatType)
            UnhookUserMessage(118, HookUmChat);

        RemoveCommandListener(null!, ComamndListenerHandler, HookMode.Pre);
        // AddCommandListener("callvote", OnCommandCallVote);
        // AddCommandListener("say", OnCommandSay);
        // AddCommandListener("say_team", OnCommandTeamSay);
    }


    // private HookResult OnCommandCallVote(CCSPlayerController? caller, CommandInfo info)
    // {
    //     var voteType = info.GetArg(1).ToLower();
    //     
    //     if (voteType != "kick")
    //         return HookResult.Continue;
    //
    //     var target = int.TryParse(info.GetArg(2), out var userId) 
    //         ? Utilities.GetPlayerFromUserid(userId) 
    //         : null;
    //     
    //     if (target == null || !target.IsValid || target.Connected != PlayerConnectedState.PlayerConnected)
    //         return HookResult.Continue;
    //
    //     return !AdminManager.CanPlayerTarget(caller, target) ? HookResult.Stop : HookResult.Continue;
    // }

    private void OnGameServerSteamAPIActivated()
    {
        if (ServerLoaded || _serverLoading)
            return;

        _serverLoading = true;
        new ServerManager().LoadServerData();
    }

    [GameEventHandler]
    public HookResult OnClientDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Reason is 149 or 6)
            info.DontBroadcast = true;

        var player = @event.Userid;

#if DEBUG
        Logger.LogCritical("[OnClientDisconnect] Before");
#endif

        if (player == null || !player.IsValid || player.IsHLTV)
            return HookResult.Continue;

        CachedPlayers.Remove(player);
        BotPlayers.Remove(player);
        SilentPlayers.Remove(player.Slot);
        GodPlayers.Remove(player.Slot);
        SpeedPlayers.Remove(player.Slot);
        GravityPlayers.Remove(player.Slot);

        if (player.IsBot)
        {
            return HookResult.Continue;
        }

        Server.ExecuteCommand($"mm_removeexcludeslot {player.Slot}");

#if DEBUG
        Logger.LogCritical("[OnClientDisconnect] After Check");
#endif

        try
        {
            if (DisconnectedPlayers.Count >= Config.OtherSettings.DisconnectedPlayersHistoryCount)
                DisconnectedPlayers.RemoveAt(0);

            var steamId = new SteamID(player.SteamID);
            var disconnectedPlayer = DisconnectedPlayers.FirstOrDefault(p => p.SteamId == steamId);

            if (disconnectedPlayer != null)
            {
                disconnectedPlayer.Name = player.PlayerName;
                disconnectedPlayer.IpAddress = player.IpAddress?.Split(":")[0];
                disconnectedPlayer.DisconnectTime = Time.ActualDateTime();
            }
            else
            {
                DisconnectedPlayers.Add(new DisconnectedPlayer(steamId, player.PlayerName,
                    player.IpAddress?.Split(":")[0], Time.ActualDateTime()));
            }

            PlayerPenaltyManager.RemoveAllPenalties(player.Slot);

            CachedPlayers.Remove(player);
            SilentPlayers.Remove(player.Slot);
            GodPlayers.Remove(player.Slot);
            SpeedPlayers.Remove(player.Slot);
            GravityPlayers.Remove(player.Slot);

            if (player.UserId.HasValue)
                PlayersInfo.TryRemove(player.SteamID, out _);

            if (!PermissionManager.AdminCache.TryGetValue(steamId, out var data)
                || !(data.ExpirationTime <= Time.ActualDateTime()))
            {
                return HookResult.Continue;
            }

            AdminManager.RemovePlayerPermissions(steamId, PermissionManager.AdminCache[steamId].Flags.ToArray());
            AdminManager.RemovePlayerFromGroup(steamId, true, PermissionManager.AdminCache[steamId].Flags.ToArray());
            var adminData = AdminManager.GetPlayerAdminData(steamId);

            if (adminData == null || data.Flags.ToList().Count != 0 && adminData.Groups.ToList().Count != 0)
                return HookResult.Continue;

            AdminManager.ClearPlayerPermissions(steamId);
            AdminManager.RemovePlayerAdminData(steamId);

            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            Logger.LogError($"An error occurred in OnClientDisconnect: {ex.Message}");
            return HookResult.Continue;
        }
    }
    private void OnClientConnect(int playerslot, string name, string ipAddress)
    {
#if DEBUG
        Logger.LogCritical("[OnClientConnect]");
#endif

        if (Instance.CacheManager != null || Instance.BanManager != null)
        {
            var initialIp = ipAddress.Split(':')[0];
            EnforceBanOnConnect(playerslot, initialIp, 0);
        }

        var player = Utilities.GetPlayerFromSlot(playerslot);
        if (player == null || !player.IsValid || player.IsBot)
            return;

        PlayerManager.LoadPlayerData(player);
    }

    private void EnforceBanOnConnect(int playerSlot, string? initialIp, int attempt)
    {
        Server.NextWorldUpdate(() =>
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || !player.IsValid || player.IsBot)
            {
                if (attempt < MaxBanConnectAttempts)
                    AddTimer(0.1f, () => EnforceBanOnConnect(playerSlot, initialIp, attempt + 1));
                else
                    BanCheckLog(LogLevel.Warning, $"[BanCheck] Unable to resolve player in slot {playerSlot} after {attempt} attempts.");
                return;
            }

            var cacheManager = Instance.CacheManager;
            var banManager = Instance.BanManager;

            if (cacheManager == null && banManager == null)
                return;

            var steamId = player.SteamID.ToString();
            var steamId64 = player.SteamID;
            var playerIp = player.IpAddress?.Split(':')[0] ?? initialIp;
            var config = Config.OtherSettings;
            var ipCheckEnabled = config.BanType != 0 && !string.IsNullOrWhiteSpace(playerIp);

            var steamCached = cacheManager?.IsPlayerBanned(player.PlayerName, steamId64, null) ?? false;
            var ipCached = ipCheckEnabled && playerIp != null &&
                cacheManager?.IsPlayerBanned(player.PlayerName, null, playerIp) == true;

            var steamActive = steamCached;
            var ipActive = ipCached;

            if (banManager != null)
            {
                try
                {
                    steamActive = banManager.IsSteamIdBanned(steamId);
                }
                catch (Exception ex)
                {
                    BanCheckLog(LogLevel.Error, $"[BanCheck] Unable to validate Steam ban for {steamId}: {ex.Message}");
                    steamActive = steamCached || cacheManager?.IsPlayerBanned(player.PlayerName, steamId64, null) == true;
                }

                if (ipCheckEnabled && playerIp != null)
                {
                    try
                    {
                        ipActive = banManager.IsIpBanned(playerIp);
                    }
                    catch (Exception ex)
                    {
                        BanCheckLog(LogLevel.Error, $"[BanCheck] Unable to validate IP ban for {playerIp}: {ex.Message}");
                        ipActive = ipCached || cacheManager?.IsPlayerBanned(player.PlayerName, null, playerIp) == true;
                    }
                }
            }

            BanCheckLog(
                LogLevel.Information,
                $"[BanCheck] Player {player.PlayerName} ({steamId}) - Steam banned: {steamActive}, IP banned: {ipActive}, IP: {playerIp ?? "Unknown"}"
            );

            if (!steamActive && !ipActive)
            {
                BanCheckLog(
                    LogLevel.Information,
                    $"[BanCheck] Player {player.PlayerName} ({steamId}) allowed to join (no active bans)."
                );
                return;
            }

            if (cacheManager != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await cacheManager.RefreshCacheAsync();
                    }
                    catch (Exception ex)
                    {
                        BanCheckLog(LogLevel.Error, $"[BanCheck] Unable to refresh ban cache for {steamId}: {ex.Message}");
                    }
                });
            }

            BanCheckLog(
                LogLevel.Information,
                $"[BanCheck] Blocking player {player.PlayerName} ({steamId}) due to active bans (Steam: {steamActive}, IP: {ipActive}). IP: {playerIp ?? "Unknown"}"
            );

            Helper.KickPlayer(player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED);
        });
    }

    private void OnClientConnected(int playerslot)
    {
#if DEBUG
        Logger.LogCritical("[OnClientConnected]");
#endif

        var player = Utilities.GetPlayerFromSlot(playerslot);
        if (player == null || !player.IsValid || player.IsBot)
            return;

        if (!CachedPlayers.Contains(player))
            CachedPlayers.Add(player);

        PlayerManager.LoadPlayerData(player);
    }

    //     private void OnClientConnect(int playerslot, string name, string ipaddress)
    //     {
    // #if DEBUG
    //         Logger.LogCritical("[OnClientConnect]");
    // #endif
    //         if (Config.OtherSettings.BanType == 0)
    //             return;
    //         
    //         if (Instance.CacheManager != null && !Instance.CacheManager.IsPlayerBanned(null, ipaddress.Split(":")[0]))
    //                 return;
    //         
    //         var testPlayer = Utilities.GetPlayerFromSlot(playerslot);
    //         if (testPlayer == null)
    //             return;
    //         Logger.LogInformation($"Gracz {testPlayer.PlayerName} ({testPlayer.SteamID.ToString()}) Czas: {DateTime.Now}");
    //
    //         Server.NextFrame((() =>
    //         {
    //             var player = Utilities.GetPlayerFromSlot(playerslot);
    //             if (player == null || !player.IsValid || player.IsBot)
    //                 return;
    //
    //             Helper.KickPlayer(player, NetworkDisconnectionReason.NETWORK_DISCONNECT_REJECT_BANNED);
    //         }));
    //         
    //         // Server.NextFrame(() =>
    //         // {
    //         //     var player = Utilities.GetPlayerFromSlot(playerslot);
    //         //
    //         //     if (player == null || !player.IsValid || player.IsBot)
    //         //         return;
    //         //
    //         //     new PlayerManager().LoadPlayerData(player);
    //         // });
    //     }

    [GameEventHandler]
    public HookResult OnPlayerFullConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player == null || !player.IsValid)
            return HookResult.Continue;

        if (player is { IsBot: true, IsHLTV: false })
        {
            BotPlayers.Add(player);
            return HookResult.Continue;
        }

        PlayerManager.LoadPlayerData(player, true);

        var hidePermissions = Config.OtherSettings.HideAdminsOnJoinPermission;
        if (hidePermissions.Count == 0 ||
            !hidePermissions.Any(permission => AdminManager.PlayerHasPermissions(player, permission)))
        {
            return HookResult.Continue;
        }

        var playerSlot = player.Slot;

        AddTimer(0.5f, () =>
        {
            var hidePlayer = Utilities.GetPlayerFromSlot(playerSlot);

            if (hidePlayer == null || !hidePlayer.IsValid || hidePlayer.IsBot)
                return;

            hidePlayer.ChangeTeam(CsTeam.Spectator);
        });
        AddTimer(0.65f, () =>
        {
            var hidePlayer = Utilities.GetPlayerFromSlot(playerSlot);

            if (hidePlayer == null || !hidePlayer.IsValid || hidePlayer.IsBot)
                return;

            hidePlayer.ExecuteClientCommandFromServer("css_hide");
        });

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        GodPlayers.Clear();
        SpeedPlayers.Clear();
        GravityPlayers.Clear();

        foreach (var player in PlayersInfo.Values)
        {
            player.DiePosition = null;
        }

        AddTimer(0.41f, () =>
        {
            foreach (var list in RenamedPlayers)
            {
                var player = Utilities.GetPlayerFromSteamId(list.Key);

                if (player == null || !player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
                    continue;

                if (player.PlayerName.Equals(list.Value))
                    continue;

                player.Rename(list.Value);
            }
        });

        return HookResult.Continue;
    }

    private HookResult HookUmChat(UserMessage um)
    {
        var author = Utilities.GetPlayerFromIndex(um.ReadInt("entityindex"));
        if (author == null || !author.IsValid || author.IsBot)
            return HookResult.Continue;

        if (!PlayerPenaltyManager.IsPenalized(author.Slot, PenaltyType.Gag, out DateTime? endDateTime) &&
            !PlayerPenaltyManager.IsPenalized(author.Slot, PenaltyType.Silence, out endDateTime))
            return HookResult.Continue;

        if (_localizer == null || endDateTime == null)
            return HookResult.Continue;

        var message = um.ReadString("param2");
        var triggers = CoreConfig.PublicChatTrigger.Concat(CoreConfig.SilentChatTrigger);
        if (!triggers.Any(trigger => message.StartsWith(trigger))) return HookResult.Stop;

        for (var i = um.Recipients.Count - 1; i >= 0; i--)
        {
            if (um.Recipients[i] != author)
            {
                um.Recipients.RemoveAt(i);
            }
        }

        return HookResult.Continue;

        // author.SendLocalizedMessage(_localizer, "sa_player_penalty_chat_active", endDateTime.Value.ToString("g", author.GetLanguage()));
    }

    private HookResult ComamndListenerHandler(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        var command = info.GetArg(0).ToLower();

        if (Config.OtherSettings.AdditionalCommandsToLog.Contains(command))
            Helper.LogCommand(player, info);

        switch (command)
        {
            case "css_admins_reload":
                AddTimer(1.0f, () => ReloadAdmins(null));
                return HookResult.Continue;
            case "callvote":
                {
                    var voteType = info.GetArg(1).ToLower();

                    if (voteType != "kick")
                        return HookResult.Continue;

                    var target = int.TryParse(info.GetArg(2), out var userId)
                        ? Utilities.GetPlayerFromUserid(userId)
                        : null;

                    if (target == null || !target.IsValid || target.Connected != PlayerConnectedState.PlayerConnected)
                        return HookResult.Continue;

                    return !player.CanTarget(target) ? HookResult.Stop : HookResult.Continue;
                }
        }

        if (!command.Contains("say"))
            return HookResult.Continue;

        if (info.GetArg(1).Length == 0)
            return HookResult.Stop;

        var triggers = CoreConfig.PublicChatTrigger.Concat(CoreConfig.SilentChatTrigger);
        if (triggers.Any(trigger => info.GetArg(1).StartsWith(trigger)))
        {
            return HookResult.Continue;
        }

        // if (!Config.OtherSettings.UserMessageGagChatType)
        // {
        if (PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag, out DateTime? endDateTime) ||
            PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence, out endDateTime))
        {
            if (_localizer != null && endDateTime is not null)
                player.SendLocalizedMessage(_localizer, "sa_player_penalty_chat_active", endDateTime.Value.ToString("g", player.GetLanguage()));
            return HookResult.Stop;
        }

        if (string.IsNullOrWhiteSpace(info.GetArg(1))
            || info.GetArg(1).StartsWith($"/")
            || info.GetArg(1).StartsWith($"!"))
            return HookResult.Continue;

        if (info.GetArg(1).Length == 0)
            return HookResult.Stop;

        if (command == "say" && info.GetArg(1).StartsWith($"@") &&
            AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), "@css/chat"))
            // }

            if (AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), "@css/chat") && command == "say" && info.GetArg(1).StartsWith($"@"))
            {
                player.ExecuteClientCommandFromServer($"css_say {info.GetArg(1).Remove(0, 1)}");
                return HookResult.Stop;
            }

        if ((command == "say" || command == "say_team") && Config.IsCSSPanel)
        {
            var message = info.GetArg(1);

            string trimmedMessage1 = message.TrimStart();
            string trimmedMessage = trimmedMessage1.TrimEnd();

            //Console.WriteLine($"{IsStringValid(trimmedMessage)} | {Config.ChatLog.ExcludeMessageContainsLessThanXLetters} | {CountLetters(trimmedMessage)}");

            if (IsStringValid(trimmedMessage) && (Config.ChatLog.ExcludeMessageContainsLessThanXLetters <= 0 || CountLetters(trimmedMessage) > Config.ChatLog.ExcludeMessageContainsLessThanXLetters))
            {
                var vplayername = player.PlayerName;
                var steamId64 = (player.AuthorizedSteamID != null) ? player.AuthorizedSteamID.SteamId64.ToString() : "InvalidSteamID";

                secondMessage = firstMessage;
                firstMessage = trimmedMessage;

                if (!Config.ChatLog.ExcludeMessageDuplicate || secondMessage != firstMessage)
                {
                    // Add to db
                    ChatManager.AddChatMessageDB(
                        steamId64,
                        vplayername,
                        trimmedMessage,
                        command.Contains("say_team")
                    );
                }
            }
        }

        if (command != "say_team" || !info.GetArg(1).StartsWith($"@")) return HookResult.Continue;

        StringBuilder sb = new();
        if (AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), "@css/chat"))
        {
            sb.Append(_localizer!["sa_adminchat_template_admin", player.PlayerName, info.GetArg(1).Remove(0, 1)]);
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p is { IsBot: false, IsHLTV: false } && AdminManager.PlayerHasPermissions(new SteamID(p.SteamID), "@css/chat")))
            {
                p.PrintToChat(sb.ToString());
            }
        }
        else
        {
            sb.Append(_localizer!["sa_adminchat_template_player", player.PlayerName, info.GetArg(1).Remove(0, 1)]);
            player.PrintToChat(sb.ToString());
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false } && AdminManager.PlayerHasPermissions(new SteamID(p.SteamID), "@css/chat")))
            {
                p.PrintToChat(sb.ToString());
            }
        }

        return HookResult.Stop;
    }

    /*public HookResult OnCommandSay(CCSPlayerController? player, CommandInfo info)
	{
		if (player == null ||  !player.IsValid || player.IsBot)
			return HookResult.Continue;

		if (info.GetArg(1).StartsWith($"/")
			|| info.GetArg(1).StartsWith($"!"))
			return HookResult.Continue;

		if (info.GetArg(1).Length == 0)
			return HookResult.Handled;

		if (PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag) || PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence))
			return HookResult.Handled;

		return HookResult.Continue;
	}*/

    public HookResult OnCommandTeamSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        if (info.GetArg(1).StartsWith($"/")
            || info.GetArg(1).StartsWith($"!"))
            return HookResult.Continue;

        if (info.GetArg(1).Length == 0)
            return HookResult.Handled;

        if (PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Gag, out _) || PlayerPenaltyManager.IsPenalized(player.Slot, PenaltyType.Silence, out _))
            return HookResult.Stop;

        if (!info.GetArg(1).StartsWith($"@")) return HookResult.Continue;

        StringBuilder sb = new();

        if (AdminManager.PlayerHasPermissions(new SteamID(player.SteamID), "@css/chat"))
        {
            sb.Append(_localizer!["sa_adminchat_template_admin", player.PlayerName, info.GetArg(1).Remove(0, 1)]);
            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p is { IsBot: false, IsHLTV: false } && AdminManager.PlayerHasPermissions(new SteamID(p.SteamID), "@css/chat")))
            {
                p.PrintToChat(sb.ToString());
            }
        }
        else
        {
            sb.Append(_localizer!["sa_adminchat_template_player", player.PlayerName, info.GetArg(1).Remove(0, 1)]);
            player.PrintToChat(sb.ToString());
            foreach (var p in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false } && AdminManager.PlayerHasPermissions(new SteamID(p.SteamID), "@css/chat")))
            {
                p.PrintToChat(sb.ToString());
            }
        }

        return HookResult.Handled;
    }

    private void OnMapStart(string mapName)
    {
        if (!ServerLoaded || ServerId == null)
            AddTimer(2.0f, OnGameServerSteamAPIActivated);

        if (Config.OtherSettings.ReloadAdminsEveryMapChange && ServerLoaded && ServerId != null)
            AddTimer(5.0f, () => ReloadAdmins(null));

        AddTimer(1.0f, ServerManager.CheckHibernationStatus);

        // AddTimer(34, () =>
        // {
        //     if (!ServerLoaded)
        //         OnGameServerSteamAPIActivated();
        // });

        GodPlayers.Clear();
        SilentPlayers.Clear();
        SpeedPlayers.Clear();
        GravityPlayers.Clear();

        PlayerPenaltyManager.RemoveAllPenalties();
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player is null || @event.Attacker is null || player.PlayerPawn?.Value?.LifeState != (int)LifeState_t.LIFE_ALIVE || player.PlayerPawn.Value == null)
            return HookResult.Continue;

        if (!GodPlayers.Contains(player.Slot)) return HookResult.Continue;

        player.PlayerPawn.Value.Health = player.PlayerPawn.Value.MaxHealth;
        player.PlayerPawn.Value.ArmorValue = 100;

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player?.UserId == null || !player.IsValid || player.IsHLTV ||
            player.Connected != PlayerConnectedState.PlayerConnected || !PlayersInfo.ContainsKey(player.SteamID) ||
            @event.Attacker == null)
            return HookResult.Continue;

        SpeedPlayers.Remove(player.Slot);
        GravityPlayers.Remove(player.Slot);

        PlayersInfo[player.SteamID].DiePosition = new DiePosition(
            (Vector3)player.PlayerPawn.Value?.AbsOrigin!,
            (Vector3)player.PlayerPawn.Value?.AbsRotation!
        );

        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || !SilentPlayers.Contains(player.Slot))
            return HookResult.Continue;

        if (@event is not { Oldteam: <= 1, Team: >= 1 }) return HookResult.Continue;

        SilentPlayers.Remove(player.Slot);
        SimpleAdminApi?.OnAdminToggleSilentEvent(player.Slot, false);

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerInfo(EventPlayerInfo @event, GameEventInfo _)
    {
        var player = @event.Userid;

        if (player is null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        if (!RenamedPlayers.TryGetValue(player.SteamID, out var name)) return HookResult.Continue;

        if (player.PlayerName.Equals(name))
            return HookResult.Continue;

        player.Rename(name);

        return HookResult.Continue;
    }
}






