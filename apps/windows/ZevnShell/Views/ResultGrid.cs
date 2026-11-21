using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;
using uniffi.zevn_core;

namespace ZevnShell.Views;

/// <summary>Pabrik elemen baris untuk ItemsRepeater (baris dibuat hanya saat terlihat).</summary>
internal sealed class RowFactory(Func<int, UIElement> build) : IElementFactory
{
    public UIElement GetElement(ElementFactoryGetArgs args) => build((int)args.Data);
    public void RecycleElement(ElementFactoryRecycleArgs args) { }
}

/// <summary>Tabel hasil query: header tetap, baris tervirtualisasi, klik sel untuk menyalin nilai.</summary>
internal sealed class ResultGrid : Grid
{
    private const double RowHeight = 28;

    public ResultGrid(QueryResult r, ulong rowOffset = 0, string? highlightColumn = null)
    {
        var cols = r.Columns;
        var widths = new double[cols.Length];
        for (int c = 0; c < cols.Length; c++)
        {
            var len = cols[c].Length;
            foreach (var row in r.Rows.Take(200)) len = Math.Max(len, Math.Min(60, row.Length > c ? (row[c]?.Length ?? 4) : 0));
            widths[c] = Math.Clamp(len * 7.4 + 26, 70, 380);
            if (cols[c] == highlightColumn) widths[c] = 112;
        }
        const double numWidth = 54;
        var total = numWidth + widths.Sum();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Height = 32, Background = Theme.Ink(0.05) };
        header.Children.Add(Cell("#", numWidth, Theme.Text3, FontWeights.SemiBold, right: true));
        for (int c = 0; c < cols.Length; c++) header.Children.Add(Cell(cols[c], widths[c], Theme.Text, FontWeights.SemiBold));

        var copied = new Border
        {
            Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(12, 6, 12, 7), CornerRadius = new CornerRadius(9),
            Background = Theme.Dark ? Theme.Brush("#17191E", 0.98) : Theme.Brush("#FFFFFF"), BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1)
        };

        UIElement BuildRow(int i)
        {
            var row = r.Rows[i];
            var p = new StackPanel { Orientation = Orientation.Horizontal, Height = RowHeight, Background = i % 2 == 1 ? Theme.Ink(0.025) : new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            p.Children.Add(Cell((rowOffset + (ulong)i + 1).ToString(), numWidth, Theme.Text3, null, right: true, mono: true));
            for (int c = 0; c < cols.Length; c++)
            {
                var v = c < row.Length ? row[c] : null;
                FrameworkElement cell;
                if (cols[c] == highlightColumn && v is { Length: > 0 })
                {
                    var color = v.StartsWith("PRI") ? "#F59E0B" : v.StartsWith("UNI") ? Theme.Accent3 : Theme.Accent2;
                    var label = v.StartsWith("PRI") ? "PRIMARY" : v.StartsWith("UNI") ? "UNIQUE" : "INDEX";
                    cell = new Border { Width = widths[c], Padding = new Thickness(8, 0, 8, 0), Child = Ui.Badge(label, color).With(b => b.HorizontalAlignment = HorizontalAlignment.Left) };
                }
                else if (v == null)
                    cell = Cell("NULL", widths[c], Theme.Text3, null, italic: true);
                else
                    cell = Cell(v.Replace('\n', ' ').Replace('\r', ' '), widths[c], Theme.Text, null, mono: true);
                var value = v;
                cell.Tapped += (_, _) =>
                {
                    if (value == null) return;
                    Ui.CopyText(value);
                    copied.Child = Ui.Text("Disalin: " + (value.Length > 40 ? value[..40] + "…" : value), 12);
                    copied.Visibility = Visibility.Visible;
                    Ui.StaggerIn(copied, 0);
                    var stamp = value;
                    _ = System.Threading.Tasks.Task.Delay(1400).ContinueWith(_ => copied.DispatcherQueue.TryEnqueue(() => { if (copied.Child is TextBlock t && t.Text.Contains(stamp.Length > 40 ? stamp[..40] : stamp)) copied.Visibility = Visibility.Collapsed; }));
                };
                if (v is { Length: > 60 }) ToolTipService.SetToolTip(cell, v.Length > 2000 ? v[..2000] + "…" : v);
                p.Children.Add(cell);
            }
            return p;
        }

        var repeater = new ItemsRepeater
        {
            ItemsSource = Enumerable.Range(0, r.Rows.Length).ToList(),
            ItemTemplate = new RowFactory(BuildRow),
            Layout = new StackLayout { Orientation = Orientation.Vertical }
        };
        var body = new ScrollViewer { Content = repeater, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var inner = Ui.Rows("Auto,*", 0, header, body);
        inner.Width = total;
        inner.HorizontalAlignment = HorizontalAlignment.Left;
        var outer = new ScrollViewer
        {
            Content = inner, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var frame = new Border
        {
            Child = outer, CornerRadius = new CornerRadius(10), BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1),
            Background = Theme.Card
        };
        Children.Add(frame);
        if (cols.Length == 0)
        {
            frame.Child = Ui.Secondary(r.Affected is { } a ? $"Berhasil — {a} baris terpengaruh" : "Perintah berhasil dijalankan").With(t => { t.Margin = new Thickness(16); });
        }
        else if (r.Rows.Length == 0)
        {
            Children.Add(Ui.Secondary("0 baris").With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }));
        }
        Children.Add(copied);
    }

    private static TextBlock Cell(string text, double width, Brush fg, FontWeight? weight, bool right = false, bool mono = false, bool italic = false) => new()
    {
        Text = text, Width = width, Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        Foreground = fg, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, FontFamily = mono ? Theme.MonoFont : new FontFamily("Segoe UI Variable Text"),
        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal
    };
}
