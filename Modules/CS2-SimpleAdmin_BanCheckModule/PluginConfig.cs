using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace CS2_SimpleAdmin_BanCheckModule;

public class PluginConfig : IBasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("SendJoinMessage")]
    public bool SendJoinMessage { get; set; } = true;

    [JsonPropertyName("CheckIpBans")]
    public bool CheckIpBans { get; set; } = true;

    [JsonPropertyName("UseServerIdScope")]
    public bool UseServerIdScope { get; set; } = true;

    [JsonPropertyName("ResolvePlayerMaxAttempts")]
    public int ResolvePlayerMaxAttempts { get; set; } = 20;

    [JsonPropertyName("ResolveRetryDelaySeconds")]
    public float ResolveRetryDelaySeconds { get; set; } = 0.10f;
}
