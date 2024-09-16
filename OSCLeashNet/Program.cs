using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OSCLeashNet.Misc;
using OscQueryLibrary;
using OscQueryLibrary.Utils;
using Serilog;
using VRChatOSCLib;

namespace OSCLeashNet;

public static class Program
{
    #region Parameter Addresses
    
    private static readonly string ZPosAddress = Config.Instance.Parameters["Z_Positive"];
    private static readonly string ZNegAddress = Config.Instance.Parameters["Z_Negative"];
    private static readonly string XPosAddress = Config.Instance.Parameters["X_Positive"];
    private static readonly string XNegAddress = Config.Instance.Parameters["X_Negative"];
    private static readonly string GrabAddress = $"{Config.Instance.Parameters["PhysboneParameter"]}_IsGrabbed";
    private static readonly string StretchAddress = $"{Config.Instance.Parameters["PhysboneParameter"]}_Stretch";
    
    #endregion
    
    private static readonly object LockObj = new();
    private static readonly LeashParameters Leash = new();

    #region applied configs
    
    private static readonly float RunDeadzone = Config.Instance.Deadzone.RunDeadzone;
    private static readonly float WalkDeadzone = Config.Instance.Deadzone.WalkDeadzone;
    private static readonly TimeSpan InactiveDelay = TimeSpan.FromSeconds(Config.Instance.Delay.InputSendDelay);
    
    #endregion
    
    private static readonly bool Logging = Config.Instance.Logging;
    private static ILogger Logger = null;
    private static readonly string PrefixName = GenerateRandomPrefixedString();

