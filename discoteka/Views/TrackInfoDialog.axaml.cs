using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using discoteka.ViewModels;

namespace discoteka.Views;

public partial class TrackInfoDialog : Window
{
    private readonly Func<discoteka_cli.Models.MetadataTabEntry, Task> _saveTabAsync;

    public TrackInfoDialog()
    {
        _saveTabAsync = _ => Task.CompletedTask;
        InitializeComponent();
    }

    public TrackInfoDialog(
        TrackInfoDialogViewModel viewModel,
        Func<discoteka_cli.Models.MetadataTabEntry, Task> saveTabAsync) : this()
    {
        DataContext = viewModel;
        _saveTabAsync = saveTabAsync;
    }

    private TrackInfoDialogViewModel? ViewModel => DataContext as TrackInfoDialogViewModel;

    private async void OnSaveCurrentClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedTab == null)
        {
            return;
        }

        try
        {
            ViewModel.StatusText = $"Saving {ViewModel.SelectedTab.Title}...";
            await _saveTabAsync(ViewModel.SelectedTab.ToModel());
            ViewModel.StatusText = $"Saved {ViewModel.SelectedTab.Title}.";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Save failed: {ex.Message}";
        }
    }

    private async void OnSaveAllClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        try
        {
            ViewModel.StatusText = "Saving all tabs...";
            foreach (var tab in ViewModel.Tabs.ToList())
            {
                await _saveTabAsync(tab.ToModel());
            }

            ViewModel.StatusText = "Saved all tabs.";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Save failed: {ex.Message}";
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
