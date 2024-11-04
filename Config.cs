using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace IksAdminCheckCheatsPlugin
{
    public class IksAdminCheckCheatsConfig : BasePluginConfig
    {
        [JsonPropertyName("ban_reason")]
        public string BanReason { get; set; } = "Cheats";

        [JsonPropertyName("ban_time")] 
        public int BanTime { get; set; } = 0;   
        
        [JsonPropertyName("check_duration")]
        public int CheckDuration { get; set; } = 120;
        
        [JsonPropertyName("flag")]
        public string Flag { get; set; } = "c"; 
        
        [JsonPropertyName("check_sound_path")]
        public string CheckSoundPath { get; set; } = "sounds/buttons/button8.vsnd_c";        
        
        [JsonPropertyName("ban_on_disconnect_after_contact")]
        public bool BanOnDisconnectAfterContact { get; set; } = true;
        
        [JsonPropertyName("move_to_spectators_on_check")]
        public bool MoveToSpectatorsOnCheck { get; set; } = true;

        [JsonPropertyName("block_team_change_during_check")]
        public bool BlockTeamChangeDuringCheck { get; set; } = true;
        
        [JsonPropertyName("enable_discord_logging")]
        public bool EnableDiscordLogging { get; set; } = true;

        [JsonPropertyName("discord_webhook_url")]
        public string DiscordWebhookUrl { get; set; } = "";
        
        [JsonPropertyName("discord_color_check_started")]
        public string DiscordColorCheckStarted { get; set; } = "FFA500"; 

        [JsonPropertyName("discord_color_contact_provided")]
        public string DiscordColorContactProvided { get; set; } = "00FF00";

        [JsonPropertyName("discord_color_check_completed")]
        public string DiscordColorCheckCompleted { get; set; } = "00FF00"; 

        [JsonPropertyName("discord_footer_icon_url")]
        public string DiscordFooterIconUrl { get; set; } = "https://i.imgur.com/2NbqQu7.png";
    }
}