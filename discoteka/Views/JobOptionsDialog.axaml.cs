using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace discoteka.Views;

public partial class JobOptionsDialog : Window
{
    private TextBlock _headerText = null!;
    private TextBlock _labelText = null!;
    private Slider _valueSlider = null!;
    private TextBlock _valueText = null!;

    public JobOptionsDialog()
    {
        InitializeComponent();
        _valueSlider.PropertyChanged += (_, args) =>
        {
            if (args.Property.Name == nameof(Slider.Value))
            {
                UpdatePercentLabel();
            }
        };
        UpdatePercentLabel();
    }

    public JobOptionsDialog(string header, string label) : this()
    {
        _headerText.Text = header;
        _labelText.Text = label;
    }

    public JobOptionsDialog(string header, string label, double min, double max, double value) : this(header, label)
    {
        _valueSlider.Minimum = min;
        _valueSlider.Maximum = max;
        _valueSlider.Value = value;
        UpdatePercentLabel();
    }

    private void OnRunClick(object? sender, RoutedEventArgs e)
    {
        Close(_valueSlider.Value);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void UpdatePercentLabel()
    {
        _valueText.Text = $"{(int)Math.Round(_valueSlider.Value)}%";
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _headerText = this.FindControl<TextBlock>("HeaderText") ?? throw new InvalidOperationException("HeaderText not found.");
        _labelText = this.FindControl<TextBlock>("LabelText") ?? throw new InvalidOperationException("LabelText not found.");
        _valueSlider = this.FindControl<Slider>("ValueSlider") ?? throw new InvalidOperationException("ValueSlider not found.");
        _valueText = this.FindControl<TextBlock>("ValueText") ?? throw new InvalidOperationException("ValueText not found.");
    }
}
