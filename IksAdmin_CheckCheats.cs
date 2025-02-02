using System.Globalization;
using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Dapper;
using IksAdminApi;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace IksAdminCheckCheatsPlugin;

[MinimumApiVersion(80)]
public class IksAdminCheckCheatsPlugin : AdminModule, IPluginConfig<IksAdminCheckCheatsConfig>
{
    public override string ModuleName => "[IksAdmin] Check Cheats";
    public override string ModuleAuthor => "ABKAM | Forked by iks__ & Alley";
    public override string ModuleVersion => "1.2.2";

    private static readonly HttpClient HttpClient = new();
    private readonly Dictionary<ulong, AdminCheckInfo> _adminCheckMessages = new();
    private readonly Dictionary<ulong, CParticleSystem> _activeOverlays = new();
    private readonly HashSet<ulong> _contactProvided = new();
    private readonly HashSet<ulong> _hiddenMessagesForAdmins = new();
    private readonly Dictionary<ulong, int> _messageDisplayTimes = new();
    private readonly HashSet<ulong> _playersUnderCheck = new();
    private readonly Dictionary<ulong, Timer> _playerTimers = new();
    private readonly HashSet<ulong> _processedPlayers = new();
    private readonly Dictionary<ulong, int> _remainingTimes = new();
    private readonly Dictionary<ulong, int> _uncheckMessages = new();
    private readonly HashSet<ulong> _playersChangingTeam = new();
    private readonly Dictionary<ulong, (string DiscordContact, ulong AdminSteamId)> _webhookInfo = new();

    private MySqlConnection? _dbConnection;
    private bool _isMapChanging;
    private string? _chatPrefix;
    private DateTime _lastMapChangeTime = DateTime.MinValue;
    public IksAdminCheckCheatsConfig Config { get; set; }

    public void OnConfigParsed(IksAdminCheckCheatsConfig config)
    {
        Config = config;
        if (Config.WebhookMode < 1 || Config.WebhookMode > 2) Config.WebhookMode = 1;
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        base.OnAllPluginsLoaded(hotReload);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterListener<Listeners.OnTick>(OnTick);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.CheckTransmit>(CheckTransmit);

        AddCommand("css_contact", Localizer["command_contact_description"], PlayerContactCommand);
        AddCommand("css_close", Localizer["command_close_description"], CloseMessageCommand);

        _chatPrefix = Localizer["chat_prefix"];

        InitializeDatabase();
    }

    public override void Ready()
    {
        base.Ready();
        Api.RegisterPermission("check_cheats.flag", "b");
        Api.MenuOpenPre += OnMenuOpenPre;
    }
    public override void Unload(bool hotReload)
    {
        base.Unload(hotReload);
        Api.MenuOpenPre -= OnMenuOpenPre;
    }

