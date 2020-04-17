﻿using NitroSharp.Media;
using NitroSharp.Text;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Veldrid;
using Veldrid.StartupUtilities;
using System.Runtime.InteropServices;
using System.Text;
using NitroSharp.Content;
using NitroSharp.Graphics;
using NitroSharp.NsScript.Compiler;
using NitroSharp.NsScript.VM;

#nullable enable

namespace NitroSharp
{
    internal readonly struct FrameStamp
    {
        public readonly long FrameId;
        public readonly long StopwatchTicks;

        public FrameStamp(long frameId, long stopwatchTicks)
            => (FrameId, StopwatchTicks) = (frameId, stopwatchTicks);
    }

    internal sealed class Context
    {
        public World World { get; }
        public ContentManager Content { get; }
        public GlyphRasterizer GlyphRasterizer { get; }
        public FontConfiguration FontConfig { get; }
        public RenderContext RenderContext { get; }

        public Context(
            World world,
            ContentManager content,
            GlyphRasterizer glyphRasterizer,
            FontConfiguration fontConfig,
            RenderContext renderContext)
        {
            World = world;
            Content = content;
            GlyphRasterizer = glyphRasterizer;
            FontConfig = fontConfig;
            RenderContext = renderContext;
        }
    }

    public partial class Game : IDisposable
    {
        private readonly bool UseWicOnWindows = true;

        private readonly Stopwatch _gameTimer;
        private readonly Configuration _configuration;
        private readonly Logger _logger;
        private readonly LogEventRecorder _logEventRecorder;
        private readonly CancellationTokenSource _shutdownCancellation;

        private readonly GameWindow _window;
        private volatile bool _needsResize;
        private volatile bool _surfaceDestroyed;
        private GraphicsDevice? _graphicsDevice;
        private Swapchain? _swapchain;
        private readonly TaskCompletionSource<int> _initializingGraphics;
        private RenderSystem? _renderSystem;
        private readonly GlyphRasterizer _glyphRasterizer;
        private FontConfiguration? _fontConfig;

        private ContentManager? _content;
        private readonly InputTracker _inputTracker;

        private AudioDevice? _audioDevice;
        private AudioSourcePool? _audioSourcePool;

        private readonly World _world;
        private readonly string _nssFolder;
        private readonly string _bytecodeCacheDir;
        private NsScriptVM? _vm;
        private Builtins _builtinFunctions;

        public Game(GameWindow window, Configuration configuration)
        {
            _shutdownCancellation = new CancellationTokenSource();
            _initializingGraphics = new TaskCompletionSource<int>();
            _configuration = configuration;
            (_logger, _logEventRecorder) = SetupLogging();
            _glyphRasterizer = new GlyphRasterizer(enableOutlines: true);
            _window = window;
            _window.Mobile_SurfaceCreated += OnSurfaceCreated;
            _window.Resized += () => _needsResize = true;
            _window.Mobile_SurfaceDestroyed += () => _surfaceDestroyed = true;

            _gameTimer = new Stopwatch();
            _world = new World();
            _inputTracker = new InputTracker(window);

            _nssFolder = Path.Combine(_configuration.ContentRoot, "nss");
            _bytecodeCacheDir = _nssFolder.Replace("nss", "nsx");
        }

        internal AudioDevice AudioDevice => _audioDevice!;
        internal AudioSourcePool AudioSourcePool => _audioSourcePool!;

        internal Logger Logger => _logger;
        internal LogEventRecorder LogEventRecorder => _logEventRecorder;

        public Task Run(bool useDedicatedThread = false)
        {
            // Blocking the thread here because desktop platforms
            // require that the main loop be run on the main thread.
            // useDedicatedThread is only used by the Android verison
            // at this point.
            try
            {
                Initialize().Wait();
            }
            catch (AggregateException aex)
            {
                throw aex.Flatten();
            }

            var context = new Context(_world, _content!, _glyphRasterizer, _fontConfig!, _renderSystem!.Context);
            _builtinFunctions = new Builtins(context);
            return RunMainLoop(useDedicatedThread);
        }

        private async Task Initialize()
        {
            var initializeAudio = Task.Run(SetupAudio);
            SetupFonts();
            await Task.WhenAll(_initializingGraphics.Task, initializeAudio);
            _content = CreateContentManager();
            await Task.Run(LoadStartupScript);
        }

        private static (Logger, LogEventRecorder) SetupLogging()
        {
            var recorder = new LogEventRecorder();
            Logger logger = new LoggerConfiguration()
                .WithSink(recorder)
                .CreateLogger();
            return (logger, recorder);
        }

