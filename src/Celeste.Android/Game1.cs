using System;
using System.Linq;
using Android.App;
using Celeste.Android.Platform.Fullscreen;
using Celeste.Android.Platform.Lifecycle;
using Celeste.Android.Platform.Rendering;
using Celeste.Core.Platform.Boot;
using Celeste.Core.Platform.Content;
using Celeste.Core.Platform.Diagnostics;
using Celeste.Core.Platform.Interop;
using Celeste.Core.Platform.Logging;
using Celeste.Core.Platform.Services;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input.Touch;

namespace Celeste.Android;

public class Game1 : Game, IAndroidGameLifecycle
{
    private readonly PlatformServices _services;
    private readonly ImmersiveFullscreenController _fullscreen;
    private readonly Activity _activity;
    private readonly string _activeAbi;
    private readonly BootStateMachine _bootStateMachine;
    private readonly ContentValidator _contentValidator;
    private readonly BitmapFallbackFont _fallbackFont;
    private readonly Action _requestRuntimeLaunch;

    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch? _spriteBatch;
    private SpriteFont? _internalErrorFont;
    private Texture2D? _pixel;
    private ContentValidationReport? _lastContentReport;
    private BootExecutionResult? _lastBootResult;

    private bool _bootCompleted;
    private bool _showErrorScreen;
    private bool _touchIgnoredLogged;
    private bool _diagnosticMode;
    private bool _diagLatch;
    private bool _runtimeLaunchRequested;
    private double _diagHoldSeconds;
    private string _bootMessage = "BOOTING";
    private string _gpuInfo = "GPU=Pending";

    public Game1(
        PlatformServices services,
        ImmersiveFullscreenController fullscreen,
        Activity activity,
        string activeAbi,
        Action requestRuntimeLaunch)
    {
        _services = services;
        _fullscreen = fullscreen;
        _activity = activity;
        _activeAbi = activeAbi;
        _requestRuntimeLaunch = requestRuntimeLaunch;

        _bootStateMachine = new BootStateMachine();
        _contentValidator = new ContentValidator(_services.Paths, _services.FileSystem);
        _fallbackFont = new BitmapFallbackFont();

        _graphics = new GraphicsDeviceManager(this)
        {
            IsFullScreen = true,
            SupportedOrientations = DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = false;
        TouchPanel.EnabledGestures = GestureType.None;
    }

    protected override void Initialize()
    {
        RunBoot(BootPhase.BootInitLogger, isRetry: false);
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });

        try
        {
            _internalErrorFont = Content.Load<SpriteFont>("ErrorFont");
            _services.Logger.Log(LogLevel.Info, "UI", "Internal error SpriteFont loaded");
        }
        catch (Exception exception)
        {
            _internalErrorFont = null;
            _services.Logger.Log(LogLevel.Warn, "UI", "Internal SpriteFont unavailable, using bitmap fallback", exception);
        }