    private HookResult OnMenuOpenPre(CCSPlayerController player, IDynamicMenu menu, IMenu gameMenu)
    {
        if (menu.Id != "iksadmin:menu:main") return HookResult.Continue;

        menu.AddMenuOption("select_player_for_check", Localizer["select_player_for_check"], (p, _) => {
            ShowCheckMenu(p);
        }, viewFlags: AdminUtils.GetCurrentPermissionFlags("check_cheats.flag"));
        menu.AddMenuOption("select_player_to_uncheck", Localizer["select_player_to_uncheck"], (p, _) => {
            ShowUncheckMenu(p);
        }, viewFlags: AdminUtils.GetCurrentPermissionFlags("check_cheats.flag"));

        return HookResult.Continue;
    }

    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        foreach (var playerSteamId64 in _playersUnderCheck)
        {
            var player = Utilities.GetPlayers().Find(p => p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == playerSteamId64);
            if (player != null && player.IsValid)
            {
                ApplyCheatCheckParticle(player);
            }
        }
        return HookResult.Continue;
    }

    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || player.AuthorizedSteamID == null)
            return HookResult.Continue;

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        if (_playersUnderCheck.Contains(playerSteamId64))
        {
            var hasProvidedContact = _webhookInfo.TryGetValue(playerSteamId64, out var webhookData) &&
                                     !string.IsNullOrEmpty(webhookData.DiscordContact);

            if (!hasProvidedContact && _remainingTimes.TryGetValue(playerSteamId64, out var remainingTime) &&
                remainingTime > 0) StartCheckTimer(player, player, remainingTime);
        }

        return HookResult.Continue;
    }

    public void OnMapStart(string mapName)
    {
        _isMapChanging = true;
        _lastMapChangeTime = DateTime.UtcNow;


        foreach (var timer in _playerTimers.Values)
        {
            timer.Kill();
        }
        _playerTimers.Clear();
        _activeOverlays.Clear();
        _playersUnderCheck.Clear();
        _processedPlayers.Clear();
        _contactProvided.Clear();
        _hiddenMessagesForAdmins.Clear();
        _messageDisplayTimes.Clear();
        _uncheckMessages.Clear();

        RegisterListener<Listeners.OnServerPrecacheResources>(PreCacheResources);
    }

    private void PreCacheResources(ResourceManifest manifest)
    {
        manifest.AddResource(Config.OverlayPath);
    }

    public void OnMapEnd()
    {
        _isMapChanging = false;


        foreach (var timer in _playerTimers.Values)
        {
            timer.Kill();
        }
        _playerTimers.Clear();
        _activeOverlays.Clear();
        _playersUnderCheck.Clear();
        _processedPlayers.Clear();
        _contactProvided.Clear();
        _hiddenMessagesForAdmins.Clear();
        _messageDisplayTimes.Clear();
        _uncheckMessages.Clear();
    }

    private void PlayerContactCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null) return;

        if (info.ArgCount < 2)
        {
            string errorMessage = Localizer["error_message"];
            player.PrintToChat(_chatPrefix + errorMessage);
            return;
        }

        var discordContact = info.GetArg(1);
        if (player.AuthorizedSteamID == null)
        {
            string errorMessage = Localizer["error_player_no_steamid", player.PlayerName];
            player.PrintToChat(_chatPrefix + errorMessage);
            return;
        }

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        if (_remainingTimes.ContainsKey(playerSteamId64))
        {
            if (_adminCheckMessages.TryGetValue(playerSteamId64, out var adminInfo))
            {
                _webhookInfo[playerSteamId64] = (discordContact, adminInfo.AdminSteamId);

                var admin = Utilities.GetPlayers().Find(p =>
                    p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == adminInfo.AdminSteamId);

                if (admin != null)
                {
                    string message = Localizer["admin_message_format", player.PlayerName, discordContact];
                    admin.PrintToChat(_chatPrefix + message);

                    if (Config.EnableDiscordLogging && Config.WebhookMode == 1 &&
                        !string.IsNullOrEmpty(Config.DiscordWebhookUrl))
                        _ = SendDiscordContactProvidedNotification(
                            Config.DiscordWebhookUrl,
                            player.PlayerName,
                            admin.PlayerName,
                            playerSteamId64.ToString(),
                            adminInfo.AdminSteamId.ToString(),
                            discordContact
                        );
                }
            }

            _adminCheckMessages.Remove(playerSteamId64);
            StopCheckTimer(playerSteamId64);

            player.PrintToChat(_chatPrefix + Localizer["contact_success_message"]);
        }
        else
        {
            player.PrintToChat(_chatPrefix + Localizer["error_no_active_check"]);
        }
    }

    private void CloseMessageCommand(CCSPlayerController? admin, CommandInfo info)
    {
        if (admin == null || admin.AuthorizedSteamID == null) return;

        var adminSteamId64 = admin.AuthorizedSteamID.SteamId64;
        var hasActiveChecks = _adminCheckMessages.Values.Any(msg => msg.AdminSteamId == adminSteamId64);

        if (hasActiveChecks)
        {
            _hiddenMessagesForAdmins.Add(adminSteamId64);
            admin.PrintToChat(_chatPrefix + Localizer["message_closed"]);
        }
    }

    private void ShowCheckMenu(CCSPlayerController admin)
    {
        var checkMenu = Api.CreateMenu("check_cheats_check", Localizer["select_player_for_check"]);

        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.AuthorizedSteamID == null) continue;

            var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

            if (_playersUnderCheck.Contains(playerSteamId64) ||
                _remainingTimes.ContainsKey(playerSteamId64) ||
                _contactProvided.Contains(playerSteamId64))
            {
                checkMenu.AddMenuOption(player.GetSteamId(), $"{player.PlayerName} {Localizer["status_check"]}", (_, _) => {}, disabled: true);
                continue;
            }

            checkMenu.AddMenuOption(player.GetSteamId(), player.PlayerName, (caller, option) =>
            {
                var checkDuration = Config.CheckDuration;

                if (_playersUnderCheck.Contains(playerSteamId64) ||
                    _remainingTimes.ContainsKey(playerSteamId64) ||
                    _contactProvided.Contains(playerSteamId64))
                {
                    caller.PrintToChat(_chatPrefix + Localizer["error_already_under_check", player.PlayerName]);
                    return;
                }

                StartCheck(player, admin);
                StartCheckTimer(player, admin, checkDuration);
                Api.CloseMenu(admin);
                caller.PrintToChat(_chatPrefix + Localizer["check", player.PlayerName]);
            });
        }

        checkMenu.Open(admin);
    }

    private void ShowUncheckMenu(CCSPlayerController admin)
    {
        var uncheckMenu = Api.CreateMenu("check_cheats_check", Localizer["select_player_to_uncheck"]);

        foreach (var player in Utilities.GetPlayers())
        {
            if (player == null || !player.IsValid || player.AuthorizedSteamID == null) continue;

            uncheckMenu.AddMenuOption(player.GetSteamId(), player.PlayerName, (caller, option) =>
            {
                UncheckPlayer(player, admin);
                Api.CloseMenu(player);
            });
        }

        uncheckMenu.Open(admin);
    }

    private void UncheckPlayer(CCSPlayerController player, CCSPlayerController admin)
    {
        if (player == null || !player.IsValid || player.AuthorizedSteamID == null)
        {
            admin.PrintToChat(_chatPrefix + "Игрок недоступен или данные некорректны.");
            return;
        }

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        if (!_remainingTimes.ContainsKey(playerSteamId64) && !_playersUnderCheck.Contains(playerSteamId64))
        {
            admin.PrintToChat(_chatPrefix + Localizer["error_no_active_check_for_admin", player.PlayerName]);
            return;
        }

        try
        {
            RemoveOverlay(playerSteamId64);
            StopCheckTimer(playerSteamId64);
            CleanupAfterProcessing(playerSteamId64);

            admin.PrintToChat(_chatPrefix + Localizer["uncheck", player.PlayerName]);

            var discordContact = _webhookInfo.TryGetValue(playerSteamId64, out var webhookData) &&
                                 !string.IsNullOrEmpty(webhookData.DiscordContact)
                ? webhookData.DiscordContact
                : Localizer["discord_contact_not_provided"].Value;

            if (Config.EnableDiscordLogging && !string.IsNullOrEmpty(Config.DiscordWebhookUrl))
            {
                if (Config.WebhookMode == 2)
                    _ = SendConsolidatedDiscordNotification(
                        Config.DiscordWebhookUrl,
                        player.PlayerName,
                        admin.PlayerName,
                        playerSteamId64.ToString(),
                        admin.AuthorizedSteamID?.SteamId64.ToString() ?? "Unknown",
                        discordContact,
                        Localizer["check_result_completed"].Value
                    );
                else
                    _ = SendDiscordCheckCompletedNotification(
                        Config.DiscordWebhookUrl,
                        player.PlayerName,
                        admin.PlayerName,
                        playerSteamId64.ToString(),
                        admin.AuthorizedSteamID?.SteamId64.ToString() ?? "Unknown"
                    );
            }

            _ = LogCheckEndToDatabase(playerSteamId64.ToString(), "db_check_result_completed", discordContact);
            _uncheckMessages[playerSteamId64] = 100;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Ошибка при снятии проверки с игрока {player.PlayerName}: {ex.Message}");
            admin.PrintToChat(_chatPrefix + "Произошла ошибка при выполнении операции.");
        }
    }


    private void StartCheckTimer(CCSPlayerController playerToCheck, CCSPlayerController admin, int remainingTime)
    {
        if (playerToCheck == null || !playerToCheck.IsValid || admin == null || !admin.IsValid)
        {
            Logger.LogError("Player or admin is invalid in StartCheckTimer.");
            return;
        }

        var playerSteamId64 = playerToCheck.AuthorizedSteamID?.SteamId64 ?? 0;
        var adminSteamId64 = admin.AuthorizedSteamID?.SteamId64 ?? 0;

        if (playerSteamId64 == 0 || adminSteamId64 == 0)
        {
            Logger.LogError("Invalid SteamID64 for player or admin.");
            return;
        }

        Logger.LogInformation($"Starting check timer for player {playerToCheck.PlayerName} (SteamID64: {playerSteamId64}) with {remainingTime} seconds.");

        _remainingTimes[playerSteamId64] = remainingTime;

        _adminCheckMessages[playerSteamId64] = new AdminCheckInfo(
            playerToCheck.PlayerName,
            remainingTime,
            adminSteamId64,
            Localizer["discord_contact_not_provided"].Value
        );

        if (_playerTimers.TryGetValue(playerSteamId64, out var existingTimer))
        {
            existingTimer.Kill();
            _playerTimers.Remove(playerSteamId64);
        }

        _playerTimers[playerSteamId64] = new Timer(1.0f, () =>
        {
            try
            {
                Logger.LogInformation($"Timer tick for player {playerSteamId64}. Remaining time: {_remainingTimes[playerSteamId64]}");

                if (!_isMapChanging)
                {
                    var currentPlayer = Utilities.GetPlayers().FirstOrDefault(p =>
                        p.AuthorizedSteamID?.SteamId64 == playerSteamId64 && p.IsValid);

                    if (currentPlayer == null || !currentPlayer.IsValid)
                    {
                        Logger.LogInformation($"Player {playerSteamId64} is no longer valid. Stopping timer.");
                        StopCheckTimer(playerSteamId64);
                        return;
                    }
                }

                _remainingTimes[playerSteamId64]--;

                if (_remainingTimes[playerSteamId64] <= 0)
                {
                    Logger.LogInformation($"Timer expired for player {playerSteamId64}. Checking Discord contact...");


                    if (!_webhookInfo.TryGetValue(playerSteamId64, out var webhookData) || string.IsNullOrEmpty(webhookData.DiscordContact))
                    {
                        Logger.LogInformation($"Player {playerSteamId64} did not provide Discord contact. Banning...");
                        var steamId = admin.GetSteamId();
                        Task.Run(async () => {
                            await BanPlayerOffline(playerSteamId64, steamId);
                        });
                    }
                    else
                    {
                        Logger.LogInformation($"Player {playerSteamId64} provided Discord contact. Unchecking...");
                        UncheckPlayer(playerToCheck, admin);
                    }

                    StopCheckTimer(playerSteamId64);
                }
                else
                {
                    _adminCheckMessages[playerSteamId64].TimeLeft = _remainingTimes[playerSteamId64];
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error in timer for player {playerSteamId64}: {ex.Message}");
                StopCheckTimer(playerSteamId64);
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void StopCheckTimer(ulong playerSteamId64, bool removeFromRemainingTimes = false)
    {
        if (_playerTimers.ContainsKey(playerSteamId64))
        {
            Logger.LogInformation($"Stopping timer for player {playerSteamId64}.");
            _playerTimers[playerSteamId64].Kill();
            _playerTimers.Remove(playerSteamId64);
        }

        if (removeFromRemainingTimes) _remainingTimes.Remove(playerSteamId64);
    }

    private void OnTick()
    {
        foreach (var kvp in _adminCheckMessages)
        {
            var playerInfo = kvp.Value;
            var playerName = playerInfo.PlayerName;
            var timeLeft = playerInfo.TimeLeft;
            var adminSteamId64 = playerInfo.AdminSteamId;

            if (adminSteamId64 == 0UL || _hiddenMessagesForAdmins.Contains(adminSteamId64)) continue;

            var admin = Utilities.GetPlayers().Find(p =>
                p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == adminSteamId64);
            if (admin != null && timeLeft > 0)
                admin.PrintToCenterHtml(Localizer["html_admin_check_info_message", playerName, timeLeft]);
        }

        foreach (var kvp in new Dictionary<ulong, int>(_uncheckMessages))
        {
            var playerSteamId64 = kvp.Key;
            var displayTime = kvp.Value;

            if (displayTime > 0)
            {
                var player = Utilities.GetPlayers().Find(p =>
                    p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == playerSteamId64);
                if (player != null) player.PrintToCenterHtml(Localizer["html_success_message"]);
                _uncheckMessages[playerSteamId64]--;
            }
            else
            {
                _uncheckMessages.Remove(playerSteamId64);
            }
        }

        if (Config.ShowHtmlMessageSuspect)
        {
            foreach (var kvp in _remainingTimes)
            {
                var playerSteamId64 = kvp.Key;
                var timeLeft = kvp.Value;

                if (timeLeft > 0)
                {
                    string updatedMessage = Localizer["html_countdown_message_format", timeLeft];
                    var player = Utilities.GetPlayers().Find(p =>
                        p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == playerSteamId64);
                    if (player != null) player.PrintToCenterHtml(updatedMessage);
                }
            }
        }

        foreach (var kvp in new Dictionary<ulong, int>(_messageDisplayTimes))
        {
            var playerSteamId64 = kvp.Key;
            var displayTime = kvp.Value;

            if (displayTime > 0)
            {
                var player = Utilities.GetPlayers().Find(p =>
                    p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == playerSteamId64);
                if (player != null) player.PrintToCenterHtml(Localizer["html_success_message"]);
                _messageDisplayTimes[playerSteamId64]--;
            }
            else
            {
                _messageDisplayTimes.Remove(playerSteamId64);
            }
        }
    }

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || player.AuthorizedSteamID == null) return HookResult.Continue;

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        var timeSinceMapChange = DateTime.UtcNow - _lastMapChangeTime;
        var recentlyMapChanged = _isMapChanging && timeSinceMapChange.TotalSeconds <= 5;

        if (recentlyMapChanged) return HookResult.Continue;

        if (_playersUnderCheck.Contains(playerSteamId64))
        {
            if (_adminCheckMessages.TryGetValue(playerSteamId64, out var adminCheckInfo))
            {
                var admin = Utilities.GetPlayers().Find(p =>
                    p.AuthorizedSteamID != null && p.AuthorizedSteamID.SteamId64 == adminCheckInfo.AdminSteamId);

                if (admin != null && admin.IsValid)
                {
                    ScheduleBanForDisconnectedPlayer(playerSteamId64, admin);
                }
                else
                {
                    Logger.LogWarning($"No valid admin found to ban player {player.PlayerName} (SteamID64: {playerSteamId64}).");
                }
            }
            else
            {
                Logger.LogWarning($"No admin check info found for player {player.PlayerName} (SteamID64: {playerSteamId64}).");
            }
        }

        return HookResult.Continue;
    }

    private void ScheduleBanForDisconnectedPlayer(ulong playerSteamId64, CCSPlayerController admin)
    {
        StopCheckTimer(playerSteamId64);
        new Timer(1.0f, () =>
        {
            if (_isMapChanging && (DateTime.UtcNow - _lastMapChangeTime).TotalSeconds <= 5) return;

            if (!_processedPlayers.Contains(playerSteamId64))
            {
                var steamId = admin.GetSteamId();
                Task.Run(async () => {
                    await BanPlayerOffline(playerSteamId64, steamId);
                });
            }
        });
    }

    private async Task BanPlayerOffline(ulong playerSteamId64, string adminSteamId64)
    {
        try
        {
            if (!_webhookInfo.TryGetValue(playerSteamId64, out var webhookData))
            {
                Logger.LogWarning($"Player {playerSteamId64} has no recorded webhook data for ban logging.");
                webhookData = (DiscordContact: Localizer["discord_contact_not_provided"].Value, AdminSteamId: 0);
            }

            await LogCheckEndToDatabase(
                playerSteamId64.ToString(),
                Localizer["db_check_result_player_left_and_banned"],
                webhookData.DiscordContact
            );

            CleanupAfterProcessing(playerSteamId64);

  

            Server.NextFrame(() =>
            {
                var admin = PlayersUtils.GetControllerBySteamId(adminSteamId64);
                if (admin == null || !admin.IsValid)
                {
                    Logger.LogError($"Admin context is invalid for banning player {playerSteamId64}.");
                    return;
                }
                var player = Utilities.GetPlayers().FirstOrDefault(p =>
                    p.AuthorizedSteamID?.SteamId64 == playerSteamId64 && p.IsValid);

                Logger.LogInformation($"Banning player {playerSteamId64}.");
                admin.ExecuteClientCommandFromServer($"css_addban {playerSteamId64} {Config.BanTime} \"{Config.BanReason}\"");
            });
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error in BanPlayerOffline: {ex.ToString()}");
        }
    }

    private void CleanupAfterProcessing(ulong playerSteamId64)
    {
        Logger.LogInformation($"Cleaning up data for player {playerSteamId64}.");
        _webhookInfo.Remove(playerSteamId64);
        _adminCheckMessages.Remove(playerSteamId64);
        _playersUnderCheck.Remove(playerSteamId64);
        _processedPlayers.Remove(playerSteamId64);
        _contactProvided.Remove(playerSteamId64);
        _hiddenMessagesForAdmins.Remove(playerSteamId64);
        _messageDisplayTimes.Remove(playerSteamId64);
        _uncheckMessages.Remove(playerSteamId64);
        StopCheckTimer(playerSteamId64, true);
        RemoveOverlay(playerSteamId64);
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerTeamPre(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (player == null || player.AuthorizedSteamID == null || !player.IsValid)
            return HookResult.Continue;

        var playerSteamId64 = player.AuthorizedSteamID.SteamId64;

        if (_playersUnderCheck.Contains(playerSteamId64) && Config.BlockTeamChangeDuringCheck)
        {
            Server.NextFrame(() =>
            {
                if (player.IsValid)
                {
                    player.ChangeTeam(CsTeam.Spectator);
                    player.PrintToChat(_chatPrefix + Localizer["check_team_lock_message"]);
                }
            });
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }

    private void StartCheck(CCSPlayerController playerToCheck, CCSPlayerController admin)
    {
        if (playerToCheck == null || !playerToCheck.IsValid || admin == null || !admin.IsValid)
        {
            Logger.LogError("Player or admin is invalid in StartCheck.");
            return;
        }

        var playerSteamId64 = playerToCheck.AuthorizedSteamID!.SteamId64;
        var adminSteamId64 = admin.AuthorizedSteamID!.SteamId64;

        if (_playersUnderCheck.Contains(playerSteamId64))
        {
            admin.PrintToChat(_chatPrefix + Localizer["error_already_under_check", playerToCheck.PlayerName]);
            return;
        }

        Logger.LogInformation($"Starting check for player {playerToCheck.PlayerName} (SteamID64: {playerSteamId64}).");

        _playersUnderCheck.Add(playerSteamId64);

        var suspectDiscord = _webhookInfo.TryGetValue(playerSteamId64, out var webhookData)
            ? webhookData.DiscordContact
            : null;

        _ = LogCheckStartToDatabase(Config.ServerId, playerSteamId64.ToString(), playerToCheck.PlayerName,
            adminSteamId64.ToString(), admin.PlayerName, suspectDiscord);

        if (Config.MoveToSpectatorsOnCheck && playerToCheck.Team != CsTeam.Spectator)
        {
            if (!_playersChangingTeam.Contains(playerSteamId64))
            {
                try
                {
                    _playersChangingTeam.Add(playerSteamId64);
                    playerToCheck.ChangeTeam(CsTeam.Spectator);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error while changing team for player {playerToCheck.PlayerName}: {ex.Message}");
                }
                finally
                {
                    _playersChangingTeam.Remove(playerSteamId64);
                }
            }
            else
            {
                Logger.LogWarning($"Attempted to change team for player {playerToCheck.PlayerName} while already processing.");
            }
        }

        playerToCheck.PrintToCenterHtml(_chatPrefix + Localizer["check_start_message"]);

        if (Config.Overlay)
        {
            ApplyCheatCheckParticle(playerToCheck);
        }

        if (Config.EnableDiscordLogging && !string.IsNullOrEmpty(Config.DiscordWebhookUrl))
        {
            _ = SendDiscordCheckStartedNotification(
                Config.DiscordWebhookUrl,
                playerToCheck.PlayerName,
                admin.PlayerName,
                playerSteamId64.ToString(),
                adminSteamId64.ToString()
            );
        }
    }

    private void CheckTransmit(CCheckTransmitInfoList infoList)
    {
        foreach (var (info, player) in infoList)
        {
            if (player == null || player.AuthorizedSteamID == null) continue;

            foreach (var (OverlaySteamID, particleSystem) in _activeOverlays)
            {
                var OverlayOwner = Utilities.GetPlayerFromSteamId(OverlaySteamID);
                if (OverlayOwner == null || !particleSystem.IsValid) continue;

                uint OverlayEntityIndex = particleSystem.Index;
                if (OverlayEntityIndex == Utilities.InvalidEHandleIndex) continue;

                if (player.AuthorizedSteamID.SteamId64 != OverlaySteamID)
                {
                    info.TransmitEntities.Remove((int)OverlayEntityIndex);
                }
            }
        }
    }

    private void ApplyCheatCheckParticle(CCSPlayerController player)
    {
        var particleSystem = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particleSystem == null || !particleSystem.IsValid) return;

        particleSystem.EffectName = Config.OverlayPath;
        var origin = player.PlayerPawn?.Value?.AbsOrigin ?? new Vector();
        particleSystem.Teleport(origin, new QAngle(), new Vector());
        particleSystem.DispatchSpawn();
        particleSystem.AcceptInput("FollowEntity", player.PlayerPawn?.Value, player.PlayerPawn?.Value, "!activator", 0);
        particleSystem.AcceptInput("Start");

        _activeOverlays[player.AuthorizedSteamID!.SteamId64] = particleSystem;
    }

    private void RemoveOverlay(ulong playerSteamId64)
    {
        if (_activeOverlays.TryGetValue(playerSteamId64, out var particleSystem) && particleSystem.IsValid)
        {
            particleSystem.Active = false;
            particleSystem.DispatchSpawn();
            _activeOverlays.Remove(playerSteamId64);
        }
    }

    private async Task SendConsolidatedDiscordNotification(string webhookUrl, string playerName, string adminName,
        string playerSteamId64, string adminSteamId64, string discordContact, string checkResult)
    {
        var embed = new
        {
            title = Localizer["discord_consolidated_check_title"].Value,
            description = Localizer["discord_consolidated_check_description"].Value,
            color = int.Parse(Config.DiscordColorCheckCompleted.Replace("0x", ""), NumberStyles.HexNumber),
            fields = new[]
            {
                new
                {
                    name = Localizer["discord_check_started_admin_field"].Value,
                    value = $"[{adminName}](https://steamcommunity.com/profiles/{adminSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_check_started_player_field"].Value,
                    value = $"[{playerName}](https://steamcommunity.com/profiles/{playerSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_contact_field"].Value,
                    value = discordContact ?? Localizer["discord_contact_not_provided"].Value,
                    inline = true
                },
                new
                {
                    name = Localizer["discord_check_result_field"].Value,
                    value = checkResult,
                    inline = true
                }
            },
            footer = new
            {
                text = Localizer["discord_system_footer_text"].Value,
                icon_url = Config.DiscordFooterIconUrl
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var payload = new { embeds = new[] { embed } };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await HttpClient.PostAsync(webhookUrl, content);
    }

    private async Task SendDiscordCheckCompletedNotification(string webhookUrl, string playerName, string adminName,
        string playerSteamId64, string adminSteamId64)
    {
        var embed = new
        {
            title = Localizer["discord_check_completed_title"].Value,
            description = Localizer["discord_check_completed_description"].Value,
            color = int.Parse(Config.DiscordColorCheckCompleted.Replace("0x", ""),
                NumberStyles.HexNumber),
            fields = new[]
            {
                new
                {
                    name = Localizer["discord_check_started_player_field"].Value,
                    value = $"[{playerName}](https://steamcommunity.com/profiles/{playerSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_check_started_admin_field"].Value,
                    value = $"[{adminName}](https://steamcommunity.com/profiles/{adminSteamId64})",
                    inline = true
                }
            },
            footer = new
            {
                text = Localizer["discord_system_footer_text"].Value,
                icon_url = Config.DiscordFooterIconUrl
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var payload = new { embeds = new[] { embed } };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await HttpClient.PostAsync(webhookUrl, content);
    }

    private async Task SendDiscordContactProvidedNotification(string webhookUrl, string playerName, string adminName,
        string playerSteamId64, string adminSteamId64, string discordContact)
    {
        var embed = new
        {
            title = Localizer["discord_contact_provided_title"].Value,
            description = Localizer["discord_contact_provided_description"].Value,
            color = int.Parse(Config.DiscordColorContactProvided.Replace("0x", ""),
                NumberStyles.HexNumber),
            fields = new[]
            {
                new
                {
                    name = Localizer["discord_check_started_player_field"].Value,
                    value = $"[{playerName}](https://steamcommunity.com/profiles/{playerSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_check_started_admin_field"].Value,
                    value = $"[{adminName}](https://steamcommunity.com/profiles/{adminSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_contact_field"].Value,
                    value = discordContact,
                    inline = true
                }
            },
            footer = new
            {
                text = Localizer["discord_system_footer_text"].Value,
                icon_url = Config.DiscordFooterIconUrl
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var payload = new { embeds = new[] { embed } };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await HttpClient.PostAsync(webhookUrl, content);
    }

    private async Task SendDiscordCheckStartedNotification(string webhookUrl, string playerName, string adminName,
        string playerSteamId64, string adminSteamId64)
    {
        var embed = new
        {
            title = Localizer["discord_check_started_title"].Value,
            description = Localizer["discord_check_started_description"].Value,
            color = int.Parse(Config.DiscordColorCheckStarted.Replace("0x", ""),
                NumberStyles.HexNumber),
            fields = new[]
            {
                new
                {
                    name = Localizer["discord_check_started_player_field"].Value,
                    value = $"[{playerName}](https://steamcommunity.com/profiles/{playerSteamId64})",
                    inline = true
                },
                new
                {
                    name = Localizer["discord_check_started_admin_field"].Value,
                    value = $"[{adminName}](https://steamcommunity.com/profiles/{adminSteamId64})",
                    inline = true
                }
            },
            footer = new
            {
                text = Localizer["discord_system_footer_text"].Value,
                icon_url = Config.DiscordFooterIconUrl
            },
            timestamp = DateTime.UtcNow.ToString("o")
        };

        var payload = new { embeds = new[] { embed } };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        await HttpClient.PostAsync(webhookUrl, content);
    }

    private void InitializeDatabase()
    {
        if (!Config.EnableDatabaseLogging) return;

        var connectionString =
            $"Server={Config.DatabaseHost};Port={Config.DatabasePort};Database={Config.DatabaseName};User={Config.DatabaseUser};Password={Config.DatabasePassword};";
        _dbConnection = new MySqlConnection(connectionString);

        try
        {
            _dbConnection.Open();

            var createTableQuery = $@"
                CREATE TABLE IF NOT EXISTS `{Config.TableName}` (
    `id` INT AUTO_INCREMENT PRIMARY KEY,
    `server_id` INT DEFAULT NULL,
    `player_steamid` VARCHAR(32) DEFAULT NULL,
    `player_name` VARCHAR(64) DEFAULT NULL,
    `admin_steamid` VARCHAR(32) DEFAULT NULL,
    `admin_name` VARCHAR(64) DEFAULT NULL,
    `datestart` INT UNSIGNED DEFAULT NULL,
    `date_end` INT UNSIGNED DEFAULT NULL,   
    `verdict` VARCHAR(128) DEFAULT NULL,
    `suspect_discord` VARCHAR(32) DEFAULT NULL
                );";

            _dbConnection.Execute(createTableQuery);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error initializing database: {ex.Message}");
        }
    }

    private int GetUnixTimestamp(DateTime dateTime)
    {
        return (int)new DateTimeOffset(dateTime).ToUnixTimeSeconds();
    }

    private async Task LogCheckStartToDatabase(int serverId, string playerSteamId, string playerName,
        string adminSteamId, string adminName, string? suspectDiscord)
    {
        if (_dbConnection == null || !Config.EnableDatabaseLogging) return;

        var query = $@"
    INSERT INTO `{Config.TableName}` 
    (server_id, player_steamid, player_name, admin_steamid, admin_name, datestart, suspect_discord) 
    VALUES 
    (@ServerId, @PlayerSteamId, @PlayerName, @AdminSteamId, @AdminName, @Datestart, @SuspectDiscord)";

        await _dbConnection.ExecuteAsync(query, new
        {
            ServerId = serverId,
            PlayerSteamId = playerSteamId,
            PlayerName = playerName,
            AdminSteamId = adminSteamId,
            AdminName = adminName,
            Datestart = GetUnixTimestamp(DateTime.UtcNow),
            SuspectDiscord = suspectDiscord
        });
    }

    private async Task LogCheckEndToDatabase(string playerSteamId, string verdictKey, string discordContact)
    {
        if (_dbConnection == null || !Config.EnableDatabaseLogging) return;

        var query = $@"
    UPDATE `{Config.TableName}` 
    SET date_end = @DateEnd, verdict = @Verdict, suspect_discord = @SuspectDiscord 
    WHERE player_steamid = @PlayerSteamId AND date_end IS NULL";

        await _dbConnection.ExecuteAsync(query, new
        {
            PlayerSteamId = playerSteamId,
            DateEnd = GetUnixTimestamp(DateTime.UtcNow),
            Verdict = Localizer[verdictKey].Value,
            SuspectDiscord = discordContact
        });
    }
}

public class AdminCheckInfo
{
    public AdminCheckInfo(string playerName, int timeLeft, ulong adminSteamId, string discordContact)
    {
        PlayerName = playerName;
        TimeLeft = timeLeft;
        AdminSteamId = adminSteamId;
        DiscordContact = discordContact;
    }

    public string PlayerName { get; set; }
    public int TimeLeft { get; set; }
    public ulong AdminSteamId { get; set; }
    public string DiscordContact { get; set; }
}