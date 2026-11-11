using System.IO;
using System.Windows;

namespace HistoryBoard;

// Ready-made example lessons. They are built from board objects (no external pictures), so they
// can be shipped freely, opened without preparation and changed like any other lesson.
public static class DemoLessons
{
    public sealed record Demo(string FileName, string Title, Func<Lesson> Build);

    public static IReadOnlyList<Demo> All { get; } =
    [
        new("lesson-ancient-rome.hboard", "Древний Рим", AncientRome),
        new("lesson-1812.hboard", "Отечественная война 1812 года", War1812),
        new("lesson-great-patriotic-war.hboard", "Великая Отечественная война", GreatPatriotic)
    ];

    public static string ExamplesDirectory => Path.Combine(App.DataDirectory, "Примеры уроков");

    // Writes an example once; afterwards the teacher's own changes to that file are kept.
    public static string EnsureFile(Demo demo, string? directory = null)
    {
        directory ??= ExamplesDirectory;
        var path = Path.Combine(directory, demo.Title + ".hboard");
        if (!File.Exists(path)) LessonFile.Save(path, demo.Build(), new Dictionary<string, byte[]>());
        return path;
    }

    public static void WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var demo in All) LessonFile.Save(Path.Combine(directory, demo.FileName), demo.Build(), new Dictionary<string, byte[]>());
    }

    private const string Ink = "#26352F", Green = "#58725B", Red = "#C94F49", Blue = "#427CB3", Gold = "#D69A38", Grey = "#7B827C";
    private const double FrameWidth = 1600, FrameHeight = 900, FrameGap = 400;

    private sealed class Builder(string title)
    {
        public readonly Lesson Lesson = new() { Title = title, Paper = "grid" };
        private double frameX = -(FrameWidth + FrameGap);
        public double X0 => frameX; public double Y0 => 0;

        public void Frame(string name)
        {
            frameX += FrameWidth + FrameGap;
            Lesson.Frames.Add(new LessonFrame { Name = name, X = frameX, Y = 0, Width = FrameWidth, Height = FrameHeight });
        }
        public BoardItem Add(BoardItem item) { Lesson.Items.Add(item); return item; }
        public BoardItem Text(double x, double y, string text, double size, string color = Ink, bool bold = false, bool serif = false, double width = 1400, int step = 0) =>
            Add(new BoardItem { Kind = "text", X = X0 + x, Y = Y0 + y, Text = text, Size = size, Color = color, Bold = bold, Serif = serif, Width = width, Step = step });
        public BoardItem Heading(string text) => Text(100, 70, text, 54, Ink, true, true);
        public BoardItem Box(double x, double y, double w, double h, string color, int step = 0) =>
            Add(new BoardItem { Kind = "rect", X = X0 + x, Y = Y0 + y, Width = w, Height = h, Color = color, Size = 3, Fill = BoardSurface.FillFor(color), Step = step });
        public BoardItem Cover(double x, double y, double w, double h, string color = Green) =>
            Add(new BoardItem { Kind = "cover", X = X0 + x, Y = Y0 + y, Width = w, Height = h, Color = color, Size = 4 });
        public BoardItem Timeline(double x, double y, double width, string events, double size = 22, string color = Ink) =>
            Add(new BoardItem { Kind = "timeline", X = X0 + x, Y = Y0 + y, Width = width, Height = 1, Text = events, Size = size, Color = color });
        public BoardItem Callout(double tipX, double tipY, double x, double y, string text, double size = 24, string color = Ink, int step = 0) =>
            Add(new BoardItem { Kind = "callout", X = X0 + x, Y = Y0 + y, Width = 340, Text = text, Size = size, Color = color, Step = step, Points = [new(tipX - x, tipY - y)] });
        public BoardItem Arrow(Point from, Point to, string color, double size = 6, int step = 0) =>
            Add(new BoardItem { Kind = "arrow", Color = color, Size = size, Step = step, Points = [new(X0 + from.X, Y0 + from.Y), new(X0 + to.X, Y0 + to.Y)] });
        public BoardItem Line(string color, double size, int step, params Point[] points) =>
            Add(new BoardItem { Kind = "pen", Color = color, Size = size, Step = step, Points = points.Select(p => new BoardPoint(X0 + p.X, Y0 + p.Y)).ToList() });
        public void Dot(Point at, string label, double size = 24, string color = Ink, double dx = 14, double dy = -34)
        {
            Add(new BoardItem { Kind = "ellipse", X = X0 + at.X - 8, Y = Y0 + at.Y - 8, Width = 16, Height = 16, Color = color, Size = 2, Fill = "#FF" + color[1..] });
            Text(at.X + dx, at.Y + dy, label, size, color, width: 320);
        }

        // Question with its answer under a cover; the answer is revealed by a click in the show.
        public void Question(double y, string question, string answer, double answerWidth = 520)
        {
            Text(100, y, question, 30, Ink, width: 880);
            Text(1000, y, answer, 30, Green, true, width: answerWidth);
            Cover(990, y - 8, answerWidth + 20, 58);
        }

        public void Title(string title, string subtitle, string? note = null)
        {
            Frame(title);
            Text(100, 180, title, 80, Ink, true, true); // two lines fit above the subtitle
            Text(104, 450, subtitle, 34, Grey, width: 1300);
            Text(104, 760, note ?? "Пример урока «Доски истории». Измените его под свой класс или используйте как образец: кадры внизу — слайды показа (F5).", 20, Grey, width: 1300);
            Box(104, 540, 520, 10, Gold);
        }

        public void Hint(string text) => Text(100, 820, text, 18, Grey, width: 1400);
    }

    private static Lesson AncientRome()
    {
        var b = new Builder("Древний Рим");
        b.Title("Древний Рим", "От основания города до падения Западной Римской империи");

        b.Frame("Лента времени");
        b.Heading("Основные даты");
        b.Timeline(60, 260, 1480, "753 до н. э. — Легендарное основание Рима\n509 до н. э. — Установление республики\n264 до н. э. — Начало Первой Пунической войны\n44 до н. э. — Убийство Гая Юлия Цезаря\n27 до н. э. — Октавиан получает титул Август\n395 — Разделение империи на Западную и Восточную\n476 — Падение Западной Римской империи", 24);
        b.Hint("Двойной клик по ленте — изменить события. Даты «до н. э.» расставляются на оси в правильном порядке.");

        b.Frame("Республика");
        b.Heading("Как управлялась Римская республика?");
        var columns = new[] { ("Консулы", "Два консула избирались на один год; командовали армией"), ("Сенат", "Совет знатных граждан и бывших магистратов; решал вопросы войны, мира и казны"), ("Народное собрание", "Полноправные граждане; избирали должностных лиц и принимали законы") };
        for (var i = 0; i < columns.Length; i++)
        {
            var x = 100 + i * 480;
            b.Box(x, 220, 440, 470, i switch { 0 => Blue, 1 => Gold, _ => Green });
            b.Text(x + 24, 244, columns[i].Item1, 34, Ink, true, width: 400);
            b.Text(x + 24, 330, columns[i].Item2, 26, Ink, width: 390);
            b.Cover(x + 14, 316, 412, 340);
        }
        b.Hint("В показе щёлкните по плашке, чтобы открыть ответ. Плашка «Закрыть плашкой» — в меню «Действия».");

        b.Frame("Причины падения");
        b.Heading("Почему пала Западная Римская империя?");
        string[] causes = ["Кризис III века: смуты, частая смена императоров, упадок хозяйства", "Великое переселение народов и натиск германских племён", "Ослабление армии, опора на наёмников-варваров", "Разделение империи в 395 году на Западную и Восточную"];
        for (var i = 0; i < causes.Length; i++) b.Text(120, 230 + i * 130, $"{i + 1}.  {causes[i]}", 32, Ink, width: 1360, step: i + 1);
        b.Callout(900, 760, 1080, 640, "476 г. — вождь Одоакр низлагает последнего императора Запада", 22, Red, step: 4);
        b.Hint("Причины появляются по одной: в показе нажимайте → или пробел. Номер шага — в меню «Действия → Появление в показе».");

        b.Frame("Проверь себя");
        b.Heading("Проверь себя");
        b.Question(220, "Когда, по легенде, был основан Рим?", "753 г. до н. э.");
        b.Question(330, "Кто первым получил титул Август?", "Октавиан");
        b.Question(440, "Как назывались войны Рима с Карфагеном?", "Пунические войны");
        b.Question(550, "В каком году пала Западная Римская империя?", "476 г.");
        b.Hint("Щёлкните по плашке, чтобы открыть ответ, и ещё раз — чтобы закрыть.");
        return b.Lesson;
    }

    private static Lesson War1812()
    {
        var b = new Builder("Отечественная война 1812 года");
        b.Title("Отечественная война 1812 года", "Вторжение Наполеона в Россию и его поражение", "Даты указаны по старому стилю. Пример урока «Доски истории»: кадры внизу — слайды показа (F5).");

        b.Frame("Ход войны");
        b.Heading("Ход войны");
        b.Timeline(60, 230, 1480, "12 июня 1812 — Вторжение «Великой армии» в Россию\n4–6 августа 1812 — Смоленское сражение\n26 августа 1812 — Бородинское сражение\n1 сентября 1812 — Совет в Филях\n2 сентября 1812 — Французы вступают в Москву\n7 октября 1812 — Наполеон оставляет Москву\n12 октября 1812 — Сражение у Малоярославца\n14–16 ноября 1812 — Переправа через Березину\n25 декабря 1812 — Манифест об окончании войны", 22);
        b.Hint("Даты одного года расставляются по месяцам и дням.");

        b.Frame("Схема похода");
        b.Heading("Схема похода Наполеона");
        // A schematic, not a map: longitude and latitude are projected onto the frame.
        Point P(double lon, double lat) => new(110 + (lon - 23.5) * 96, 330 + (55.9 - lat) * 200);
        var kovno = P(23.9, 54.9); var vilna = P(25.3, 54.7); var vitebsk = P(30.2, 55.2); var smolensk = P(32.05, 54.78);
        var vyazma = P(34.3, 55.21); var borodino = P(35.82, 55.52); var moscow = P(37.62, 55.75); var malo = P(36.47, 55.01);
        var tarutino = P(36.98, 55.06); var borisov = P(28.5, 54.23);
        b.Line(Blue, 3, 0, kovno with { X = kovno.X - 30, Y = kovno.Y - 120 }, kovno with { X = kovno.X - 18, Y = kovno.Y - 30 }, kovno with { X = kovno.X - 26, Y = kovno.Y + 60 }, kovno with { X = kovno.X - 12, Y = kovno.Y + 150 });
        b.Text(kovno.X - 100, kovno.Y - 170, "р. Неман", 20, Blue, width: 200);
        b.Line(Blue, 3, 0, borisov with { X = borisov.X + 10, Y = borisov.Y - 100 }, borisov with { X = borisov.X - 4, Y = borisov.Y - 20 }, borisov with { X = borisov.X + 10, Y = borisov.Y + 50 });
        b.Text(borisov.X - 170, borisov.Y - 70, "р. Березина", 20, Blue, width: 200);
        // Each route is a group of arrows, so it moves, is selected and appears as one piece.
        var invasionGroup = Guid.NewGuid().ToString("N"); var retreatGroup = Guid.NewGuid().ToString("N");
        var invasion = new[] { kovno, vilna, vitebsk, smolensk, vyazma, borodino, moscow };
        for (var i = 1; i < invasion.Length; i++) b.Arrow(invasion[i - 1] with { Y = invasion[i - 1].Y - 14 }, invasion[i] with { Y = invasion[i].Y - 14 }, Blue, 6, 1).GroupId = invasionGroup;
        var retreat = new[] { moscow, malo, vyazma, smolensk, borisov, vilna, kovno };
        for (var i = 1; i < retreat.Length; i++) b.Arrow(retreat[i - 1] with { Y = retreat[i - 1].Y + 14 }, retreat[i] with { Y = retreat[i].Y + 14 }, Red, 5, 3).GroupId = retreatGroup;
        b.Arrow(moscow with { X = moscow.X - 10, Y = moscow.Y + 30 }, tarutino with { Y = tarutino.Y - 10 }, Green, 5, 2);
        b.Dot(kovno, "Ковно"); b.Dot(vilna, "Вильна"); b.Dot(vitebsk, "Витебск"); b.Dot(smolensk, "Смоленск"); b.Dot(vyazma, "Вязьма", dy: 14);
        b.Dot(borodino, "Бородино"); b.Dot(moscow, "Москва", 28, Red); b.Dot(malo, "Малоярославец", dx: -120, dy: 16); b.Dot(tarutino, "Тарутино", dy: 12); b.Dot(borisov, "Борисов", dx: 14, dy: 10);
        b.Callout(borodino.X, borodino.Y, borodino.X - 360, borodino.Y - 230, "26 августа — Бородинское сражение", 22, Ink, 1);
        b.Text(100, 760, "—  наступление «Великой армии»", 22, Blue, step: 1, width: 500);
        b.Text(620, 760, "—  Тарутинский манёвр русской армии", 22, Green, step: 2, width: 520);
        b.Text(1160, 760, "—  отступление французов", 22, Red, step: 3, width: 400);
        b.Hint("Схема, а не карта: расстояния приблизительные. Стрелки появляются по шагам. Можно добавить настоящую карту через «＋ Добавить» и закрепить её на фоне.");

        b.Frame("Проверь себя");
        b.Heading("Проверь себя");
        b.Question(220, "Кто командовал русской армией при Бородине?", "М. И. Кутузов");
        b.Question(330, "Где было принято решение оставить Москву?", "На совете в Филях");
        b.Question(440, "Какое сражение заставило Наполеона отступать по разорённой Смоленской дороге?", "У Малоярославца");
        b.Question(580, "Через какую реку переправлялась отступавшая армия в ноябре 1812 года?", "Березина");
        return b.Lesson;
    }

    private static Lesson GreatPatriotic()
    {
        var b = new Builder("Великая Отечественная война");
        b.Title("Великая Отечественная война", "1941–1945: основные события и периоды");

        b.Frame("Ключевые даты");
        b.Heading("Ключевые даты");
        b.Timeline(60, 230, 1480, "22 июня 1941 — Нападение Германии на СССР\n8 сентября 1941 — Начало блокады Ленинграда\n5 декабря 1941 — Контрнаступление под Москвой\n19 ноября 1942 — Контрнаступление под Сталинградом\n2 февраля 1943 — Победа в Сталинградской битве\n5 июля 1943 — Начало Курской битвы\n27 января 1944 — Полное снятие блокады Ленинграда\n23 июня 1944 — Начало операции «Багратион»\n9 мая 1945 — День Победы", 20);
        b.Hint("События расставлены на оси по дням: близкие даты уходят в верхние ряды подписей.");

        b.Frame("Периоды войны");
        b.Heading("Периоды войны");
        var periods = new[] { ("Начальный период", "22 июня 1941 — 18 ноября 1942", "Отступление Красной армии, битва за Москву — первое крупное поражение вермахта"), ("Коренной перелом", "19 ноября 1942 — конец 1943", "Сталинградская и Курская битвы, битва за Днепр; инициатива переходит к СССР"), ("Завершающий период", "1944 — 9 мая 1945", "Освобождение территории СССР, походы в Европу, взятие Берлина") };
        for (var i = 0; i < periods.Length; i++)
        {
            var x = 100 + i * 480; var color = i switch { 0 => Red, 1 => Gold, _ => Green };
            b.Box(x, 220, 440, 500, color, i + 1);
            b.Text(x + 24, 244, $"{i + 1}. {periods[i].Item1}", 32, Ink, true, width: 400, step: i + 1);
            b.Text(x + 24, 340, periods[i].Item2, 24, color, true, width: 400, step: i + 1);
            b.Text(x + 24, 420, periods[i].Item3, 24, Ink, width: 390, step: i + 1);
        }
        b.Hint("Периоды появляются по одному: в показе нажимайте → или пробел.");

        b.Frame("Коренной перелом");
        b.Heading("Коренной перелом");
        b.Box(100, 220, 680, 420, Blue); b.Box(820, 220, 680, 420, Gold);
        b.Text(130, 244, "Сталинградская битва", 34, Ink, true, width: 620);
        b.Text(130, 320, "17 июля 1942 — 2 февраля 1943", 28, Blue, true, width: 620);
        b.Cover(120, 310, 640, 56, Blue);
        b.Text(130, 400, "Окружение и капитуляция 6-й армии Паулюса; начало коренного перелома", 26, Ink, width: 620);
        b.Text(850, 244, "Курская битва", 34, Ink, true, width: 620);
        b.Text(850, 320, "5 июля — 23 августа 1943", 28, Gold, true, width: 620);
        b.Cover(840, 310, 640, 56, Gold);
        b.Text(850, 400, "Танковое сражение у Прохоровки; после битвы инициатива окончательно перешла к Красной армии", 26, Ink, width: 620);
        b.Hint("Плашки закрывают даты битв: щелчок в показе открывает их.");

        b.Frame("Проверь себя");
        b.Heading("Проверь себя");
        b.Question(220, "Сколько дней длилась блокада Ленинграда?", "872 дня");
        b.Question(330, "Когда началось контрнаступление под Москвой?", "5–6 декабря 1941 г.");
        b.Question(440, "Как называлась операция по освобождению Белоруссии?", "«Багратион»");
        b.Question(550, "Когда был подписан акт о безоговорочной капитуляции Германии?", "8 мая 1945 г. (в Москве — 9 мая)", 560);
        return b.Lesson;
    }
}
