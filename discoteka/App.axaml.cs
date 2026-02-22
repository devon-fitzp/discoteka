using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System;
using Avalonia.Markup.Xaml;
using discoteka.Playback;
using discoteka.ViewModels;
using discoteka.Views;
using discoteka_cli.Database;
using discoteka_cli.Jobs;

namespace discoteka;

public partial class App : Application
{
    private IBackgroundJobQueue? _jobQueue;
    private IMediaPlaybackService? _playbackService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Console.WriteLine("[Startup] Initializing application...");
            var dbPath = DatabaseInitializer.Initialize();
            Console.WriteLine($"[Startup] Database path: {dbPath}");
            _jobQueue = new BackgroundJobQueue();
            Console.WriteLine("[Startup] Background job queue ready.");
            try
            {
                _playbackService = new MediaPlaybackService();
                Console.WriteLine("[Startup] Media playback service initialized.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Media playback initialization failed: {ex.Message}");
                if (OperatingSystem.IsLinux())
                {
                    Console.Error.WriteLine(LibVlcNativeResolver.BuildLinuxDependencyHint());
                }
                _playbackService = null;
            }
            var importJobs = new LibraryImportJobs(_jobQueue);
            var viewModel = new MainWindowViewModel(importJobs, _jobQueue, null, _playbackService);
            Console.WriteLine("[Startup] Main window view model created.");

            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            Console.WriteLine("[Startup] Main window created.");

            _ = viewModel.InitializeAsync();
            Console.WriteLine("[Startup] Initial track load requested.");

            desktop.Exit += (_, _) =>
            {
                Console.WriteLine("[Shutdown] Application exiting, disposing services...");
                _jobQueue?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _playbackService?.Dispose();
                Console.WriteLine("[Shutdown] Cleanup complete.");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
