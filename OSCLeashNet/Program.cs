using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OSCLeashNet.Misc;
using OscQueryLibrary;
using OscQueryLibrary.Utils;
using Serilog;
using Serilog.Events;
using VRChatOSCLib;

namespace OSCLeashNet;

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
    
    private static readonly ILogger _logger = Log.ForContext(typeof(Program));

    private static readonly string Prefixedname = GenerateRandomPrefixedString();

    private static void Dispose()
    {
        _oscInstance?.Dispose();
        _currentOscQueryServer?.Dispose();
    }
    public static Task Main()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        Console.Title = Prefixedname;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(LogEventLevel.Verbose,
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var oscQueryServer = new OscQueryServer(Prefixedname, IPAddress.Parse((ReadOnlySpan<char>)Config.Instance.Ip));
        oscQueryServer.FoundVrcClient += FoundVrcClient;
        oscQueryServer.Start();

        if (Utils.WaitForListening(ref _oscInstance))
        { 
            _logger.Information(Config.Instance.Ip == IPAddress.Loopback.ToString() ? "IP: Localhost" : $"IP: {Config.Instance.Ip} | Not Localhost? Wack.");
            _logger.Information("OSCLeash is Running!");
            _logger.Information($"Run deadzone {MathF.Round(Config.Instance.RunDeadzone * 100, 3)}% of stretch");
            _logger.Information($"Walking deadzone {MathF.Round(Config.Instance.WalkDeadzone * 100, 3)}% of stretch");
            _logger.Information($"Delays of {Config.Instance.ActiveDelay * 1000}ms & {Config.Instance.InactiveDelay * 1000}ms");
        }
        
        Thread.Sleep(-1);
        return Task.CompletedTask;
    }

    private static CancellationTokenSource _loopCancellationToken = new();
    private static OscQueryServer? _currentOscQueryServer;

    private static VRChatOSC? _oscInstance;

    private static Task FoundVrcClient(OscQueryServer oscQueryServer, IPEndPoint ipEndPoint)
    {
        _loopCancellationToken.Cancel();
        _loopCancellationToken = new CancellationTokenSource();
        _oscInstance?.Dispose();
        _oscInstance = null;

        _oscInstance = new VRChatOSC();
        _oscInstance.Connect(ipEndPoint.Address, ipEndPoint.Port);
        _oscInstance.Listen(oscQueryServer.OscReceivePort);
        _logger.Information("Sending to port: " + ipEndPoint.Port);
        _logger.Information("Listening on port: " + oscQueryServer.OscReceivePort);
        _oscInstance.TryAddMethod(ZPosAddress.Replace(ParamPrefix, ""), OnReceiveZPos);
        _oscInstance.TryAddMethod(ZNegAddress.Replace(ParamPrefix, ""), OnReceiveZNeg);
        _oscInstance.TryAddMethod(XPosAddress.Replace(ParamPrefix, ""), OnReceiveXPos);
        _oscInstance.TryAddMethod(XNegAddress.Replace(ParamPrefix, ""), OnReceiveXNeg);
        _oscInstance.TryAddMethod(GrabAddress.Replace(ParamPrefix, ""), OnReceiveGrab);
        _oscInstance.TryAddMethod(StretchAddress.Replace(ParamPrefix, ""), OnReceiveStretch);

        _currentOscQueryServer = oscQueryServer;
        ErrorHandledTask.Run(ReceiverLoopAsync);
        return Task.CompletedTask;
    }


    private static async Task ReceiverLoopAsync()
    {
        var currentCancellationToken = _loopCancellationToken.Token;
        TimeSpan delay = TimeSpan.FromSeconds(Config.Instance.ActiveDelay);
        while (!currentCancellationToken.IsCancellationRequested)
        {
            try
            {
                await LeashRun();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error in receiver loop");
            }
            await Task.Delay(delay, currentCancellationToken);
        }
    }

    
    static Task LeashRun()
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

        return Task.CompletedTask;
    }

    static void LeashOutput(float vertical, float horizontal, float run)
    {
        _oscInstance!.SendInput(VRCAxes.Vertical, vertical);
        _oscInstance.SendInput(VRCAxes.Horizontal, horizontal);
        _oscInstance.SendInput(VRCButton.Run, run);

        if(Logging)
            Console.WriteLine($"Sending: Vertical - {MathF.Round(vertical, 2)} | Horizontal = {MathF.Round(horizontal, 2)} | Run - {run}");
    }
    
    static void OnReceiveZPos(VRCMessage msg)
    {
        try
        {
            lock (LockObj)
            {
                Leash.ZPositive = msg.GetValue<float>();
            }
        }
        catch (Exception ex)
        {
            _logger.Information(
                $"Exception occured when trying to read float value on address {ZPosAddress}:\n{ex.Message}");
        }
    }

    static void OnReceiveZNeg(VRCMessage msg)
    {
        try
        {
            lock (LockObj)
                Leash.ZNegative = msg.GetValue<float>();
        }
        catch (Exception ex)
        {
            _logger.Information(
                $"Exception occured when trying to read float value on address {ZNegAddress}:\n{ex.Message}");
        }
    }

    static void OnReceiveXPos(VRCMessage msg)
    {
        try
        {
            lock (LockObj)
                Leash.XPositive = msg.GetValue<float>();
        }
        catch (Exception ex)
        {
            _logger.Information(
                $"Exception occured when trying to read float value on address {XPosAddress}:\n{ex.Message}");
        }
    }

    static void OnReceiveXNeg(VRCMessage msg)
    {
        try
        {
            lock (LockObj)
                Leash.XNegative = msg.GetValue<float>();
        }
        catch (Exception ex)
        {
            _logger.Information(
                $"Exception occured when trying to read float value on address {XNegAddress}:\n{ex.Message}");
        }
    }

    static void OnReceiveStretch(VRCMessage msg)
    {
        try
        {
            lock (LockObj)
                Leash.Stretch = msg.GetValue<float>();
        }
        catch (Exception ex)
        {
            _logger.Information(
                $"Exception occured when trying to read float value on address {StretchAddress}:\n{ex.Message}");
        }
    }

    static void OnReceiveGrab(VRCMessage msg)
    {
        try
        {
            lock (LockObj)
                Leash.Grabbed = msg.GetValue<bool>();
        }
        catch (Exception ex)
        {
            _logger.Information(
                $"Exception occured when trying to read float value on address {GrabAddress}:\n{ex.Message}");
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