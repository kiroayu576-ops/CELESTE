using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Celeste.Android.Platform.Audio;
using Celeste.Android.Platform.Filesystem;
using Celeste.Android.Platform.Fullscreen;
using Celeste.Android.Platform.Input;
using Celeste.Android.Platform.Lifecycle;
using Celeste.Android.Platform.Logging;
using Celeste.Android.Platform.Paths;
using Celeste.Core.Platform.Logging;
using Celeste.Core.Platform.Services;
using Microsoft.Xna.Framework;

namespace Celeste.Android;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Icon = "@drawable/icon",
    AlwaysRetainTaskState = true,
    LaunchMode = LaunchMode.SingleInstance,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize
)]
public class Activity1 : AndroidGameActivity
{
    private Game? _currentGame;
    private IAndroidGameLifecycle? _currentLifecycle;
    private View? _currentView;
    private AndroidDualLogger? _logger;
    private ImmersiveFullscreenController? _fullscreen;
    private PlatformServices? _services;
    private string _activeAbi = "unknown";
    private bool _runtimeLaunchDispatched;

    protected override void OnCreate(Bundle? bundle)
    {
        base.OnCreate(bundle);

        var paths = new AndroidPathsProvider(this);
        var directoryLayout = paths.EnsureDirectoryLayout();

        _logger = new AndroidDualLogger(paths.LogsPath);
        _fullscreen = new ImmersiveFullscreenController(_logger);
        RegisterGlobalExceptionHandlers(_logger);

        _logger.Log(LogLevel.Info, "APP", "SESSION_START");
        _logger.Log(LogLevel.Info, "DEVICE", $"Manufacturer={Build.Manufacturer}; Model={Build.Model}; ApiLevel={(int)Build.VERSION.SdkInt}");
        _logger.Log(LogLevel.Info, "RUNTIME", $"SupportedAbis={string.Join(",", Build.SupportedAbis ?? Array.Empty<string>())}");
        _logger.Log(LogLevel.Info, "PATHS", $"BaseDataPath={paths.BaseDataPath}");
        _logger.Log(LogLevel.Info, "PATHS", $"ContentPath={paths.ContentPath}");
        _logger.Log(LogLevel.Info, "PATHS", $"LogsPath={paths.LogsPath}");
        _logger.Log(LogLevel.Info, "PATHS", $"SavePath={paths.SavePath}");
        _logger.Log(directoryLayout.Success ? LogLevel.Info : LogLevel.Error, "PATHS", directoryLayout.StatusCode, context: directoryLayout.Message);

        var fileSystem = new AndroidFileSystem(paths, _logger);
        var input = new AndroidInputProvider(_logger);
        var audio = new FmodAudioBackend(_logger);
        _services = new PlatformServices(_logger, paths, fileSystem, input, audio);
        _activeAbi = GetActiveAbi();

        RunLauncherThenRuntime();
    }

    protected override void OnResume()
    {
        try
        {
            base.OnResume();
        }
        catch (Exception exception)
        {
            _logger?.Log(LogLevel.Error, "LIFECYCLE", "Base OnResume failed", exception);
        }

        _logger?.Log(LogLevel.Info, "LIFECYCLE", "OnResume");
        _fullscreen?.Apply(this, "OnResume");
        _currentLifecycle?.HandleResume();
    }

    protected override void OnPause()
    {
        _logger?.Log(LogLevel.Info, "LIFECYCLE", "OnPause");
        _currentLifecycle?.HandlePause();
        base.OnPause();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        _logger?.Log(LogLevel.Info, "LIFECYCLE", $"OnWindowFocusChanged={hasFocus}");

        if (hasFocus)
        {
            _fullscreen?.Apply(this, "OnWindowFocusChanged-HasFocus");
        }

        _currentLifecycle?.HandleFocusChanged(hasFocus);
    }

    protected override void OnDestroy()
    {
        _logger?.Log(LogLevel.Info, "LIFECYCLE", "OnDestroy");
        _currentLifecycle?.HandleDestroy();
        _currentGame?.Dispose();
        _currentGame = null;
        _currentLifecycle = null;
        _currentView = null;
        _logger?.Log(LogLevel.Info, "APP", "SESSION_END");
        _logger?.Flush();
        _logger?.Dispose();
        base.OnDestroy();
    }

    private void RunLauncherThenRuntime()
    {
        if (_services is null || _fullscreen is null)
        {
            throw new InvalidOperationException("Platform services were not initialized");
        }

        _runtimeLaunchDispatched = false;
        var launcher = new Game1(_services, _fullscreen, this, _activeAbi, RequestRuntimeLaunch);
        RunGameInstance(launcher, "Launcher");
    }

    private void RequestRuntimeLaunch()
    {
        if (_runtimeLaunchDispatched)
        {
            return;
        }

        _runtimeLaunchDispatched = true;
        _logger?.Log(LogLevel.Info, "BOOT", "Runtime launch requested by launcher, opening RuntimeActivity");

        RunOnUiThread(() =>
        {
            try
            {
                var intent = new Intent(this, typeof(RuntimeActivity));
                StartActivity(intent);
                Finish();
            }
            catch (Exception exception)
            {
                _runtimeLaunchDispatched = false;
                _logger?.Log(LogLevel.Error, "BOOT", "Failed to launch RuntimeActivity", exception);
            }
        });
    }

    private void RunGameInstance(Game game, string stage)
    {
        _logger?.Log(LogLevel.Info, "BOOT", $"RunGameInstance stage={stage}");
        _currentGame = game;
        _currentLifecycle = game as IAndroidGameLifecycle;
        _currentView = game.Services.GetService(typeof(View)) as View ?? throw new InvalidOperationException("MonoGame did not provide a root view");

        SetContentView(_currentView);
        _currentView.Post(() => _fullscreen?.Apply(this, $"RunGameInstance-{stage}-PostSetContentView"));
        game.Run();
        _logger?.Log(LogLevel.Info, "BOOT", $"RunGameInstance started stage={stage}");
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

    private static void RegisterGlobalExceptionHandlers(AndroidDualLogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            logger.Log(LogLevel.Error, "EXCEPTION", "AppDomain.CurrentDomain.UnhandledException", args.ExceptionObject as Exception);
            logger.Flush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.Log(LogLevel.Error, "EXCEPTION", "TaskScheduler.UnobservedTaskException", args.Exception);
            logger.Flush();
            args.SetObserved();
        };

        AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
        {
            logger.Log(LogLevel.Error, "EXCEPTION", "AndroidEnvironment.UnhandledExceptionRaiser", args.Exception);
            logger.Flush();
        };
    }
}
