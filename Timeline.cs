using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace HistoryBoard;

// A timeline stores its events as lines of text: "1812 — Отечественная война".
// When every date contains a year the events are placed to scale, otherwise evenly.
public static partial class Timeline
{
    public sealed record Event(string Date, string Title, double? Year);
    public sealed record Placed(Event Event, double X, int Row, FormattedText Date, FormattedText? Title, double Wrap);
    public sealed record Layout(IReadOnlyList<Placed> Events, double AxisY, double TitleTop, double TitleRowHeight, double Height, bool Proportional);

    public const string Example = "1812 — Отечественная война\n1825 — Восстание декабристов\n1861 — Отмена крепостного права\n1905 — Первая русская революция";
    private static readonly string[] Separators = [" — ", " – ", " - ", "\t", ": "];

    public static List<Event> Parse(string text)
    {
        var events = new List<Event>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            string date = line, title = "";
            foreach (var separator in Separators)
            {
                var index = line.IndexOf(separator, StringComparison.Ordinal);
                if (index <= 0) continue;
                date = line[..index].Trim(); title = line[(index + separator.Length)..].Trim(); break;
            }
            events.Add(new Event(date, title, Year(date)));
        }
        return events;
    }

    [GeneratedRegex(@"(?<![\p{L}\d])(?<n>\d{1,2}|[IVXLC]{1,6})\s*(-?й\s*)?(в\.|в\b|век|вв)", RegexOptions.IgnoreCase)]
    private static partial Regex CenturyPattern();
    [GeneratedRegex(@"\d{1,4}")]
    private static partial Regex NumberPattern();

    // "1812", "22 июня 1941", "753 до н. э.", "XIX век", "17 в." → year on a common scale.
    public static double? Year(string date)
    {
        var beforeEra = date.Contains("до н", StringComparison.OrdinalIgnoreCase) || date.Contains("до р", StringComparison.OrdinalIgnoreCase) || date.Contains("BC", StringComparison.Ordinal);
        double? value = null;
        var century = CenturyPattern().Match(date);
        if (century.Success)
        {
            var text = century.Groups["n"].Value;
            var number = char.IsDigit(text[0]) ? int.Parse(text, CultureInfo.InvariantCulture) : Roman(text.ToUpperInvariant());
            if (number is > 0 and <= 40) value = beforeEra ? -(number - 1) * 100 - 50 : (number - 1) * 100 + 50;
        }
        if (value == null)
        {
            var numbers = NumberPattern().Matches(date).Select(m => m.Value).ToList();
            var year = numbers.FirstOrDefault(n => n.Length >= 3) ?? numbers.LastOrDefault();
            if (year != null) value = int.Parse(year, CultureInfo.InvariantCulture);
        }
        return value is { } v && beforeEra && v > 0 ? -v : value;
    }

    private static int Roman(string text)
    {
        var values = new Dictionary<char, int> { ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50, ['C'] = 100 };
        var total = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var current = values[text[i]];
            total += i + 1 < text.Length && values[text[i + 1]] > current ? -current : current;
        }
        return total;
    }

    private static FormattedText Text(string value, double size, bool bold, BoardItem item, double wrap = 0)
    {
        var typeface = new Typeface(new FontFamily(item.Serif ? "Georgia" : "Segoe UI"), FontStyles.Normal, bold || item.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(value.Length == 0 ? " " : value, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, typeface, size, BoardSurface.Brush(item.Color), 1.0);
        if (wrap > 0) { text.MaxTextWidth = wrap; text.TextAlignment = TextAlignment.Center; }
        return text;
    }

    public static Layout Arrange(BoardItem item)
    {
        var events = Parse(item.Text);
        var proportional = events.Count >= 2 && events.All(e => e.Year != null) && events.Select(e => e.Year).Distinct().Count() >= 2;
        var ordered = proportional ? events.OrderBy(e => e.Year).ToList() : events;
        var padding = Math.Max(40, item.Size * 2.2);
        var span = Math.Max(1, item.Width - 2 * padding);
        var min = proportional ? ordered.Min(e => e.Year!.Value) : 0; var max = proportional ? ordered.Max(e => e.Year!.Value) : 1;
        var wrap = Math.Clamp(ordered.Count > 1 ? span / (ordered.Count - 1) * 1.7 : span, 90, Math.Max(90, item.Size * 11));
        var lastX = new[] { double.NegativeInfinity, double.NegativeInfinity };
        var placed = new List<Placed>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            var x = padding + (proportional ? (e.Year!.Value - min) / (max - min) * span : ordered.Count == 1 ? span / 2 : i * span / (ordered.Count - 1));
            var date = Text(e.Date, item.Size, true, item);
            var title = e.Title.Length > 0 ? Text(e.Title, Math.Max(10, item.Size * .72), false, item, wrap) : null;
            // Labels of close events go to the second row when they would overlap in the first one.
            var half = Math.Max(date.Width, title?.Width ?? 0) / 2 + item.Size * .3;
            var row = x - half >= lastX[0] ? 0 : x - half >= lastX[1] ? 1 : lastX[0] <= lastX[1] ? 0 : 1;
            lastX[row] = x + half;
            placed.Add(new Placed(e, x, row, date, title, wrap));
        }
        var dateRow = item.Size * 1.4 + 8;
        var rows = placed.Count == 0 ? 1 : placed.Max(p => p.Row) + 1;
        var axisY = dateRow * rows + 14;
        var titleRow = placed.Where(p => p.Title != null).Select(p => p.Title!.Height).DefaultIfEmpty(item.Size).Max() + 10;
        var titleTop = axisY + 18;
        return new Layout(placed, axisY, titleTop, titleRow, titleTop + titleRow * rows, proportional);
    }

    public static void Draw(DrawingContext dc, BoardItem item)
    {
        var layout = Arrange(item);
        var brush = BoardSurface.Brush(item.Color);
        var thickness = Math.Max(2, item.Size * .12);
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round }; pen.Freeze();
        var thin = new Pen(brush, Math.Max(1, thickness / 2)) { DashStyle = DashStyles.Dot }; thin.Freeze();
        var head = Math.Max(12, item.Size * .7);
        dc.DrawLine(pen, new Point(0, layout.AxisY), new Point(item.Width - head * .6, layout.AxisY));
        var arrow = new StreamGeometry();
        using (var c = arrow.Open())
        {
            c.BeginFigure(new Point(item.Width, layout.AxisY), true, true);
            c.LineTo(new Point(item.Width - head, layout.AxisY - head * .5), true, false);
            c.LineTo(new Point(item.Width - head, layout.AxisY + head * .5), true, false);
        }
        arrow.Freeze(); dc.DrawGeometry(brush, null, arrow);
        var radius = Math.Max(4, item.Size * .26);
        var dateRow = item.Size * 1.4 + 8;
        foreach (var e in layout.Events)
        {
            var dateBottom = layout.AxisY - 12 - e.Row * dateRow;
            var titleY = layout.TitleTop + e.Row * layout.TitleRowHeight;
            if (e.Row > 0)
            {
                dc.DrawLine(thin, new Point(e.X, layout.AxisY - radius), new Point(e.X, dateBottom));
                if (e.Title != null) dc.DrawLine(thin, new Point(e.X, layout.AxisY + radius), new Point(e.X, titleY));
            }
            dc.DrawEllipse(Brushes.White, pen, new Point(e.X, layout.AxisY), radius, radius);
            dc.DrawText(e.Date, new Point(e.X - e.Date.Width / 2, dateBottom - e.Date.Height));
            if (e.Title != null) dc.DrawText(e.Title, new Point(e.X - e.Wrap / 2, titleY));
        }
        if (layout.Events.Count == 0)
        {
            var hint = Text("Двойной клик — добавить события", Math.Max(10, item.Size * .7), false, new BoardItem { Color = "#8A9581", Size = item.Size });
            dc.DrawText(hint, new Point(item.Width / 2 - hint.Width / 2, layout.AxisY + 14));
        }
    }
}
