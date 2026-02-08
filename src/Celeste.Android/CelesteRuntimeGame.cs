using Android.App;
using Celeste.Android.Platform.Fullscreen;
using Celeste.Android.Platform.Lifecycle;
using Celeste.Core.Platform.Logging;
using Celeste.Core.Platform.Services;

namespace Celeste.Android;

public sealed class CelesteRuntimeGame : global::Celeste.Celeste, IAndroidGameLifecycle
{
    private readonly PlatformServices _services;
    private readonly ImmersiveFullscreenController _fullscreen;
    private readonly Activity _activity;
    private readonly string _activeAbi;

    public CelesteRuntimeGame(PlatformServices services, ImmersiveFullscreenController fullscreen, Activity activity, string activeAbi)
    {
        _services = services;
        _fullscreen = fullscreen;
        _activity = activity;
        _activeAbi = activeAbi;

        _services.Logger.Log(LogLevel.Info, "RUNTIME", "CelesteRuntimeGame created");
    }

    protected override void Initialize()
    {
        _services.Logger.Log(LogLevel.Info, "RUNTIME", $"Initialize runtime ABI={_activeAbi}");
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _services.Logger.Log(LogLevel.Info, "RUNTIME", "LoadContent begin");
        base.LoadContent();
        _services.Logger.Log(LogLevel.Info, "RUNTIME", "LoadContent done");
    }

    public void HandlePause()
    {
        _services.Logger.Log(LogLevel.Info, "LIFECYCLE", "Runtime HandlePause");
        global::Celeste.Audio.PauseMusic = true;
        global::Celeste.Audio.PauseGameplaySfx = true;
        global::Celeste.Audio.PauseUISfx = true;
    }

    public void HandleResume()
    {
        _services.Logger.Log(LogLevel.Info, "LIFECYCLE", "Runtime HandleResume");
        _fullscreen.Apply(_activity, "Runtime-HandleResume");
        global::Celeste.Audio.PauseMusic = false;
        global::Celeste.Audio.PauseGameplaySfx = false;
        global::Celeste.Audio.PauseUISfx = false;
    }

    public void HandleFocusChanged(bool hasFocus)
    {
        _services.Logger.Log(LogLevel.Info, "LIFECYCLE", $"Runtime HandleFocusChanged={hasFocus}");
        if (hasFocus)
        {
            _fullscreen.Apply(_activity, "Runtime-HandleFocusChanged");
        }
    }

    public void HandleDestroy()
    {
        _services.Logger.Log(LogLevel.Info, "LIFECYCLE", "Runtime HandleDestroy");
        _services.Logger.Flush();
    }
}
