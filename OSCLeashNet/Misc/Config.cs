using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OSCLeashNet.Misc
{
    [JsonSerializable(typeof(Config))]
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default, WriteIndented = true, AllowTrailingCommas = true)]
    internal partial class ConfigSourceGenerationContext : JsonSerializerContext;
    [Serializable]
    public class Config
    {
        static readonly string ConfigPath = $"{AppContext.BaseDirectory}config.json";
        public static Config Instance { get; } = LoadConfig();

        public string Ip { get; set; } = "127.0.0.1";
        public float RunDeadzone { get; set; } = 0.70f;
        public float WalkDeadzone { get; set; } = 0.15f;
        public float ActiveDelay { get; set; } = 0.1f;
        public float InactiveDelay { get; set; } = 0.15f;
        public float InputSendDelay { get; set; } = 0.1f;
        public bool Logging { get; set; }

        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>()
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