        private void SetupFonts()
        {
            _glyphRasterizer.AddFonts(Directory.EnumerateFiles("Fonts"));
            var defaultFont = new FontKey(_configuration.FontFamily, FontStyle.Regular);
            _fontConfig = new FontConfiguration(
                defaultFont,
                italicFont: null,
                new PtFontSize(_configuration.FontSize),
                defaultTextColor: RgbaFloat.White,
                defaultOutlineColor: RgbaFloat.Black,
                rubyFontSizeMultiplier: 0.4f
            );
        }

        private void LoadStartupScript()
        {
            const string globalsFileName = "_globals";
            string globalsPath = Path.Combine(_bytecodeCacheDir, globalsFileName);
            if (_configuration.SkipUpToDateCheck || !File.Exists(globalsPath) || !ValidateBytecodeCache())
            {
                if (!Directory.Exists(_bytecodeCacheDir))
                {
                    Directory.CreateDirectory(_bytecodeCacheDir);
                    _logger.LogInformation("Bytecode cache is empty. Compiling the scripts...");
                }
                else
                {
                    _logger.LogInformation("Bytecode cache is not up-to-date. Recompiling the scripts...");
                    foreach (string file in Directory
                        .EnumerateFiles(_bytecodeCacheDir, "*.nsx", SearchOption.AllDirectories))
                    {
                        File.Delete(file);
                    }
                }

                Encoding? sourceEncoding = _configuration.UseUtf8 ? Encoding.UTF8 : null;
                var compilation = new Compilation(
                    _nssFolder,
                    _bytecodeCacheDir,
                    globalsFileName,
                    sourceEncoding
                );
                compilation.Emit(compilation.GetSourceModule(_configuration.StartupScript));
            }
            else
            {
                _logger.LogInformation("Bytecode cache is up-to-date.");
            }

            var nsxLocator = new FileSystemNsxModuleLocator(_bytecodeCacheDir);
            _vm = new NsScriptVM(nsxLocator, File.OpenRead(globalsPath));
            _vm.CreateThread(
                name: "__MAIN",
                Path.ChangeExtension(_configuration.StartupScript, null),
                symbol: "main"
            );
        }

