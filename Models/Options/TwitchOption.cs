﻿namespace LivestreamRecorderService.Models.Options
{
    public class TwitchOption
    {
#pragma warning disable IDE1006 // 命名樣式
    public const string ConfigurationSectionName = "Twitch";
#pragma warning restore IDE1006 // 命名樣式

        public required string ClientId { get; set; }
        public required string ClientSecret { get; set; }
    }
}
