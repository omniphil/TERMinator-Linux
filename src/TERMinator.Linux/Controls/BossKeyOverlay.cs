using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TERMinator.Linux.Controls;

/// <summary>
/// The boss key screen: a full-window, perfectly boring spreadsheet (a quarterly budget in a spreadsheet look-alike).
/// Pause/Break or Ctrl+Shift+B shows it; any key hides it again. MainWindow swallows that key, so nothing typed
/// reaches the board, and its white background blocks the mouse. Same layout and numbers as the Windows client.
/// </summary>
public sealed class BossKeyOverlay : Grid
{
    public const string FakeTitle = "Q3 Budget Forecast.xlsx - Excel";

    private sealed record Cell(string Text, bool Right = false, bool Bold = false, IBrush? Foreground = null,
                               IBrush? Fill = null, double Size = 14.5, int Span = 1);

    private static readonly IBrush White = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush Green = Solid(0x21, 0x73, 0x46);
    private static readonly IBrush DarkGreen = Solid(0x1A, 0x5C, 0x38);
    private static readonly IBrush Chrome = Solid(0xF3, 0xF3, 0xF3);
    private static readonly IBrush ChromeText = Solid(0x44, 0x44, 0x44);
    private static readonly IBrush GridLine = Solid(0xE1, 0xE1, 0xE1);
    private static readonly IBrush HeaderLine = Solid(0xC6, 0xC6, 0xC6);
    private static readonly IBrush CellText = Solid(0x22, 0x22, 0x22);
    private static readonly IBrush Negative = Solid(0xC0, 0x00, 0x00);
    private static readonly IBrush HeaderFill = Solid(0xDD, 0xEB, 0xF7);
    private static readonly IBrush LightText = Solid(0xDD, 0xEE, 0xE4);

    private const int SheetRows = 48;
    private const int ActiveRow = 18;
    private const int ActiveColumn = 4; // E
    private static readonly double[] ColumnWidths = [190, 96, 96, 96, 104, 104, 96, 90, 72, 72, 72, 72, 72, 72];

    private readonly long _activeValue;

