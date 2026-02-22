using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using discoteka_cli.Models;

namespace discoteka.ViewModels;

public sealed class TrackInfoDialogViewModel : ViewModelBase
{
    private TrackInfoTabViewModel? _selectedTab;
    private string _statusText = "Ready.";

    public TrackInfoDialogViewModel(TrackMetadataSnapshot snapshot)
    {
        TrackId = snapshot.TrackId;
        foreach (var tab in snapshot.Tabs)
        {
            Tabs.Add(new TrackInfoTabViewModel(tab));
        }

        SelectedTab = Tabs.FirstOrDefault();
    }

    public long TrackId { get; }
    public ObservableCollection<TrackInfoTabViewModel> Tabs { get; } = new();

    public TrackInfoTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public IEnumerable<MetadataTabEntry> ExportTabs()
    {
        return Tabs.Select(tab => tab.ToModel()).ToList();
    }
}

public sealed class TrackInfoTabViewModel : ViewModelBase
{
    public TrackInfoTabViewModel(MetadataTabEntry model)
    {
        Title = model.Title;
        TableName = model.TableName;
        KeyColumn = model.KeyColumn;
        KeyValue = model.KeyValue;
        foreach (var field in model.Fields)
        {
            Fields.Add(new TrackInfoFieldViewModel(field));
        }
    }

    public string Title { get; }
    public string TableName { get; }
    public string KeyColumn { get; }
    public string? KeyValue { get; }
    public ObservableCollection<TrackInfoFieldViewModel> Fields { get; } = new();

    public MetadataTabEntry ToModel()
    {
        return new MetadataTabEntry
        {
            Title = Title,
            TableName = TableName,
            KeyColumn = KeyColumn,
            KeyValue = KeyValue,
            Fields = Fields.Select(field => field.ToModel()).ToList()
        };
    }
}

public sealed class TrackInfoFieldViewModel : ViewModelBase
{
    private string? _value;

    public TrackInfoFieldViewModel(MetadataFieldEntry model)
    {
        Name = model.Name;
        DeclaredType = model.DeclaredType;
        IsPrimaryKey = model.IsPrimaryKey;
        _value = model.Value;
    }

    public string Name { get; }
    public string DeclaredType { get; }
    public bool IsPrimaryKey { get; }

    public string? Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public MetadataFieldEntry ToModel()
    {
        return new MetadataFieldEntry
        {
            Name = Name,
            DeclaredType = DeclaredType,
            Value = Value,
            IsPrimaryKey = IsPrimaryKey
        };
    }
}
