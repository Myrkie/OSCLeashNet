using System;
using System.Reflection;
using System.Text;
using System.Threading;
using Serilog;
using VRChatOSCLib;

namespace OSCLeashNet.Misc
{
    public static class Utils
    {
        public static string GenerateRandomPrefixedString()
        {
            Random random = new Random();
            StringBuilder stringBuilder = new StringBuilder("Leash-OSC-");

            for (int i = 0; i < 5; i++)
            {
                int randomNumber = random.Next(0, 10);
                stringBuilder.Append(randomNumber);
            }

            char randomLetter = (char)random.Next('A', 'Z' + 1);
            stringBuilder.Append(randomLetter);

            return stringBuilder.ToString();
        }
        
        private static readonly ILogger Logger = Log.ForContext(typeof(Utils));

        public static void WaitForListening(ref VRChatOSC? oscInstance)
        {
            FieldInfo? listeningField = null;

            while (true)
            {
                if (oscInstance == null)
                {
                    Logger.Error("VRChatOSC instance is null. Waiting for it to be initialized, this can also result from VRChat not running or VRChat returning no ports.");
                    Thread.Sleep(2000);
                    continue;
                }

                if (listeningField == null)
                {
                    try
                    {
                        listeningField =
#pragma warning disable IL2065
                            typeof(VRChatOSC).GetField("m_Listening", BindingFlags.NonPublic | BindingFlags.Instance);
#pragma warning restore IL2065

                        if (listeningField == null)
                        {
                            throw new Exception("Field 'm_Listening' not found.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to retrieve the 'm_Listening' field.");
                        throw;
                    }
                }

                try
                {
                    var isListening = (bool)listeningField.GetValue(oscInstance)!;

                    if (isListening)
                    {
                        Logger.Information("VRChatOSC instance is ready and listening!");
                        Logger.Information("OSCLeash is Running!");
                        
                        Logger.Information($"Run deadzone {MathF.Round(Config.Instance.Deadzone.RunDeadzone * 100, 3)}% of stretch");
                        Logger.Information($"Walking deadzone {MathF.Round(Config.Instance.Deadzone.WalkDeadzone * 100, 3)}% of stretch");
                        Logger.Information($"Delays of {Config.Instance.Delay.ActiveDelay * 1000}ms & {Config.Instance.Delay.InactiveDelay * 1000}ms");
                        break;
                    }

                    Logger.Error("VRChatOSC is not listening yet. Checking again...");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to get value of 'm_Listening' field.");
                    throw;
                }

                Thread.Sleep(1000);
            }
        }
    }
}
