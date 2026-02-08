namespace Celeste.Android.Platform.Lifecycle;

public interface IAndroidGameLifecycle
{
    void HandlePause();

    void HandleResume();

    void HandleFocusChanged(bool hasFocus);

    void HandleDestroy();
}