    public BossKeyOverlay()
    {
        Background = White;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
        ClipToBounds = true;

        var cells = BuildBudget(out _activeValue);

        RowDefinitions.Add(new RowDefinition(new GridLength(32)));                    // title bar
        RowDefinitions.Add(new RowDefinition(new GridLength(30)));                    // ribbon tabs
        RowDefinitions.Add(new RowDefinition(new GridLength(66)));                    // ribbon
        RowDefinitions.Add(new RowDefinition(new GridLength(30)));                    // formula bar
        RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));  // sheet
        RowDefinitions.Add(new RowDefinition(new GridLength(28)));                    // sheet tabs
        RowDefinitions.Add(new RowDefinition(new GridLength(24)));                    // status bar

        Place(BuildTitleBar(), 0);
        Place(BuildTabs(), 1);
        Place(BuildRibbon(), 2);
        Place(BuildFormulaBar(), 3);
        Place(new Border { ClipToBounds = true, Child = BuildSheet(cells) }, 4);
        Place(BuildSheetTabs(), 5);
        Place(BuildStatusBar(), 6);
    }

    private void Place(Control element, int row)
    {
        SetRow(element, row);
        Children.Add(element);
    }

    // ================================================================ chrome

    private static Control BuildTitleBar()
    {
        var bar = new DockPanel { Background = Green };

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        left.Children.Add(Label("AutoSave", 12, White));
        left.Children.Add(new Border
        {
            Width = 30,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderBrush = White,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0)
            }
        });
        left.Children.Add(Label("Q3 Budget Forecast.xlsx  ·  Saved", 12, White));
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);

        bar.Children.Add(new Border
        {
            Background = DarkGreen,
            CornerRadius = new CornerRadius(4),
            Width = 320,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = Label("   Search", 12, LightText)
        });
        return bar;
    }

    private static Control BuildTabs()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Background = Chrome };
        tabs.Children.Add(new Border { Background = Green, Padding = new Thickness(14, 0, 14, 0), Child = Label("File", 13, White) });
        foreach (var name in new[] { "Home", "Insert", "Page Layout", "Formulas", "Data", "Review", "View", "Help" })
        {
            bool active = name == "Home";
            tabs.Children.Add(new Border
            {
                Padding = new Thickness(12, 0, 12, 0),
                BorderBrush = Green,
                BorderThickness = new Thickness(0, 0, 0, active ? 3 : 0),
                Child = Label(name, 13, active ? Green : ChromeText, bold: active)
            });
        }
        return tabs;
    }

    private static Control BuildRibbon()
    {
        var ribbon = new StackPanel { Orientation = Orientation.Horizontal, Background = Chrome };
        (string Top, string Bottom, string Caption)[] groups =
        [
            ("Paste ▾", "Cut  ·  Copy  ·  Format Painter", "Clipboard"),
            ("Calibri          ▾   11 ▾", "B    I    U ▾    A ▾", "Font"),
            ("≡   ≡   ≡      Wrap Text", "Merge & Center ▾", "Alignment"),
            ("Currency            ▾", "$ ▾    %    ,    .0   .00", "Number"),
            ("Conditional Formatting ▾", "Format as Table ▾   Cell Styles ▾", "Styles"),
            ("Insert ▾   Delete ▾", "Format ▾", "Cells"),
            ("Σ AutoSum ▾    Sort & Filter ▾", "Fill ▾    Clear ▾    Find & Select ▾", "Editing")
        ];
        foreach (var group in groups)
        {
            var panel = new StackPanel { Margin = new Thickness(12, 5, 12, 2) };
            panel.Children.Add(Label(group.Top, 12, CellText));
            var bottom = Label(group.Bottom, 12, CellText);
            bottom.Margin = new Thickness(0, 3, 0, 0);
            panel.Children.Add(bottom);
            var caption = Label(group.Caption, 11);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Margin = new Thickness(0, 5, 0, 0);
            panel.Children.Add(caption);
            ribbon.Children.Add(panel);
            ribbon.Children.Add(new Border { Width = 1, Background = HeaderLine, Margin = new Thickness(0, 8, 0, 8) });
        }
        return ribbon;
    }

    private static Control BuildFormulaBar()
    {
        var bar = new DockPanel { Background = White };
        var nameBox = new Border
        {
            Width = 90,
            BorderBrush = HeaderLine,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = Label($"  {(char)('A' + ActiveColumn)}{ActiveRow}", 12, CellText)
        };
        DockPanel.SetDock(nameBox, Dock.Left);
        bar.Children.Add(nameBox);

        var fx = new Border
        {
            BorderBrush = HeaderLine,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(14, 0, 14, 0),
            Child = Label("✕    ✓    fx", 12)
        };
        DockPanel.SetDock(fx, Dock.Left);
        bar.Children.Add(fx);

        bar.Children.Add(new Border
        {
            BorderBrush = HeaderLine,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 0, 0, 0),
            Child = Label("=SUM(E5:E16)", 13, CellText)
        });
        return bar;
    }

    private static Control BuildSheetTabs()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Background = Chrome };
        tabs.Children.Add(new Border { Padding = new Thickness(12, 0, 16, 0), Child = Label("◀   ▶", 11) });
        foreach (var name in new[] { "Summary", "Q3 Detail", "Assumptions" })
        {
            bool active = name == "Q3 Detail";
            tabs.Children.Add(new Border
            {
                Background = active ? White : Chrome,
                BorderBrush = active ? Green : HeaderLine,
                BorderThickness = active ? new Thickness(0, 0, 0, 3) : new Thickness(0, 0, 1, 0),
                Padding = new Thickness(16, 0, 16, 0),
                Child = Label(name, 12, active ? Green : ChromeText, bold: active)
            });
        }
        tabs.Children.Add(new Border { Padding = new Thickness(12, 0, 12, 0), Child = Label("+", 15) });
        return tabs;
    }

    private Control BuildStatusBar()
    {
        var bar = new DockPanel { Background = Chrome };
        var right = Label($"Sum: {Money(_activeValue)}            ▦   ▤   ▥       ────────●────────   100%", 12);
        right.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        var ready = Label("Ready", 12);
        ready.Margin = new Thickness(12, 0, 0, 0);
        bar.Children.Add(ready);
        return bar;
    }

    // ================================================================ sheet

    private static Grid BuildSheet(Dictionary<(int Row, int Col), Cell> cells)
    {
        var sheet = new Grid
        {
            Background = White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        sheet.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(44)));
        foreach (double width in ColumnWidths)
            sheet.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(width)));
        for (int r = 0; r <= SheetRows; r++)
            sheet.RowDefinitions.Add(new RowDefinition(new GridLength(r == 0 ? 22 : 21)));

        // Corner and column letters
        Add(sheet, 0, 0, HeaderCell(string.Empty));
        for (int c = 0; c < ColumnWidths.Length; c++)
            Add(sheet, 0, c + 1, HeaderCell(((char)('A' + c)).ToString()));

        for (int r = 1; r <= SheetRows; r++)
        {
            Add(sheet, r, 0, HeaderCell(r.ToString(CultureInfo.InvariantCulture)));
            for (int c = 0; c < ColumnWidths.Length; c++)
            {
                cells.TryGetValue((r, c), out var cell);
                Add(sheet, r, c + 1, new Border
                {
                    BorderBrush = GridLine,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = cell?.Fill
                });
            }
        }

        // Text goes on top of the grid lines, spanning cells where it runs long
        foreach (var ((row, col), cell) in cells)
        {
            if (cell.Text.Length == 0)
                continue;
            var text = new TextBlock
            {
                Text = cell.Text,
                FontSize = cell.Size,
                FontWeight = cell.Bold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = cell.Foreground ?? CellText,
                HorizontalAlignment = cell.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 5, 0)
            };
            SetColumnSpan(text, cell.Span);
            Add(sheet, row, col + 1, text);
        }

        // The active cell
        Add(sheet, ActiveRow, ActiveColumn + 1, new Border
        {
            BorderBrush = Green,
            BorderThickness = new Thickness(2),
            Margin = new Thickness(-1, -1, 0, 0)
        });
        return sheet;
    }

    private static Dictionary<(int Row, int Col), Cell> BuildBudget(out long activeValue)
    {
        var cells = new Dictionary<(int Row, int Col), Cell>
        {
            [(1, 0)] = new Cell("Q3 Budget Forecast — FY2026", Bold: true, Size: 20, Span: 5),
            [(2, 0)] = new Cell("Cost center 4410 · Operations · All figures USD", Foreground: ChromeText, Span: 5)
        };

        string[] headers = ["Department", "July", "August", "September", "Q3 Total", "Budget", "Variance", "% of Budget"];
        for (int c = 0; c < headers.Length; c++)
            cells[(4, c)] = new Cell(headers[c], Right: c > 0, Bold: true, Fill: HeaderFill);

        string[] departments =
        [
            "Facilities", "IT Infrastructure", "Software Licenses", "Telecom", "Travel & Expenses", "Training",
            "Contractors", "Marketing Programs", "Office Supplies", "Insurance", "Legal & Compliance", "Equipment Leases"
        ];

        // Seeded, so the same dull numbers come up every time (and match the Windows client)
        var random = new Random(4410);
        var monthTotals = new long[3];
        long quarterTotal = 0, budgetTotal = 0;
        for (int i = 0; i < departments.Length; i++)
        {
            int row = 5 + i;
            long baseAmount = random.Next(4, 60) * 1000L + random.Next(0, 1000);
            long quarter = 0;
            cells[(row, 0)] = new Cell(departments[i]);
            for (int m = 0; m < 3; m++)
            {
                long amount = (long)(baseAmount * (0.85 + random.NextDouble() * 0.3));
                monthTotals[m] += amount;
                quarter += amount;
                cells[(row, 1 + m)] = new Cell(Money(amount), Right: true);
            }
            long budget = (long)Math.Round(quarter * (0.92 + random.NextDouble() * 0.16) / 100.0) * 100;
            AddTotals(cells, row, quarter, budget, bold: false);
            quarterTotal += quarter;
            budgetTotal += budget;
        }

        int totalRow = 5 + departments.Length + 1;
        cells[(totalRow, 0)] = new Cell("Total", Bold: true);
        for (int m = 0; m < 3; m++)
            cells[(totalRow, 1 + m)] = new Cell(Money(monthTotals[m]), Right: true, Bold: true);
        AddTotals(cells, totalRow, quarterTotal, budgetTotal, bold: true);

        cells[(totalRow + 2, 0)] = new Cell("Notes", Bold: true);
        cells[(totalRow + 3, 0)] = new Cell("Travel freeze extended through the end of the quarter per the CFO memo.",
                                            Foreground: ChromeText, Span: 6);
        cells[(totalRow + 4, 0)] = new Cell("Reforecast is due to Finance by the 15th. Any variance over 5% needs a comment.",
                                            Foreground: ChromeText, Span: 6);

        activeValue = quarterTotal;
        return cells;
    }

    private static void AddTotals(Dictionary<(int Row, int Col), Cell> cells, int row, long total, long budget, bool bold)
    {
        long variance = budget - total;
        cells[(row, 4)] = new Cell(Money(total), Right: true, Bold: bold);
        cells[(row, 5)] = new Cell(Money(budget), Right: true, Bold: bold);
        cells[(row, 6)] = new Cell(variance < 0 ? $"({Money(-variance)})" : Money(variance), Right: true, Bold: bold,
                                   Foreground: variance < 0 ? Negative : null);
        cells[(row, 7)] = new Cell((total * 100.0 / budget).ToString("0.0", CultureInfo.InvariantCulture) + "%",
                                   Right: true, Bold: bold);
    }

    // ================================================================ helpers

    private static void Add(Grid grid, int row, int column, Control element)
    {
        SetRow(element, row);
        SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Border HeaderCell(string text) => new()
    {
        Background = Chrome,
        BorderBrush = HeaderLine,
        BorderThickness = new Thickness(0, 0, 1, 1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = ChromeText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static TextBlock Label(string text, double size = 12, IBrush? foreground = null, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground ?? ChromeText,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static string Money(long amount) => amount.ToString("$#,##0", CultureInfo.InvariantCulture);

    private static IBrush Solid(byte r, byte g, byte b) => new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));
}
