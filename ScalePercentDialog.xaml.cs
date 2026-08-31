using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using ClipToGif.Localization;

namespace ClipToGif;

public partial class ScalePercentDialog : Window
{
    private static readonly Regex Digits = new("[^0-9]+", RegexOptions.Compiled);

    public int Percent { get; private set; }

    public ScalePercentDialog(int initialPercent)
    {
        InitializeComponent();
        Percent = Math.Clamp(initialPercent, 1, 99);
        PercentBox.Text = Percent.ToString(CultureInfo.InvariantCulture);
        PercentBox.SelectAll();
        Loaded += (_, _) => PercentBox.Focus();
        DataObject.AddPastingHandler(PercentBox, OnPaste);
    }

    private void PercentBox_OnPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = Digits.IsMatch(e.Text);

    private void PercentBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
            e.Handled = true;
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text) &&
            e.DataObject.GetData(DataFormats.Text) is string text &&
            !Digits.IsMatch(text))
            return;

        e.CancelCommand();
    }

    private void PercentBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateOkEnabled();

    private void UpdateOkEnabled() =>
        OkButton.IsEnabled = TryReadPercent(out _);

    private bool TryReadPercent(out int percent)
    {
        percent = 0;
        return int.TryParse(PercentBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out percent)
               && percent is >= 1 and <= 99;
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadPercent(out var percent))
        {
            MessageBox.Show(this, Loc.Get("ScaleCustomInvalid"), Loc.Get("ScaleCustomTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            PercentBox.Focus();
            PercentBox.SelectAll();
            return;
        }

        Percent = percent;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