        private bool ValidateBytecodeCache()
        {
            static string getModulePath(string rootDir, string fullPath)
            {
                return Path.ChangeExtension(
                    Path.GetRelativePath(rootDir, fullPath),
                    extension: null
                );
            }

            string startupModule = getModulePath(
                rootDir: _nssFolder,
                Path.Combine(_nssFolder, _configuration.StartupScript)
            );
            foreach (string nssFile in Directory
                .EnumerateFiles(_nssFolder, "*.nss", SearchOption.AllDirectories))
            {
                string currentModule = getModulePath(rootDir: _nssFolder, nssFile);
                string nsxPath = Path.ChangeExtension(
                    Path.Combine(_bytecodeCacheDir, currentModule),
                    "nsx"
                );
                try
                {
                    using (FileStream nsxStream = File.OpenRead(nsxPath))
                    {
                        long nsxTimestamp = NsxModule.GetSourceModificationTime(nsxStream);
                        long nssTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(nssFile))
                            .ToUnixTimeSeconds();
                        if (nsxTimestamp != nssTimestamp)
                        {
                            return false;
                        }
                    }

                }
                catch
                {
                    if (currentModule.Equals(startupModule, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private Task RunMainLoop(bool useDedicatedThread = false)
        {
            if (useDedicatedThread)
            {
                return Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning);
            }
            try
            {
                MainLoop();
            }
            catch (TaskCanceledException)
            {
            }
            return Task.FromResult(0);
        }

        private void MainLoop()
        {
            _gameTimer.Start();

            long prevFrameTicks = 0L;
            long frameId = 0L;
            while (!_shutdownCancellation.IsCancellationRequested && _window.Exists)
            {
                if (_surfaceDestroyed)
                {
                    HandleSurfaceDestroyed();
                    return;
                }
                Debug.Assert(_swapchain != null);
                if (_needsResize)
                {
                    Size newSize = _window.Size;
                    _swapchain.Resize(newSize.Width, newSize.Height);
                    _needsResize = false;
                }

                long currentFrameTicks = _gameTimer.ElapsedTicks;
                float deltaMilliseconds = (float)(currentFrameTicks - prevFrameTicks)
                    / Stopwatch.Frequency * 1000.0f;
                prevFrameTicks = currentFrameTicks;

                try
                {
                    var framestamp = new FrameStamp(frameId++, _gameTimer.ElapsedTicks);
                    Tick(framestamp, deltaMilliseconds);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private void Tick(FrameStamp framestamp, float deltaMilliseconds)
        {
            Debug.Assert(_renderSystem != null);
            Debug.Assert(_content != null);
            if (_content.ResolveAssets())
            {
                _world.BeginFrame();

                Debug.Assert(_vm != null);
                _vm.RefreshThreadState();
                _vm.ProcessPendingThreadActions();
                _vm.Run(_builtinFunctions, _shutdownCancellation.Token);
            }

            _inputTracker.Update();
            _world.FlushDetachedAnimations();
            try
            {
                _renderSystem.Render(_world, framestamp);
            }
            catch (VeldridException e)
                when (e.Message == "The Swapchain's underlying surface has been lost.")
            {
            }
        }

        private void OnSurfaceCreated(SwapchainSource swapchainSource)
        {
            bool resuming = _initializingGraphics.Task.IsCompleted;
            SetupGraphics(swapchainSource);
            Debug.Assert(_graphicsDevice != null);
            _initializingGraphics.TrySetResult(0);

            if (resuming)
            {
                if (_graphicsDevice.BackendType == GraphicsBackend.OpenGLES)
                {
                    // TODO (Android): recreate all device resources
                }

                RunMainLoop(true);
            }
        }

        private void SetupGraphics(SwapchainSource swapchainSource)
        {
            var options = new GraphicsDeviceOptions(false, null, _configuration.EnableVSync);
            options.PreferStandardClipSpaceYDirection = true;
#if DEBUG
            options.Debug = true;
#endif
            GraphicsBackend backend = _configuration.PreferredGraphicsBackend
                ?? VeldridStartup.GetPlatformDefaultBackend();
            var swapchainDesc = new SwapchainDescription(swapchainSource,
                (uint)_configuration.WindowWidth, (uint)_configuration.WindowHeight,
                options.SwapchainDepthFormat, options.SyncToVerticalBlank);

            if (backend == GraphicsBackend.OpenGLES || backend == GraphicsBackend.OpenGL)
            {
                _graphicsDevice = _window is DesktopWindow desktopWindow
                    ? VeldridStartup.CreateDefaultOpenGLGraphicsDevice(options, desktopWindow.SdlWindow, backend)
                    : GraphicsDevice.CreateOpenGLES(options, swapchainDesc);
                _swapchain = _graphicsDevice.MainSwapchain;
            }
            else
            {
                if (_graphicsDevice == null)
                {
                    _graphicsDevice = backend switch
                    {
                        GraphicsBackend.Direct3D11 => GraphicsDevice.CreateD3D11(options),
                        GraphicsBackend.Vulkan => GraphicsDevice.CreateVulkan(options),
                        GraphicsBackend.Metal => GraphicsDevice.CreateMetal(options),
                        _ => ThrowHelper.Unreachable<GraphicsDevice>()
                    };
                }
                _swapchain = _graphicsDevice.ResourceFactory.CreateSwapchain(ref swapchainDesc);
            }

            _renderSystem = new RenderSystem(
                _configuration,
                _graphicsDevice,
                _swapchain,
                _glyphRasterizer,
                _content!
            );
        }

        private void SetupAudio()
        {
            var audioParameters = AudioParameters.Default;
            AudioBackend backend = _configuration.PreferredAudioBackend
                ?? AudioDevice.GetPlatformDefaultBackend();
            _audioDevice = AudioDevice.Create(backend, audioParameters);
            _audioSourcePool = new AudioSourcePool(_audioDevice);
        }

        private ContentManager CreateContentManager()
        {
            Debug.Assert(_graphicsDevice != null);
            Debug.Assert(_audioDevice != null);
            TextureLoader textureLoader;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && UseWicOnWindows)
            {
                textureLoader = new WicTextureLoader(_graphicsDevice);
            }
            else
            {
                textureLoader = null!;
                //textureLoader = new FFmpegTextureLoader(_graphicsDevice);
            }

            var content = new ContentManager(
                _configuration.ContentRoot,
                _graphicsDevice,
                textureLoader
            );
            return content;
        }

        private void HandleSurfaceDestroyed()
        {
            Debug.Assert(_graphicsDevice != null);
            Debug.Assert(_swapchain != null);
            if (_graphicsDevice.BackendType == GraphicsBackend.OpenGLES)
            {
                // TODO (Android): destroy all device resources
                _graphicsDevice.Dispose();
                _graphicsDevice = null;
                _swapchain = null;
            }
            else
            {
                _swapchain.Dispose();
                _swapchain = null;
            }

            _surfaceDestroyed = false;
            _window.Mobile_HandledSurfaceDestroyed.Set();
        }

        public void Exit()
        {
            _shutdownCancellation.Cancel();
        }

        public void Dispose()
        {
            _renderSystem?.Dispose();
            _content?.Dispose();
            _glyphRasterizer.Dispose();
            _swapchain?.Dispose();
            _graphicsDevice?.Dispose();
            _audioSourcePool?.Dispose();
            _audioDevice?.Dispose();
        }
    }
}
