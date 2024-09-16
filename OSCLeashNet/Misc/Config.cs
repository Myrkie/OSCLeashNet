using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OSCLeashNet.Misc
{
    [JsonSerializable(typeof(NetworkConfig))]
    [JsonSerializable(typeof(DeadzoneConfig))]
    [JsonSerializable(typeof(DelayConfig))]
    [JsonSerializable(typeof(Config))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default, WriteIndented = true, AllowTrailingCommas = true)]
    internal partial class ConfigSourceGenerationContext : JsonSerializerContext;

    [Serializable]
    public class NetworkConfig
    {
        public string Ip { get; set; } = "127.0.0.1";
        public int ListeningPort { get; set; } = 9001;
        public int SendingPort { get; set; } = 9000;
        public bool UseConfigPorts { get; set; }
    }

    [Serializable]
    public class DeadzoneConfig
    {
        public float RunDeadzone { get; set; } = 0.70f;
        public float WalkDeadzone { get; set; } = 0.15f;
    }

    [Serializable]
    public class DelayConfig
    {
        public float ActiveDelay { get; set; } = 0.1f;
        public float InactiveDelay { get; set; } = 0.15f;
        public float InputSendDelay { get; set; } = 0.1f;
    }

    [Serializable]
    public class Config
    {
        static readonly string ConfigPath = $"{AppContext.BaseDirectory}config.json";
        public static Config Instance { get; } = LoadConfig();

        public NetworkConfig Network { get; set; } = new();
        public DeadzoneConfig Deadzone { get; set; } = new();
        public DelayConfig Delay { get; set; } = new();

        public bool Logging { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new()
        {
            { "Z_Positive", "Leash_Z+" },
            { "Z_Negative", "Leash_Z-" },
            { "X_Positive", "Leash_X+" },
            { "X_Negative", "Leash_X-" },
            { "PhysboneParameter", "Leash" },
        };

        static Config LoadConfig()
        {
            Config? cfg = File.Exists(ConfigPath) ? JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), ConfigSourceGenerationContext.Default.Config) : null;
            if(cfg == null)
            {
                cfg = new Config();
                cfg.SaveConfig();
            }

            return cfg;
        }

        void SaveConfig()
        {
            string json = JsonSerializer.Serialize(this, ConfigSourceGenerationContext.Default.Config);
            File.WriteAllText(ConfigPath, json);
        }
    }
}
