using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace SpokaSoundBoard.Views;

public partial class SliderRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SliderRow), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SliderRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(SliderRow),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SliderRow)d).UpdateText()));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SliderRow), new PropertyMetadata(0d));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SliderRow), new PropertyMetadata(1d));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(SliderRow), new PropertyMetadata(0.05d));

    /// <summary>Composite format, e.g. "{0:0} Hz". Use "%" for a percentage of a 0..1 value.</summary>
    public static readonly DependencyProperty FormatProperty =
        DependencyProperty.Register(nameof(Format), typeof(string), typeof(SliderRow),
            new PropertyMetadata("{0:0.00}", (d, _) => ((SliderRow)d).UpdateText()));

    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(nameof(DisplayText), typeof(string), typeof(SliderRow), new PropertyMetadata(""));

    public SliderRow()
    {
        InitializeComponent();
        UpdateText();
    }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string DisplayText { get => (string)GetValue(DisplayTextProperty); private set => SetValue(DisplayTextProperty, value); }

    private void UpdateText()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        DisplayText = Format switch
        {
            "%" => (Value * 100).ToString("0", culture) + " %",
            "dB" => (Value > 0.05 ? "+" : "") + Value.ToString("0.0", culture) + " dB",
            "st" => (Value > 0.05 ? "+" : "") + Value.ToString("0.0", culture) + " demi-tons",
            _ => string.Format(culture, Format, Value),
        };
    }
}
