using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Celeste.Android.Platform.Audio;
using Celeste.Android.Platform.Filesystem;
using Celeste.Android.Platform.Fullscreen;
using Celeste.Android.Platform.Input;
using Celeste.Android.Platform.Logging;
using Celeste.Android.Platform.Paths;
using Celeste.Core.Platform.Audio;
using Celeste.Core.Platform.Interop;
using Celeste.Core.Platform.Logging;
using Celeste.Core.Platform.Services;
using Microsoft.Xna.Framework;

namespace Celeste.Android;

[Activity(
    Label = "@string/app_name",
    MainLauncher = false,
    Icon = "@drawable/icon",
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize
)]
public class RuntimeActivity : AndroidGameActivity
{
    private CelesteRuntimeGame? _game;
    private View? _view;
    private AndroidDualLogger? _logger;
    private ImmersiveFullscreenController? _fullscreen;
    private PlatformServices? _services;

    protected override void OnCreate(Bundle? bundle)
    {
        base.OnCreate(bundle);

        var paths = new AndroidPathsProvider(this);
        var directoryLayout = paths.EnsureDirectoryLayout();

        _logger = new AndroidDualLogger(paths.LogsPath);
        _fullscreen = new ImmersiveFullscreenController(_logger);

        var fmodEnabled = AudioRuntimePolicy.IsFmodEnabledOnAndroid();
        _logger.Log(
            fmodEnabled ? LogLevel.Info : LogLevel.Warn,
            "AUDIO",
            fmodEnabled
                ? "Android override active: FMOD enabled for test"
                : "Android policy active: FMOD disabled; runtime will run in silent mode");

        _logger.Log(LogLevel.Info, "APP", "RUNTIME_SESSION_START");
        _logger.Log(LogLevel.Info, "PATHS", $"BaseDataPath={paths.BaseDataPath}");
        _logger.Log(LogLevel.Info, "PATHS", $"ContentPath={paths.ContentPath}");
        _logger.Log(LogLevel.Info, "PATHS", $"LogsPath={paths.LogsPath}");
        _logger.Log(LogLevel.Info, "PATHS", $"SavePath={paths.SavePath}");
        _logger.Log(directoryLayout.Success ? LogLevel.Info : LogLevel.Error, "PATHS", directoryLayout.StatusCode, context: directoryLayout.Message);

        CelestePathBridge.Configure(
            () => paths.ContentPath,
            () => paths.SavePath,
            () => paths.LogsPath,
            (level, tag, message) =>
            {
                var mappedLevel = level switch
                {
                    "ERROR" => LogLevel.Error,
                    "WARN" => LogLevel.Warn,
                    _ => LogLevel.Info
                };

                _logger.Log(mappedLevel, tag, message);
            });

        var fileSystem = new AndroidFileSystem(paths, _logger);
        var input = new AndroidInputProvider(_logger);
        var audio = new NullAudioBackend(_logger);
        _services = new PlatformServices(_logger, paths, fileSystem, input, audio);

        global::Celeste.Settings.Initialize();
        if (!global::Celeste.Settings.Existed)
        {
            global::Celeste.Settings.Instance.Language = "english";
        }

        var activeAbi = GetActiveAbi();
        _logger.Log(LogLevel.Info, "RUNTIME", $"Starting Celeste runtime activity ABI={activeAbi}");

        _game = new CelesteRuntimeGame(_services, _fullscreen, this, activeAbi);
        _view = _game.Services.GetService(typeof(View)) as View ?? throw new InvalidOperationException("MonoGame did not provide a root view");
        SetContentView(_view);
        _view.Post(() => _fullscreen.Apply(this, "RuntimeActivity-OnCreate-PostSetContentView"));
        _game.Run();
    }

    protected override void OnResume()
    {
        try
        {
            base.OnResume();
        }
        catch (Exception exception)
        {
            _logger?.Log(LogLevel.Error, "LIFECYCLE", "RuntimeActivity base OnResume failed", exception);
        }

        _logger?.Log(LogLevel.Info, "LIFECYCLE", "RuntimeActivity OnResume");
        _fullscreen?.Apply(this, "RuntimeActivity-OnResume");
        _game?.HandleResume();
    }

    protected override void OnPause()
    {
        _logger?.Log(LogLevel.Info, "LIFECYCLE", "RuntimeActivity OnPause");
        _game?.HandlePause();
        base.OnPause();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        _logger?.Log(LogLevel.Info, "LIFECYCLE", $"RuntimeActivity OnWindowFocusChanged={hasFocus}");

        if (hasFocus)
        {
            _fullscreen?.Apply(this, "RuntimeActivity-OnWindowFocusChanged-HasFocus");
        }

        _game?.HandleFocusChanged(hasFocus);
    }

    protected override void OnDestroy()
    {
        _logger?.Log(LogLevel.Info, "LIFECYCLE", "RuntimeActivity OnDestroy");
        _game?.HandleDestroy();
        _game?.Dispose();
        _game = null;
        _view = null;
        _services = null;
        _logger?.Log(LogLevel.Info, "APP", "RUNTIME_SESSION_END");
        _logger?.Flush();
        _logger?.Dispose();
        base.OnDestroy();
    }

    private static string GetActiveAbi()
    {
        var abis = Build.SupportedAbis;
        if (abis is not null && abis.Count > 0)
        {
            return abis[0];
        }

        return "unknown";
    }
}