        _gpuInfo = $"GPU={GraphicsAdapter.DefaultAdapter.Description}";
        _services.Logger.Log(LogLevel.Info, "GPU", _gpuInfo);
    }

    protected override void Update(GameTime gameTime)
    {
        _services.Input.Update();
        UpdateDiagnosticToggle(gameTime);
        IgnoreTouchInput();

        if (_showErrorScreen)
        {
            if (_services.Input.IsRetryPressed())
            {
                _services.Logger.Log(LogLevel.Info, "CONTENT", "Retry requested from error screen");
                RunBoot(BootPhase.BootValidateContent, isRetry: true);
            }

            if (_services.Input.IsBackPressed())
            {
                _services.Logger.Log(LogLevel.Warn, "ERROR_SCREEN", "USER_EXIT_FROM_ERROR_SCREEN");
                Exit();
            }
        }
        else if (_bootCompleted && !_runtimeLaunchRequested)
        {
            _runtimeLaunchRequested = true;
            _services.Logger.Log(LogLevel.Info, "BOOT", "BOOT_COMPLETED_REQUEST_RUNTIME");
            _requestRuntimeLaunch();
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(_showErrorScreen ? new Color(28, 8, 8) : Color.Black);

        if (_spriteBatch is null || _pixel is null)
        {
            base.Draw(gameTime);
            return;
        }

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);

        if (_showErrorScreen)
        {
            DrawErrorScreen();
        }
        else if (_bootCompleted)
        {
            DrawBootReadyScreen();
        }
        else
        {
            DrawBootingScreen();
        }

        if (_diagnosticMode)
        {
            DrawDiagnosticOverlay();
        }

        _spriteBatch.End();
        base.Draw(gameTime);
    }

    public void HandlePause()
    {
        _services.Logger.Log(LogLevel.Info, "LIFECYCLE", "Game HandlePause");
        _services.Audio.OnPause();
    }

    public void HandleResume()
    {
        _services.Logger.Log(LogLevel.Info, "LIFECYCLE", "Game HandleResume");
        _services.Audio.OnResume();
        _fullscreen.Apply(_activity, "Game-HandleResume");
    }

    public void HandleFocusChanged(bool hasFocus)
    {
        _services.Logger.Log(LogLevel.Info, "LIFECYCLE", $"Game HandleFocusChanged={hasFocus}");
        if (hasFocus)
        {
            _fullscreen.Apply(_activity, "Game-HandleFocusChanged");
        }
    }

    public void HandleDestroy()
    {
        _services.Logger.Log(LogLevel.Info, "LIFECYCLE", "Game HandleDestroy");
        _services.Logger.Flush();
    }

    private void RunBoot(BootPhase startPhase, bool isRetry)
    {
        _lastBootResult = _bootStateMachine.Execute(ExecuteBootPhase, startPhase, _services.Logger);

        if (_lastBootResult.Success)
        {
            _bootCompleted = true;
            _showErrorScreen = false;
            _bootMessage = "BOOT_OK";
            if (isRetry)
            {
                _services.Logger.Log(LogLevel.Info, "CONTENT", "CONTENT_OK_AFTER_RETRY");
            }
            return;
        }

        _bootCompleted = false;
        _showErrorScreen = true;
        _bootMessage = _lastBootResult.Message;

        if (_lastBootResult.FailedPhase == BootPhase.BootValidateContent &&
            _lastBootResult.PhaseResults.TryGetValue(BootPhase.BootValidateContent, out var validatePhaseResult) &&
            validatePhaseResult.Payload is ContentValidationReport report)
        {
            _lastContentReport = report;
        }
    }

    private BootPhaseResult ExecuteBootPhase(BootPhase phase)
    {
        switch (phase)
        {
            case BootPhase.BootInitLogger:
                _services.Logger.Log(LogLevel.Info, "APP", $"Boot session log file: {_services.Logger.CurrentSessionLogFile}");
                _services.Logger.Log(LogLevel.Info, "APP", $"ABI active: {_activeAbi}");
                return BootPhaseResult.Success("LOGGER_READY");

            case BootPhase.BootInitPaths:
                var layout = _services.Paths.EnsureDirectoryLayout();
                CelestePathBridge.Configure(
                    () => _services.Paths.ContentPath,
                    () => _services.Paths.SavePath,
                    () => _services.Paths.LogsPath,
                    (level, tag, message) =>
                    {
                        var mappedLevel = level switch
                        {
                            "ERROR" => LogLevel.Error,
                            "WARN" => LogLevel.Warn,
                            _ => LogLevel.Info
                        };

                        _services.Logger.Log(mappedLevel, tag, message);
                    });
                _services.Logger.Log(LogLevel.Info, "PATHS", $"BaseDataPath={_services.Paths.BaseDataPath}");
                _services.Logger.Log(LogLevel.Info, "PATHS", $"ContentPath={_services.Paths.ContentPath}");
                _services.Logger.Log(LogLevel.Info, "PATHS", $"LogsPath={_services.Paths.LogsPath}");
                _services.Logger.Log(LogLevel.Info, "PATHS", $"SavePath={_services.Paths.SavePath}");
                _services.Logger.Log(layout.Success ? LogLevel.Info : LogLevel.Error, "PATHS", layout.StatusCode, context: layout.Message);
                return layout.Success
                    ? BootPhaseResult.Success("PATHS_READY")
                    : BootPhaseResult.Failure("PATH_LAYOUT_FAILED");

            case BootPhase.BootApplyFullscreen:
                _fullscreen.Apply(_activity, "BootPhase-BootApplyFullscreen");
                return BootPhaseResult.Success("FULLSCREEN_APPLIED");

            case BootPhase.BootValidateContent:
                var report = _contentValidator.Validate();
                _lastContentReport = report;
                LogContentReport(report);

                return report.IsValid
                    ? BootPhaseResult.Success("CONTENT_OK", report)
                    : BootPhaseResult.Failure("CONTENT_INVALID", payload: report);

            case BootPhase.BootInitInput:
                _services.Input.Update();
                _services.Logger.Log(LogLevel.Info, "INPUT", _services.Input.CurrentInputSummary);
                return BootPhaseResult.Success("INPUT_READY");

            case BootPhase.BootInitAudio:
                try
                {
                    _services.Audio.Initialize();
                }
                catch (Exception exception)
                {
                    _services.Logger.Log(LogLevel.Warn, "AUDIO", "Audio initialization failed. Keeping controlled fallback", exception);
                    return BootPhaseResult.Success("AUDIO_FALLBACK_ACTIVE");
                }

                if (_services.Audio.IsInitialized)
                {
                    _services.Logger.Log(LogLevel.Info, "AUDIO", $"Audio backend initialized: {_services.Audio.BackendName}");
                    return BootPhaseResult.Success("AUDIO_READY");
                }

                _services.Logger.Log(LogLevel.Warn, "AUDIO", $"Audio backend not initialized: {_services.Audio.BackendName}; fallback policy active");
                return BootPhaseResult.Success("AUDIO_FALLBACK_ACTIVE");

            case BootPhase.BootStartGame:
                _services.Logger.Log(LogLevel.Info, "BOOT", "BootStartGame reached");
                return BootPhaseResult.Success("BOOT_START_GAME_OK");

            default:
                return BootPhaseResult.Failure($"Unsupported phase: {phase}");
        }
    }

    private void LogContentReport(ContentValidationReport report)
    {
        _services.Logger.Log(LogLevel.Info, "CONTENT", $"VALIDATION_STATUS={report.Status}; DIRS={report.ScannedDirectoryCount}; FILES={report.ScannedFileCount}");

        foreach (var issue in report.Issues)
        {
            var level = issue.Severity == IssueSeverity.Error ? LogLevel.Error : LogLevel.Warn;
            _services.Logger.Log(level, "CONTENT", $"{issue.Code} | {issue.Message}", context: $"relative={issue.RelativePath}; absolute={issue.AbsolutePath}; suggestion={issue.Suggestion}");
        }
    }

    private void IgnoreTouchInput()
    {
        var touches = TouchPanel.GetState();
        if (touches.Count == 0 || _touchIgnoredLogged)
        {
            return;
        }

        _touchIgnoredLogged = true;
        _services.Logger.Log(LogLevel.Warn, "INPUT", "Touch input detected and ignored by policy");
    }

    private void UpdateDiagnosticToggle(GameTime gameTime)
    {
        if (_services.Input.IsDiagnosticComboActive())
        {
            _diagHoldSeconds += gameTime.ElapsedGameTime.TotalSeconds;
            if (_diagHoldSeconds >= 2.0 && !_diagLatch)
            {
                _diagLatch = true;
                _diagnosticMode = !_diagnosticMode;
                _services.Logger.Log(LogLevel.Info, "DIAG", _diagnosticMode ? "Diagnostic overlay enabled" : "Diagnostic overlay disabled");
            }
        }
        else
        {
            _diagHoldSeconds = 0;
            _diagLatch = false;
        }
    }

    private void DrawBootingScreen()
    {
        var y = 40f;
        y = DrawLine("BOOTING CELESTE ANDROID PORT", 40, y, Color.White);
        y = DrawLine($"Status: {_bootMessage}", 40, y, Color.LightGray);
        y = DrawLine("Sem touch: use gamepad ou teclado.", 40, y, Color.Gray);
        DrawLine($"Log: {_services.Logger.CurrentSessionLogFile}", 40, y, Color.Gray);
    }

    private void DrawBootReadyScreen()
    {
        var y = 40f;
        y = DrawLine("BOOT NORMAL CONCLUIDO", 40, y, Color.LightGreen);
        y = DrawLine("Infra Android pronta: logger, paths, content validation, fullscreen.", 40, y, Color.White);
        y = DrawLine("Sem touch: use gamepad ou teclado.", 40, y, Color.LightGray);
        y = DrawLine("Proximo passo: integrar nucleo completo do jogo Celeste.", 40, y, Color.LightGray);
        DrawLine($"ABI ativa: {_activeAbi}", 40, y, Color.LightGray);
    }

    private void DrawErrorScreen()
    {
        var y = 28f;
        y = DrawLine("ARQUIVOS DO JOGO NAO ENCONTRADOS", 24, y, Color.OrangeRed, 1.05f);
        y = DrawLine("Copie os arquivos do jogo para:", 24, y, Color.White);
        y = DrawLine(_services.Paths.ContentPath, 24, y, Color.Yellow);

        y = DrawLine("", 24, y, Color.White);
        y = DrawLine("Faltantes detectados:", 24, y, Color.White);

        if (_lastContentReport is not null)
        {
            foreach (var issue in _lastContentReport.ErrorIssues.Take(7))
            {
                y = DrawLine($"- {issue.RelativePath} ({issue.Code})", 24, y, Color.LightPink);
            }

            var caseSuggestion = _lastContentReport.Issues.FirstOrDefault(item => item.Code == "CONTENT_CASE_MISMATCH");
            if (caseSuggestion is not null)
            {
                y = DrawLine($"Exemplo de correcao: {caseSuggestion.Suggestion}", 24, y, Color.LightGoldenrodYellow);
            }
        }
        else
        {
            y = DrawLine("- Nenhum detalhe adicional disponivel.", 24, y, Color.LightPink);
        }

        y = DrawLine("", 24, y, Color.White);
        y = DrawLine("Depois de copiar, feche e abra o app novamente.", 24, y, Color.White);
        y = DrawLine("Sem touch: use gamepad/teclado.", 24, y, Color.White);
        y = DrawLine("Pressione START/ENTER para tentar novamente.", 24, y, Color.White);
        y = DrawLine("Pressione BACK para sair.", 24, y, Color.White);
        DrawLine($"Ultimo log: {_services.Logger.CurrentSessionLogFile}", 24, y, Color.Gray);
    }

    private void DrawDiagnosticOverlay()
    {
        if (_spriteBatch is null || _pixel is null)
        {
            return;
        }

        var snapshot = BuildDiagnosticSnapshot();
        var box = new Rectangle(12, 12, GraphicsDevice.Viewport.Width - 24, 220);
        _spriteBatch.Draw(_pixel, box, new Color(0, 0, 0, 180));

        var y = 20f;
        y = DrawLine("DIAGNOSTICO DE BOOT", 20, y, Color.Cyan);
        y = DrawLine($"BaseDataPath: {snapshot.BaseDataPath}", 20, y, Color.White);
        y = DrawLine($"ContentPath: {snapshot.ContentPath}", 20, y, Color.White);
        y = DrawLine($"SavePath: {snapshot.SavePath}", 20, y, Color.White);
        y = DrawLine($"LogsPath: {snapshot.LogsPath}", 20, y, Color.White);
        y = DrawLine($"ContentStatus: {snapshot.ContentStatus} (errors={snapshot.ContentErrorCount}, warnings={snapshot.ContentWarningCount})", 20, y, Color.White);
        y = DrawLine($"ABI: {snapshot.ActiveAbi}", 20, y, Color.White);
        y = DrawLine($"Audio: {snapshot.AudioBackend} | initialized={snapshot.AudioInitialized}", 20, y, Color.White);
        y = DrawLine($"Input: {snapshot.InputSummary}", 20, y, Color.White);
        y = DrawLine(_gpuInfo, 20, y, Color.White);
        DrawLine($"Log: {snapshot.SessionLogFile}", 20, y, Color.White);
    }

    private DiagnosticSnapshot BuildDiagnosticSnapshot()
    {
        var report = _lastContentReport;
        return new DiagnosticSnapshot
        {
            BaseDataPath = _services.Paths.BaseDataPath,
            ContentPath = _services.Paths.ContentPath,
            LogsPath = _services.Paths.LogsPath,
            SavePath = _services.Paths.SavePath,
            ActiveAbi = _activeAbi,
            AudioBackend = _services.Audio.BackendName,
            AudioInitialized = _services.Audio.IsInitialized,
            InputSummary = _services.Input.CurrentInputSummary,
            ContentStatus = report?.Status ?? ContentValidationStatus.Missing,
            ContentErrorCount = report?.ErrorIssues.Count() ?? 0,
            ContentWarningCount = report?.WarningIssues.Count() ?? 0,
            SessionLogFile = _services.Logger.CurrentSessionLogFile
        };
    }

    private float DrawLine(string text, float x, float y, Color color, float scale = 1f)
    {
        if (_spriteBatch is null || _pixel is null)
        {
            return y;
        }

        if (_internalErrorFont is not null)
        {
            _spriteBatch.DrawString(_internalErrorFont, text, new Vector2(x, y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            return y + _internalErrorFont.LineSpacing * scale;
        }

        var fallbackScale = Math.Max(1f, 2f * scale);
        _fallbackFont.DrawString(_spriteBatch, _pixel, text, new Vector2(x, y), color, fallbackScale);
        return y + _fallbackFont.LineHeight(fallbackScale);
    }
}
