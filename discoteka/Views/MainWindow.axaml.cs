using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using discoteka.ViewModels;
using System.Linq;
using System.Threading.Tasks;

namespace discoteka.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnImportAppleMusicClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            AllowMultiple = false,
            Filters =
            {
                new FileDialogFilter { Name = "Apple Music or Rekordbox XML", Extensions = { "xml" } },
                new FileDialogFilter { Name = "All Files", Extensions = { "*" } }
            }
        };

        var result = await dialog.ShowAsync(this);
        var path = result?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await ViewModel.QueueAppleMusicImportAsync(path);
    }

    private async void OnScanMediaClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var dialog = new OpenFolderDialog();
        var path = await dialog.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await ViewModel.QueueMediaScanAsync(path);
    }

    private async void OnRunCleanupClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var dialog = new JobOptionsDialog("Cleanup Threshold", "Minimum confidence");
        var result = await dialog.ShowDialog<double?>(this);
        if (!result.HasValue)
        {
            return;
        }

        await ViewModel.QueueCleanupAsync(result.Value / 100.0);
    }

    private async void OnRunMatchRescanClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var dialog = new JobOptionsDialog("Match Threshold", "Minimum score");
        var result = await dialog.ShowDialog<double?>(this);
        if (!result.HasValue)
        {
            return;
        }

        await ViewModel.QueueMatchRescanAsync(result.Value / 100.0);
    }

    private void OnSortTitleClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByTitle();
    private void OnSortArtistClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByArtist();
    private void OnSortAlbumClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByAlbum();
    private void OnSortTimeClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByTime();
    private void OnSortGenreClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByGenre();
    private void OnSortFormatsClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByFormats();
    private void OnSortPlaysClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByPlays();

    private void OnDefaultSortTitleClick(object? sender, RoutedEventArgs e) => ViewModel?.SetDefaultSort(MainWindowViewModel.DefaultSortOption.Title);
    private void OnDefaultSortArtistClick(object? sender, RoutedEventArgs e) => ViewModel?.SetDefaultSort(MainWindowViewModel.DefaultSortOption.Artist);
    private void OnDefaultSortRecentlyAddedClick(object? sender, RoutedEventArgs e) => ViewModel?.SetDefaultSort(MainWindowViewModel.DefaultSortOption.RecentlyAdded);
    private void OnAllMusicViewClick(object? sender, RoutedEventArgs e) => ViewModel?.ShowAllMusicView();
    private void OnArtistsViewClick(object? sender, RoutedEventArgs e) => ViewModel?.ShowArtistsView();
    private void OnAlbumsViewClick(object? sender, RoutedEventArgs e) => ViewModel?.ShowAlbumsView();
    private void OnFilterAllTracksClick(object? sender, RoutedEventArgs e) => ViewModel?.ClearSmartFilter();
    private void OnFilterAvailableLocallyClick(object? sender, RoutedEventArgs e) => ViewModel?.ShowAvailableLocallyFilter();
    private void OnFilterNoLocalFileClick(object? sender, RoutedEventArgs e) => ViewModel?.ShowNoLocalFileFilter();

    private async void OnTrackListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel == null || sender is not ListBox listBox)
        {
            return;
        }

        if (!ViewModel.PlayTrackFromVisibleIndex(listBox.SelectedIndex, out var userError) && userError == "No local file!")
        {
            await ShowSimpleMessageAsync("No local file!");
        }
    }

    private async void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var selectedIndex = this.FindControl<ListBox>("TrackList")?.SelectedIndex ?? 0;
        if (!ViewModel.TogglePlayPause(selectedIndex, out var userError) && userError == "No local file!")
        {
            await ShowSimpleMessageAsync("No local file!");
        }
    }

    private void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.PlayPrevious();
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.PlayNext();
    }

    private void OnShuffleClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleShuffle();
    }

    private void OnRepeatClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CycleRepeatMode();
    }

    private void OnProgressPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ViewModel == null || sender is not Slider slider)
        {
            return;
        }

        ViewModel.SeekToSeconds(slider.Value);
    }

    private async Task ShowSimpleMessageAsync(string message)
    {
        var dialog = new Window
        {
            Title = message,
            Width = 300,
            Height = 140,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var okButton = new Button
        {
            Content = "OK",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        okButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                okButton
            }
        };

        await dialog.ShowDialog(this);
    }
}
