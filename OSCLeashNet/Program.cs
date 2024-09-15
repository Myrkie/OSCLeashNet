using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OscQueryLibrary;
using VRChatOSCLib;

namespace OSCLeashNet
{
    public static class Program
    {
        const string ParamPrefix = "/avatar/parameters/";
        
        static readonly string ZPosAddress = $"{ParamPrefix}{Config.Instance.Parameters["Z_Positive"]}";
        static readonly string ZNegAddress = $"{ParamPrefix}{Config.Instance.Parameters["Z_Negative"]}";
        static readonly string XPosAddress = $"{ParamPrefix}{Config.Instance.Parameters["X_Positive"]}";
        static readonly string XNegAddress = $"{ParamPrefix}{Config.Instance.Parameters["X_Negative"]}";
        static readonly string GrabAddress = $"{ParamPrefix}{Config.Instance.Parameters["PhysboneParameter"]}_IsGrabbed";
        static readonly string StretchAddress = $"{ParamPrefix}{Config.Instance.Parameters["PhysboneParameter"]}_Stretch";
        
        static readonly object LockObj = new();
        static readonly LeashParameters Leash = new();

        static readonly float RunDeadzone = Config.Instance.RunDeadzone;
        static readonly float WalkDeadzone = Config.Instance.WalkDeadzone;
        static readonly TimeSpan InactiveDelay = TimeSpan.FromSeconds(Config.Instance.InputSendDelay);
        static readonly bool Logging = Config.Instance.Logging;
        
        private static readonly VRChatOSC OSC = new();

        private static string prefixedname = GenerateRandomPrefixedString();
        private static OscQueryServer OscQueryServer = new(prefixedname, Config.Instance.Ip);

        private static void Dispose()
        {
            OSC.Dispose();
            OscQueryServer.Dispose();
        }
        public static async Task Main()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
            if (Config.Instance.DebugMode)
            { 
                // override this in case the user wants to test in unity
                // "receive all parameters not in avatar json" must be enabled in Lyuma av3 emulator to get input
                OscQueryServer.OscReceivePort = 9001; 
                OscQueryServer.OscSendPort = 9000;
            }

            Console.Title = prefixedname;
            Console.WriteLine("OSCLeash is Running!");
            Console.WriteLine(Config.Instance.Ip == IPAddress.Loopback.ToString() ? "IP: Localhost" : $"IP: {Config.Instance.Ip} | Not Localhost? Wack.");
            Console.WriteLine("Listening on port: " + OscQueryServer.OscReceivePort);
            Console.WriteLine("Sending to port: " + OscQueryServer.OscSendPort);
            Console.WriteLine($"Run deadzone {MathF.Round(Config.Instance.RunDeadzone * 100, 3)}% of stretch");
            Console.WriteLine($"Walking deadzone {MathF.Round(Config.Instance.WalkDeadzone * 100, 3)}% of stretch");
            Console.WriteLine($"Delays of {Config.Instance.ActiveDelay * 1000}ms & {Config.Instance.InactiveDelay * 1000}ms");
            
            StartServer();
            await Task.Run(async () =>
            {
                LeashOutput(0f, 0f, 0f);
                TimeSpan delay = TimeSpan.FromSeconds(Config.Instance.ActiveDelay);
                while(true)
                {
                    LeashRun();
                    await Task.Delay(delay);
                }
            });
        }
        
        static void StartServer()
        {
            if(IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(x => x.Port == OscQueryServer.OscReceivePort))
            {
                Console.WriteLine($"Warning: An application is already running on port {OscQueryServer.OscReceivePort}!");
                Console.WriteLine("Press any key to Exit.");

                Console.ReadKey(true);
                Environment.Exit(0);
            }

            OSC.Connect(Config.Instance.Ip, OscQueryServer.OscSendPort);
            OSC.Listen(OscQueryServer.OscReceivePort);

            OSC.TryAddMethod(ZPosAddress.Replace(ParamPrefix, ""), OnReceiveZPos);
            OSC.TryAddMethod(ZNegAddress.Replace(ParamPrefix, ""), OnReceiveZNeg);
            OSC.TryAddMethod(XPosAddress.Replace(ParamPrefix, ""), OnReceiveXPos);
            OSC.TryAddMethod(XNegAddress.Replace(ParamPrefix, ""), OnReceiveXNeg);
            OSC.TryAddMethod(GrabAddress.Replace(ParamPrefix, ""), OnReceiveGrab);
            OSC.TryAddMethod(StretchAddress.Replace(ParamPrefix, ""), OnReceiveStretch);
        }
        
