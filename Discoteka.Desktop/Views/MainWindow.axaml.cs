using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Discoteka.Desktop.ViewModels;
using System.Linq;
using System.Threading.Tasks;

namespace Discoteka.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var saved = ThemeService.LoadPreference();
        ThemeService.ApplyTheme(this, saved);
        UpdateActiveNavButton();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as IDisposable)?.Dispose();
    }

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

    private async void OnRebuildIndexClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        await ViewModel.QueueRebuildIndexAsync();
    }

    private void OnThemeMidnightClick(object? sender, RoutedEventArgs e) => ApplyAndSaveTheme("Midnight");
    private void OnThemeObsidianClick(object? sender, RoutedEventArgs e) => ApplyAndSaveTheme("Obsidian");
    private void OnThemeForestClick(object? sender, RoutedEventArgs e) => ApplyAndSaveTheme("Forest");
    private void OnThemeRoseClick(object? sender, RoutedEventArgs e) => ApplyAndSaveTheme("Rose");

    private void ApplyAndSaveTheme(string name)
    {
        var theme = ThemeDefinition.All.First(t => t.Name == name);
        ThemeService.ApplyTheme(this, theme);
        ThemeService.SavePreference(name);
    }

    private void UpdateActiveNavButton()
    {
        NavAllMusic.Classes.Set("active", ViewModel?.IsAllMusicView ?? true);
        NavArtists.Classes.Set("active", ViewModel?.IsArtistsView ?? false);
        NavAlbums.Classes.Set("active", ViewModel?.IsAlbumsView ?? false);
        // Playlist buttons manage their own IsSelected state via PlaylistItemViewModel.IsSelected
    }

    private async void OnPlaylistNavClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null || sender is not Button { DataContext: PlaylistItemViewModel playlist })
        {
            return;
        }

        ViewModel.ShowPlaylistView(playlist);
        NavAllMusic.Classes.Set("active", false);
        NavArtists.Classes.Set("active", false);
        NavAlbums.Classes.Set("active", false);
    }

    private void OnPlaylistTrackListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel == null || sender is not ListBox listBox)
        {
            return;
        }

        var result = ViewModel.Playlists.PlayFromIndex(listBox.SelectedIndex);
        if (!result.Started && result.UserError == "No local file!")
        {
            ViewModel.PostStatus("No local file!");
        }
    }

    private void OnSortTitleClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByTitle();
    private void OnSortArtistClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByArtist();
    private void OnSortAlbumClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByAlbum();
    private void OnSortTimeClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByTime();
    private void OnSortGenreClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByGenre();
    private void OnSortFormatsClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByFormats();
    private void OnSortPlaysClick(object? sender, RoutedEventArgs e) => ViewModel?.CycleSortByPlays();

    private void OnDefaultSortTitleClick(object? sender, RoutedEventArgs e) => ViewModel?.SetDefaultSort(DefaultSortOption.Title);
    private void OnDefaultSortArtistClick(object? sender, RoutedEventArgs e) => ViewModel?.SetDefaultSort(DefaultSortOption.Artist);
    private void OnDefaultSortRecentlyAddedClick(object? sender, RoutedEventArgs e) => ViewModel?.SetDefaultSort(DefaultSortOption.RecentlyAdded);
    private void OnAllMusicViewClick(object? sender, RoutedEventArgs e) { ViewModel?.ShowAllMusicView(); UpdateActiveNavButton(); }
    private void OnArtistsViewClick(object? sender, RoutedEventArgs e) { ViewModel?.ShowArtistsView(); UpdateActiveNavButton(); }
    private void OnAlbumsViewClick(object? sender, RoutedEventArgs e) { ViewModel?.ShowAlbumsView(); UpdateActiveNavButton(); }
    private void OnFilterAllTracksClick(object? sender, RoutedEventArgs e) => ViewModel?.ClearSmartFilter();
    private void OnFilterAvailableLocallyClick(object? sender, RoutedEventArgs e) => ViewModel?.ShowAvailableLocallyFilter();
    private void OnFilterNoLocalFileClick(object? sender, RoutedEventArgs e) => ViewModel?.ShowNoLocalFileFilter();

    private async void OnArtistAlbumTileClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null || sender is not Button { DataContext: AlbumGroupViewModel album })
        {
            return;
        }

        await ViewModel.ToggleArtistAlbumAsync(album);
    }

    private async void OnArtistAlbumPlayClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null || sender is not Button button)
        {
            return;
        }

        AlbumGroupViewModel? album = button.DataContext switch
        {
            AlbumGroupViewModel selectedAlbum => selectedAlbum,
            ArtistGroupViewModel artist => artist.SelectedAlbum,
            _ => null
        };

        if (album == null)
        {
            return;
        }

        var result = await ViewModel.PlayArtistAlbumAsync(album);
        if (!result.Started && result.UserError == "No local file!")
        {
            ViewModel.PostStatus("No local file!");
        }
    }

    private async void OnAlbumsGridAlbumClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null || sender is not Button { DataContext: AlbumBrowserItemViewModel album })
        {
            return;
        }

        await ViewModel.ToggleAlbumsViewAlbumAsync(album);
    }

    private async void OnAlbumsGridAlbumPlayClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ViewModel == null || sender is not Button { DataContext: AlbumBrowserItemViewModel album })
        {
            return;
        }

        var result = await ViewModel.PlayAlbumsViewAlbumAsync(album);
        if (!result.Started && result.UserError == "No local file!")
        {
            ViewModel.PostStatus("No local file!");
        }
    }

    private void OnAlbumsLoadMoreClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.LoadMoreAlbumsViewPage();
    }

    private void OnTrackListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel == null || sender is not ListBox listBox)
        {
            return;
        }

        if (!ViewModel.PlayTrackFromVisibleIndex(listBox.SelectedIndex, out var userError) && userError == "No local file!")
        {
            ViewModel.PostStatus("No local file!");
        }
    }

    private async void OnTrackGetInfoClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null || sender is not MenuItem menuItem)
        {
            return;
        }

        var row = menuItem.CommandParameter as TrackRowViewModel
                  ?? (menuItem.Parent as ContextMenu)?.PlacementTarget?.DataContext as TrackRowViewModel;
        if (row == null || row.TrackId <= 0)
        {
            return;
        }

        var snapshot = await ViewModel.GetTrackMetadataSnapshotAsync(row.TrackId);
        if (snapshot == null)
        {
            await ShowSimpleMessageAsync("Track metadata not found.");
            return;
        }

        var dialogVm = new TrackInfoDialogViewModel(snapshot);
        var dialog = new TrackInfoDialog(
            dialogVm,
            tab => ViewModel.SaveTrackMetadataTabAsync(tab));
        await dialog.ShowDialog(this);

        // Reload to reflect any edits in the visible library list.
        await ViewModel.InitializeAsync();
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var selectedIndex = this.FindControl<ListBox>("TrackList")?.SelectedIndex ?? 0;
        if (!ViewModel.TogglePlayPause(selectedIndex, out var userError) && userError == "No local file!")
        {
            ViewModel.PostStatus("No local file!");
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