    private static void Dispose()
    {
        _oscInstance?.Dispose();
        _currentOscQueryServer?.Dispose();
    }
    public static async Task Main()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        Console.Title = PrefixName;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code)
            .CreateLogger();
        
        Logger = Log.ForContext(typeof(Program));
        
        if (!Config.Instance.Network.UseConfigPorts)
        { 
            var oscQueryServer = new OscQueryServer(PrefixName, IPAddress.Parse((ReadOnlySpan<char>)Config.Instance.Network.Ip));
            oscQueryServer.FoundVrcClient += FoundVrcClient;
            oscQueryServer.Start();
            Logger.Information($"{AppDomain.CurrentDomain.FriendlyName}: Starting up, building connections.");
            Utils.WaitForListening(ref _oscInstance);
        }
        else
        {
            Logger.Information("Debug mode has been activated, your ports will be defined by config.");
            await PortOverride();
            Utils.WaitForListening(ref _oscInstance);
        }
        Thread.Sleep(-1);
    }

    #region OSCConnection
    
    private static CancellationTokenSource _loopCancellationToken = new();
    private static OscQueryServer? _currentOscQueryServer;
    private static VRChatOSC? _oscInstance;

    #endregion

    private static async Task FoundVrcClient(OscQueryServer? oscQueryServer, IPEndPoint? ipEndPoint)
    {
        await _loopCancellationToken.CancelAsync();
        _loopCancellationToken = new CancellationTokenSource();
        _oscInstance?.Dispose();
        _oscInstance = null;

        _oscInstance = new VRChatOSC();
        _oscInstance.Connect(ipEndPoint!.Address, ipEndPoint.Port);
        _oscInstance.Listen(ipEndPoint.Address, oscQueryServer!.OscReceivePort);
        Logger.Information("Sending to {ip}|{port} ", ipEndPoint.Address, ipEndPoint.Port);
        Logger.Information("Listening on {ip}|{port} ", ipEndPoint.Address, oscQueryServer.OscReceivePort);
        _oscInstance.TryAddMethod(ZPosAddress, OnReceiveZPos);
        _oscInstance.TryAddMethod(ZNegAddress, OnReceiveZNeg);
        _oscInstance.TryAddMethod(XPosAddress, OnReceiveXPos);
        _oscInstance.TryAddMethod(XNegAddress, OnReceiveXNeg);
        _oscInstance.TryAddMethod(GrabAddress, OnReceiveGrab);
        _oscInstance.TryAddMethod(StretchAddress, OnReceiveStretch);

        _currentOscQueryServer = oscQueryServer;
        await ErrorHandledTask.Run(ReceiverLoopAsync);
    }

    private static async Task PortOverride()
    {
        await _loopCancellationToken.CancelAsync();
        _loopCancellationToken = new CancellationTokenSource();
        _oscInstance?.Dispose();
        _oscInstance = null;
        
        _oscInstance = new VRChatOSC();
        _oscInstance.Connect(Config.Instance.Network.Ip, Config.Instance.Network.SendingPort);
        _oscInstance.Listen(IPAddress.Parse(Config.Instance.Network.Ip), Config.Instance.Network.ListeningPort);
        
        Logger.Information("Sending to {ip}|{port} ", Config.Instance.Network.Ip, Config.Instance.Network.SendingPort);
        Logger.Information("Listening on {ip}|{port} ", Config.Instance.Network.Ip, Config.Instance.Network.ListeningPort);
        _oscInstance.TryAddMethod(ZPosAddress, OnReceiveZPos);
        _oscInstance.TryAddMethod(ZNegAddress, OnReceiveZNeg);
        _oscInstance.TryAddMethod(XPosAddress, OnReceiveXPos);
        _oscInstance.TryAddMethod(XNegAddress, OnReceiveXNeg);
        _oscInstance.TryAddMethod(GrabAddress, OnReceiveGrab);
        _oscInstance.TryAddMethod(StretchAddress, OnReceiveStretch);

        await ErrorHandledTask.Run(ReceiverLoopAsync);
    }


    private static async Task ReceiverLoopAsync()
    {
        await LeashOutput(0f, 0f, 0f);
        var currentCancellationToken = _loopCancellationToken.Token;
        var delay = TimeSpan.FromSeconds(Config.Instance.Delay.ActiveDelay);
        while (!currentCancellationToken.IsCancellationRequested)
        {
            try
            {
                await LeashRun();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Error in receiver loop");
            }
            await Task.Delay(delay, currentCancellationToken);
        }
    }


    private static Task LeashRun()
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
                    _ = LeashOutput(verticalOutput, horizontalOutput, 1f);
                else if(Leash.Stretch > WalkDeadzone)
                    _ = LeashOutput(verticalOutput, horizontalOutput, 0f);
                else
                    _ = LeashOutput(0f, 0f, 0f);
            }
        }
        else if(leashReleased)
        {
            _ = LeashOutput(0f, 0f, 0f);
            Thread.Sleep(InactiveDelay);
            _ = LeashOutput(0f, 0f, 0f);
        }
        else
        {
            Thread.Sleep(InactiveDelay);
        }

        return Task.CompletedTask;
    }

    private static async Task LeashOutput(float vertical, float horizontal, float run)
    {
        await _oscInstance!.SendInputAsync(VRCAxes.Vertical, vertical);
        await _oscInstance.SendInputAsync(VRCAxes.Horizontal, horizontal);
        await _oscInstance.SendInputAsync(VRCButton.Run, run);

        if(Logging)
            Logger.Information($"Sending: Vertical - {MathF.Round(vertical, 2)} | Horizontal = {MathF.Round(horizontal, 2)} | Run - {run}");
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
            Logger.Error(
                "Exception occured when trying to read float value on address {Zpos}:\n{exMsg}", ZPosAddress, ex);
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
            Logger.Error(
                "Exception occured when trying to read float value on address {Zneg}:\n{exMsg}", ZNegAddress, ex);
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
            Logger.Error(
                "Exception occured when trying to read float value on address {Xpos}:\n{exMsg}", XPosAddress, ex);
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
            Logger.Error(
                "Exception occured when trying to read float value on address {Xneg}:\n{exMsg}", XNegAddress, ex);
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
            Logger.Error(
                "Exception occured when trying to read float value on address {Stretch}:\n{exMsg}", StretchAddress, ex);
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
            Logger.Error(
                "Exception occured when trying to read float value on address {Grab}:\n{exMsg}", GrabAddress, ex);
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