        static void LeashRun()
        {
            bool leashGrabbed, leashReleased;
            float verticalOutput, horizontalOutput;

            lock(LockObj)
            {
                verticalOutput = (Leash.ZPositive - Leash.ZNegative) * Leash.Stretch;
                horizontalOutput = (Leash.XPositive - Leash.XNegative) * Leash.Stretch;

                leashGrabbed = Leash.Grabbed;

                if(leashGrabbed)
                    Leash.WasGrabbed = true;

                leashReleased = Leash.Grabbed != Leash.WasGrabbed;

                if(leashReleased)
                    Leash.WasGrabbed = false;
            }

            if(leashGrabbed)
            {
                lock (LockObj)
                {
                    if(Leash.Stretch > RunDeadzone)
                        LeashOutput(verticalOutput, horizontalOutput, 1f);
                    else if(Leash.Stretch > WalkDeadzone)
                        LeashOutput(verticalOutput, horizontalOutput, 0f);
                    else
                        LeashOutput(0f, 0f, 0f);
                }
            }
            else if(leashReleased)
            {
                LeashOutput(0f, 0f, 0f);
                Thread.Sleep(InactiveDelay);
                LeashOutput(0f, 0f, 0f);
            }
            else
            {
                Thread.Sleep(InactiveDelay);
            }
        }

        static void LeashOutput(float vertical, float horizontal, float run)
        {
            OSC.SendInput(VRCAxes.Vertical, vertical);
            OSC.SendInput(VRCAxes.Horizontal, horizontal);
            OSC.SendInput(VRCButton.Run, run);

            if(Logging)
                Console.WriteLine($"Sending: Vertical - {MathF.Round(vertical, 2)} | Horizontal = {MathF.Round(horizontal, 2)} | Run - {run}");
        }
        
        static void OnReceiveZPos(VRCMessage msg)
        {
            try
            {
                lock(LockObj)
                {
                    Leash.ZPositive = msg.GetValue<float>();
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Exception occured when trying to read float value on address {ZPosAddress}:\n{ex.Message}");
            }
        }
        
        static void OnReceiveZNeg(VRCMessage msg)
        {
            try
            {
                lock(LockObj)
                    Leash.ZNegative = msg.GetValue<float>();
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Exception occured when trying to read float value on address {ZNegAddress}:\n{ex.Message}");
            }
        }

        static void OnReceiveXPos(VRCMessage msg)
        {
            try
            {
                lock(LockObj)
                    Leash.XPositive = msg.GetValue<float>();
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Exception occured when trying to read float value on address {XPosAddress}:\n{ex.Message}");
            }
        }

        static void OnReceiveXNeg(VRCMessage msg)
        {
            try
            {
                lock(LockObj)
                    Leash.XNegative = msg.GetValue<float>();
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Exception occured when trying to read float value on address {XNegAddress}:\n{ex.Message}");
            }
        }

        static void OnReceiveStretch(VRCMessage msg)
        {
            try
            {
                lock(LockObj)
                    Leash.Stretch = msg.GetValue<float>();
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Exception occured when trying to read float value on address {StretchAddress}:\n{ex.Message}");
            }
        }

        static void OnReceiveGrab(VRCMessage msg)
        {
            try
            {
                lock(LockObj)
                    Leash.Grabbed = msg.GetValue<bool>();
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Exception occured when trying to read float value on address {GrabAddress}:\n{ex.Message}");
            }
        }
        
        private static string GenerateRandomPrefixedString()
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
    }
}