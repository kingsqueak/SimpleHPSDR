using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SimpleHPSDR.Views;

/// <summary>Help: a list of topics, and the chosen one beside it.</summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        Topics.ItemsSource = HelpContent.Topics.Select(t => t.Title).ToArray();
        Topics.SelectionChanged += (_, _) => Show(Topics.SelectedIndex);
        Topics.SelectedIndex = 0;
    }

    private void Show(int index)
    {
        if (index < 0) return;
        var (title, body) = HelpContent.Topics[index];
        Body.Children.Clear();
        Add(title, 20, FontWeight.SemiBold, 0);
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("## ")) Add(line[3..], 14, FontWeight.SemiBold, 10);
            else if (line.StartsWith("- ")) AddBullet(line[2..], "•");
            else if (line.Length > 2 && char.IsDigit(line[0]) && line[1] == '.') AddBullet(line[3..], line[..2]);
            else Add(line, 13, FontWeight.Normal, 2);
        }
        Scroller.Offset = default;
    }

    private void Add(string text, double size, FontWeight weight, double top) => Body.Children.Add(new TextBlock
    {
        Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
    });

    private void AddBullet(string text, string mark)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*") };
        row.Children.Add(new TextBlock { Text = mark, FontSize = 13, VerticalAlignment = VerticalAlignment.Top });
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 1);
        row.Children.Add(t);
        Body.Children.Add(row);
    }
}
