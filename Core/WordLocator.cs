using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MagniReader.Core;

/// <summary>
/// Finds where each word of the text being read sits on screen, so the overlay
/// can draw a box over the word that is being spoken.
///
/// Strategies, tried in this order:
///   1. UI Automation lines: walk the paragraph line by line (one text + one
///      rectangle per line) and place each word inside its line by measuring the
///      text with the line's font. About ten cross-process calls per line instead
///      of three per word, so a long paragraph costs tens of calls, not a thousand.
///   2. UI Automation FindText: search each word with FindText and ask for its
///      rectangles. Exact, but slow; used when the app doesn't report lines.
///   3. OCR (Windows.Media.Ocr): screenshot the element, recognise the words and
///      align them in order with the spoken words. Used when the text came from
///      a Name/Value property or UI Automation can't place the words. The
///      screenshot is only taken when it's needed.
///
/// Everything runs on a background thread, ahead of the speech. While reading,
/// the locator checks every half second whether the text has moved (the user
/// scrolled) and shifts all boxes by the same amount; <see cref="Moved"/> tells
/// the app so the highlight follows. All rectangles are in physical screen
/// pixels, the same space as AutomationElement.BoundingRectangle.
/// </summary>
public sealed class WordLocator : IDisposable
{
    private readonly string _text;
    private readonly List<(int start, int length)> _words;
    private readonly Rect?[] _rects;
    private readonly CancellationTokenSource _cts = new();

    // Scroll tracking: something on screen whose position we can re-query cheaply,
    // with the rectangle it had when the words were located.
    private sealed class Anchor
    {
        public TextPatternRange? Range;
        public AutomationElement? Element;
        public Rect Baseline;
    }
    private readonly List<Anchor> _anchors = new();
    private readonly int[] _wordAnchor;
    private volatile int _currentWord = -1;

    /// <summary>Which strategy ended up being used (for logs).</summary>
    public string Strategy { get; private set; } = "none";

    /// <summary>
    /// Raised on a background thread when the text moved on screen (scrolling) while
    /// it is being read. The argument is the offset in physical pixels.
    /// </summary>
    public event Action<Vector>? Moved;

    private WordLocator(string text)
    {
        _text = text;
        _words = Tokenize(text);
        _rects = new Rect?[_words.Count];
        _wordAnchor = Enumerable.Repeat(-1, _words.Count).ToArray();
    }

    /// <summary>
    /// Starts locating the words of <paramref name="spokenText"/> in the background.
    /// </summary>
    public static WordLocator Start(string spokenText, Rect elementRect,
        AutomationElement? source, CultureInfo? culture, TextPatternRange? range = null)
    {
        var locator = new WordLocator(spokenText);
        if (locator._words.Count == 0) return locator;

        // MagniReader's overlay is excluded from screen captures on Windows 10 2004+,
        // so the OCR screenshot can wait until OCR is really needed. Older Windows
        // would capture the dimmed overlay, so take it now there.
        byte[]? earlyPng = null;
        Rect earlyRect = Rect.Empty;
        if (!CaptureExclusionSupported && IsUsableRect(elementRect))
        {
            try { (earlyPng, earlyRect) = CaptureScreen(elementRect); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WordLocator] Capture failed: {ex.Message}"); }
        }

        var token = locator._cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                bool uiaWorked = false;
                if (range != null)
                    uiaWorked = locator.LocateWithLines(range, token);
                if (!uiaWorked && source != null)
                    uiaWorked = locator.LocateWithTextPattern(source, range, token);

                if (!uiaWorked && !token.IsCancellationRequested)
                {
                    var (png, captureRect) = (earlyPng, earlyRect);
                    if (png == null && IsUsableRect(elementRect))
                        (png, captureRect) = CaptureScreen(elementRect);
                    if (png != null)
                    {
                        await locator.LocateWithOcrAsync(png, captureRect, culture, token);
                        if (source != null)
                            locator.AddAnchorForAll(new Anchor { Element = source, Baseline = SafeElementRect(source) });
                    }
                }

                Infrastructure.CrashLogger.LogTrace(
                    $"[WordLocator] {locator.Strategy}: located " +
                    $"{locator.LocatedCount()}/{locator._rects.Length} words");

                await locator.TrackScrollingAsync(token);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WordLocator] Failed: {ex.Message}");
            }
        }, token);

        return locator;
    }

    private static readonly bool CaptureExclusionSupported =
        Environment.OSVersion.Version.Build >= 19041;

    /// <summary>
    /// Returns the on-screen rectangle (physical pixels) of the word that contains
    /// the character at <paramref name="charPosition"/> in the spoken text,
    /// or null if it isn't known (yet).
    /// </summary>
    public Rect? GetWordRect(int charPosition)
    {
        int index = WordIndexAt(charPosition);
        if (index < 0) return null;
        _currentWord = index;
        lock (_rects)
            return _rects[index] ?? Interpolate(index);
    }

    /// <summary>
    /// For a word the locator couldn't find (OCR misread, provider quirk), guess
    /// its box from the nearest located words on the same line, splitting the
    /// space between them by character count. Without this the box would stay
    /// on the previous word and look "behind" the voice. Caller holds the lock.
    /// </summary>
    private Rect? Interpolate(int index)
    {
        const int maxGap = 4;
        int prev = -1, next = -1;
        for (int i = index - 1; i >= Math.Max(0, index - maxGap); i--)
            if (_rects[i].HasValue) { prev = i; break; }
        for (int i = index + 1; i < Math.Min(_rects.Length, index + maxGap + 1); i++)
            if (_rects[i].HasValue) { next = i; break; }
        if (prev < 0) return null;

        Rect p = _rects[prev]!.Value;
        if (next >= 0)
        {
            Rect n = _rects[next]!.Value;
            bool sameLine = Math.Abs(n.Y - p.Y) < Math.Max(p.Height, n.Height) / 2 && n.Left > p.Right;
            if (sameLine)
            {
                // Characters (incl. one space per word) between the two located words
                int total = 0, before = 0;
                for (int i = prev + 1; i < next; i++)
                {
                    if (i == index) before = total;
                    total += _words[i].length + 1;
                }
                total += 1;
                double unit = (n.Left - p.Right) / Math.Max(1, total);
                double x = p.Right + unit * (before + 1);
                double w = Math.Max(4, unit * _words[index].length);
                return new Rect(x, p.Y, w, p.Height);
            }
        }

        // Only the previous word is known: put it right after, sized by the
        // previous word's average character width. Only for the next word,
        // further guesses would wander off the line.
        if (prev == index - 1 && _words[prev].length > 0)
        {
            double charW = p.Width / _words[prev].length;
            return new Rect(p.Right + charW, p.Y, Math.Max(4, charW * _words[index].length), p.Height);
        }
        return null;
    }

    private int LocatedCount()
    {
        lock (_rects) return _rects.Count(r => r.HasValue);
    }

    public void Dispose()
    {
        Moved = null;
        try { _cts.Cancel(); } catch { }
        // Not disposing _cts: the background task may still be checking its token.
    }

    // ─── Scroll tracking ────────────────────────────────────────

    private void AddAnchorForAll(Anchor anchor)
    {
        lock (_rects)
        {
            _anchors.Add(anchor);
            int a = _anchors.Count - 1;
            for (int i = 0; i < _wordAnchor.Length; i++)
                if (_wordAnchor[i] < 0) _wordAnchor[i] = a;
        }
    }

    /// <summary>
    /// Every 500 ms, re-reads the rectangle of the line (or word, or element) being
    /// spoken. If it moved, the user scrolled: shift every word box by the same amount.
    /// One cross-process call per check.
    /// </summary>
    private async Task TrackScrollingAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(500, token);

            Anchor? anchor;
            lock (_rects)
            {
                int w = _currentWord;
                if (w < 0 || w >= _wordAnchor.Length || _wordAnchor[w] < 0) continue;
                anchor = _anchors[_wordAnchor[w]];
            }

            Rect now;
            try
            {
                now = anchor.Range != null ? UnionRect(anchor.Range.GetBoundingRectangles())
                    : anchor.Element != null ? SafeElementRect(anchor.Element)
                    : Rect.Empty;
            }
            catch { continue; }
            if (!IsUsableRect(now) || !IsUsableRect(anchor.Baseline)) continue; // scrolled out of view

            var delta = new Vector(now.X - anchor.Baseline.X, now.Y - anchor.Baseline.Y);
            if (Math.Abs(delta.X) < 2 && Math.Abs(delta.Y) < 2) continue;

            lock (_rects)
            {
                for (int i = 0; i < _rects.Length; i++)
                    if (_rects[i] is Rect r) _rects[i] = Rect.Offset(r, delta);
                foreach (var a in _anchors)
                    if (IsUsableRect(a.Baseline)) a.Baseline = Rect.Offset(a.Baseline, delta);
            }
            Moved?.Invoke(delta);
        }
    }

    private static Rect SafeElementRect(AutomationElement element)
    {
        try { return element.Current.BoundingRectangle; }
        catch { return Rect.Empty; }
    }

    /// <summary>Union of the usable rectangles, or Empty.</summary>
    private static Rect UnionRect(Rect[]? rects)
    {
        var u = Rect.Empty;
        if (rects == null) return u;
        foreach (var r in rects)
            if (IsUsableRect(r)) u.Union(r);
        return u;
    }

    // ─── Strategy 1: UI Automation lines + text measuring ──────

    private sealed class LineInfo
    {
        public required TextPatternRange Range;
        public required string Text;
        public required Rect Rect;
        public string? FontName;
        public double FontSize;
        public int JoinedStart;
        public int Anchor;
    }

    /// <summary>
    /// Walks the paragraph line by line. Each word is matched to its line in the
    /// joined line texts (in memory) and placed inside the line's rectangle by
    /// measuring the text before it with the line's font, scaled to the line's real
    /// width. Returns false when the app doesn't report lines, or most words can't
    /// be matched, so the caller can fall back to FindText or OCR.
    /// </summary>
    private bool LocateWithLines(TextPatternRange paragraph, CancellationToken token)
    {
        TextPatternRange cursor;
        string? paraFont = null;
        double paraSize = 0;
        try
        {
            cursor = paragraph.Clone();
            cursor.MoveEndpointByRange(TextPatternRangeEndpoint.End, cursor, TextPatternRangeEndpoint.Start);
            (paraFont, paraSize) = ReadFont(paragraph);
        }
        catch { return false; }

        Strategy = "UIA lines";
        var lines = new List<LineInfo>();
        var joined = new System.Text.StringBuilder();
        int wordCursor = 0, textCursor = 0, found = 0;
        bool unsupported = false;

        using var measurer = new TextMeasurer();

        for (int guard = 0; guard < 400; guard++)
        {
            token.ThrowIfCancellationRequested();
            LineInfo line;
            try
            {
                var r = cursor.Clone();
                r.ExpandToEnclosingUnit(TextUnit.Line);
                if (r.CompareEndpoints(TextPatternRangeEndpoint.Start, paragraph, TextPatternRangeEndpoint.Start) < 0)
                    r.MoveEndpointByRange(TextPatternRangeEndpoint.Start, paragraph, TextPatternRangeEndpoint.Start);
                if (r.CompareEndpoints(TextPatternRangeEndpoint.End, paragraph, TextPatternRangeEndpoint.End) > 0)
                    r.MoveEndpointByRange(TextPatternRangeEndpoint.End, paragraph, TextPatternRangeEndpoint.End);

                var text = r.GetText(-1) ?? "";
                var rects = r.GetBoundingRectangles();
                var rect = UnionRect(rects);
                // Rectangles on different rows mean the app expanded to more than one
                // line (no Line unit support): this strategy can't place words.
                if (rects != null && IsUsableRect(rect) && rects.Where(IsUsableRect).Any(x => x.Top >= rect.Top + x.Height * 0.8))
                {
                    unsupported = true;
                    break;
                }

                string? fontName = paraFont;
                double fontSize = paraSize;
                if (fontName == null || fontSize <= 0)
                    (fontName, fontSize) = ReadFont(r);

                line = new LineInfo { Range = r, Text = text, Rect = rect, FontName = fontName, FontSize = fontSize, JoinedStart = joined.Length };
            }
            catch { break; }

            lock (_rects)
            {
                _anchors.Add(new Anchor { Range = line.Range, Baseline = line.Rect });
                line.Anchor = _anchors.Count - 1;
            }
            lines.Add(line);
            joined.Append(line.Text);
            // Keep words from running together if the app gives lines without a trailing space
            if (line.Text.Length > 0 && !char.IsWhiteSpace(line.Text[^1])) joined.Append(' ');

            found += MapWords(lines, joined.ToString(), measurer, ref wordCursor, ref textCursor, final: false);

            int moved;
            try
            {
                moved = cursor.Move(TextUnit.Line, 1);
                if (moved == 0 ||
                    cursor.CompareEndpoints(TextPatternRangeEndpoint.Start, paragraph, TextPatternRangeEndpoint.End) >= 0)
                    break;
            }
            catch { break; }
        }

        found += MapWords(lines, joined.ToString(), measurer, ref wordCursor, ref textCursor, final: true);

        // Mostly unmatched: the lines' text isn't what we're speaking. Let FindText/OCR try.
        if (unsupported || found < _words.Count * 0.6)
        {
            lock (_rects)
            {
                Array.Clear(_rects);
                Array.Fill(_wordAnchor, -1);
                _anchors.Clear();
            }
            Strategy = "none";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Matches spoken words, in order, to positions in the joined line texts and stores
    /// their rectangles. Stops at a word that isn't found yet unless enough text follows
    /// the cursor to be sure it's missing (or <paramref name="final"/>).
    /// </summary>
    private int MapWords(List<LineInfo> lines, string joined, TextMeasurer measurer,
        ref int wordCursor, ref int textCursor, bool final)
    {
        const int searchWindow = 120;
        int found = 0;
        while (wordCursor < _words.Count)
        {
            var (start, length) = _words[wordCursor];
            var word = _text.Substring(start, length);
            int limit = Math.Min(joined.Length, textCursor + searchWindow + length);
            int at = IndexOfWithin(joined, word, textCursor, limit);
            int len = word.Length;
            if (at < 0)
            {
                var trimmed = word.Trim(TrimChars);
                if (trimmed.Length > 0)
                {
                    at = IndexOfWithin(joined, trimmed, textCursor, limit);
                    len = trimmed.Length;
                }
            }

            if (at < 0)
            {
                // Not found yet: wait for the next line, unless there's already enough
                // text after the cursor that the word must be missing from the page.
                if (!final && joined.Length - textCursor < searchWindow) break;
                wordCursor++;
                continue;
            }

            var line = lines.FindLast(l => l.JoinedStart <= at)!;
            int offset = at - line.JoinedStart;
            var rect = PlaceInLine(line, offset, Math.Min(len, Math.Max(0, line.Text.Length - offset)), measurer);
            if (rect.HasValue)
            {
                lock (_rects)
                {
                    _rects[wordCursor] = rect;
                    _wordAnchor[wordCursor] = line.Anchor;
                }
                found++;
            }
            textCursor = at + len;
            wordCursor++;
        }
        return found;
    }

    private static int IndexOfWithin(string haystack, string needle, int from, int limit)
    {
        if (needle.Length == 0 || from >= haystack.Length) return -1;
        int count = Math.Min(haystack.Length, limit) - from;
        if (count < needle.Length) return -1;
        return haystack.IndexOf(needle, from, count, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rectangle of the characters [offset, offset+length) inside a line, by measuring
    /// with the line's font and scaling to the line's real on-screen width.
    /// Falls back to an even split by characters when the font isn't known.
    /// </summary>
    private static Rect? PlaceInLine(LineInfo line, int offset, int length, TextMeasurer measurer)
    {
        if (!IsUsableRect(line.Rect) || length <= 0) return null;

        // The rectangle covers the visible glyphs: ignore leading/trailing whitespace.
        var text = line.Text;
        int lead = 0;
        while (lead < text.Length && char.IsWhiteSpace(text[lead])) lead++;
        int end = text.Length;
        while (end > lead && char.IsWhiteSpace(text[end - 1])) end--;
        if (end <= lead) return null;
        var visible = text.Substring(lead, end - lead);
        int o = Math.Clamp(offset - lead, 0, visible.Length);
        int l = Math.Clamp(length, 1, visible.Length - o);
        if (l <= 0) return null;

        double before, through, total;
        if (measurer.TryMeasure(line.FontName, line.FontSize, visible, out total) &&
            measurer.TryMeasure(line.FontName, line.FontSize, visible[..o], out before) &&
            measurer.TryMeasure(line.FontName, line.FontSize, visible[..(o + l)], out through) &&
            total > 0)
        {
            double scale = line.Rect.Width / total;
            return new Rect(line.Rect.Left + before * scale, line.Rect.Top,
                Math.Max(4, (through - before) * scale), line.Rect.Height);
        }

        double unit = line.Rect.Width / visible.Length;
        return new Rect(line.Rect.Left + o * unit, line.Rect.Top, Math.Max(4, l * unit), line.Rect.Height);
    }

    private static (string? name, double size) ReadFont(TextPatternRange range)
    {
        string? name = null;
        double size = 0;
        try
        {
            if (range.GetAttributeValue(TextPattern.FontNameAttribute) is string n && n.Length > 0) name = n;
            if (range.GetAttributeValue(TextPattern.FontSizeAttribute) is double d && d > 0) size = d;
        }
        catch { }
        return (name, size);
    }

    /// <summary>Measures text widths with GDI+ (thread-safe per instance, works off the UI thread).</summary>
    private sealed class TextMeasurer : IDisposable
    {
        private readonly System.Drawing.Bitmap _bmp = new(1, 1);
        private readonly System.Drawing.Graphics _g;
        private readonly System.Drawing.StringFormat _format;
        private readonly Dictionary<(string, double), System.Drawing.Font?> _fonts = new();

        public TextMeasurer()
        {
            _g = System.Drawing.Graphics.FromImage(_bmp);
            _format = (System.Drawing.StringFormat)System.Drawing.StringFormat.GenericTypographic.Clone();
            _format.FormatFlags |= System.Drawing.StringFormatFlags.MeasureTrailingSpaces;
        }

        public bool TryMeasure(string? fontName, double sizePt, string text, out double width)
        {
            width = 0;
            if (text.Length == 0) return true;
            if (fontName == null || sizePt <= 0) return false;
            if (!_fonts.TryGetValue((fontName, sizePt), out var font))
            {
                try { font = new System.Drawing.Font(fontName, (float)sizePt, System.Drawing.GraphicsUnit.Point); }
                catch { font = null; }
                _fonts[(fontName, sizePt)] = font;
            }
            if (font == null) return false;
            try
            {
                width = _g.MeasureString(text, font, new System.Drawing.PointF(0, 0), _format).Width;
                return true;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            foreach (var f in _fonts.Values) f?.Dispose();
            _format.Dispose();
            _g.Dispose();
            _bmp.Dispose();
        }
    }

    // ─── Tokenizing ─────────────────────────────────────────────

    private static List<(int start, int length)> Tokenize(string text)
    {
        var words = new List<(int, int)>();
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos >= text.Length) break;
            int start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
            words.Add((start, pos - start));
        }
        return words;
    }

    private int WordIndexAt(int charPosition)
    {
        if (_words.Count == 0 || charPosition < 0) return -1;

        // Binary search for the last word starting at or before charPosition
        int lo = 0, hi = _words.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_words[mid].start <= charPosition) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        // If the engine points at whitespace before a word, use the next word
        if (found < 0) return 0;
        var (s, l) = _words[found];
        if (charPosition >= s + l && found + 1 < _words.Count) return found + 1;
        return found;
    }

    // ─── Strategy 2: UI Automation FindText ──────────────────

    /// <summary>
    /// Walks the spoken words in order and finds each one in the element's text
    /// with FindText, starting after the previous match. Returns false if the
    /// element has no TextPattern or the first words can't be found, so the
    /// caller can fall back to OCR.
    /// </summary>
    private bool LocateWithTextPattern(AutomationElement source, TextPatternRange? range, CancellationToken token)
    {
        TextPatternRange remaining;
        try
        {
            if (range != null)
            {
                // Search only inside the paragraph being read (a word can appear
                // many times earlier in a long web page)
                remaining = range.Clone();
            }
            else
            {
                if (!source.TryGetCurrentPattern(TextPattern.Pattern, out var obj)) return false;
                remaining = ((TextPattern)obj).DocumentRange.Clone();
            }
        }
        catch { return false; }

        Strategy = "UIA FindText";
        int found = 0, misses = 0;

        for (int i = 0; i < _words.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var (start, length) = _words[i];
            string word = _text.Substring(start, length);

            TextPatternRange? match = null;
            try { match = remaining.FindText(word, backward: false, ignoreCase: false); }
            catch { }

            if (match == null)
            {
                // Try again without surrounding punctuation (e.g. quotes or
                // brackets that the provider splits differently)
                var trimmed = word.Trim(TrimChars);
                if (trimmed.Length > 0 && trimmed != word)
                {
                    try { match = remaining.FindText(trimmed, backward: false, ignoreCase: false); }
                    catch { }
                }
            }

            if (match == null)
            {
                misses++;
                // If nothing at all matches at the start, TextPattern isn't
                // giving us the same text we're speaking. Give up and use OCR.
                if (found == 0 && misses >= 5) { Strategy = "none"; return false; }
                continue;
            }

            var rect = FirstUsableRect(match);
            if (rect.HasValue)
            {
                lock (_rects)
                {
                    _rects[i] = rect;
                    _anchors.Add(new Anchor { Range = match, Baseline = rect.Value });
                    _wordAnchor[i] = _anchors.Count - 1;
                }
                found++;
            }

            // Continue searching after this word
            try
            {
                remaining.MoveEndpointByRange(TextPatternRangeEndpoint.Start, match, TextPatternRangeEndpoint.End);
            }
            catch { }
        }

        return found > 0;
    }

    private static readonly char[] TrimChars =
        "\"'“”‘’«»()[]{}<>.,;:!?¡¿…".ToCharArray();

    private static Rect? FirstUsableRect(TextPatternRange range)
    {
        try
        {
            var rects = range.GetBoundingRectangles();
            if (rects == null || rects.Length == 0) return null;

            // A word split across lines (hyphenation) returns several rects:
            // highlight the first part, where the word starts.
            foreach (var r in rects)
                if (IsUsableRect(r)) return r;
        }
        catch { }
        return null;
    }

    // ─── Strategy 3: OCR ────────────────────────────────────────

    private async Task LocateWithOcrAsync(byte[] png, Rect captureRect, CultureInfo? culture, CancellationToken token)
    {
        var engine = CreateOcrEngine(culture);
        if (engine == null)
        {
            System.Diagnostics.Debug.WriteLine("[WordLocator] No OCR engine available (install an OCR language pack)");
            return;
        }
        Strategy = $"OCR ({engine.RecognizerLanguage.LanguageTag})";

        SoftwareBitmap bitmap;
        using (var ms = new MemoryStream(png))
        {
            var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        token.ThrowIfCancellationRequested();

        double scale = bitmap.PixelHeight > 0 ? bitmap.PixelHeight / captureRect.Height : 1.0;

        OcrResult result;
        using (bitmap)
            result = await engine.RecognizeAsync(bitmap);
        token.ThrowIfCancellationRequested();

        // Flatten OCR words in reading order, converting back to screen pixels
        var ocrWords = new List<(string norm, Rect rect)>();
        foreach (var line in result.Lines)
        {
            foreach (var w in line.Words)
            {
                var b = w.BoundingRect;
                var screen = new Rect(
                    captureRect.X + b.X / scale,
                    captureRect.Y + b.Y / scale,
                    b.Width / scale,
                    b.Height / scale);
                ocrWords.Add((Normalize(w.Text), screen));
            }
        }

        // Align spoken words with OCR words in order. OCR may merge, split or
        // misread words, so look a few words ahead and allow small typos.
        const int lookAhead = 6;
        int cursor = 0;
        for (int i = 0; i < _words.Count && cursor < ocrWords.Count; i++)
        {
            var (start, length) = _words[i];
            var norm = Normalize(_text.Substring(start, length));
            if (norm.Length == 0) continue;

            // Short words ("a", "de", "the") appear everywhere: only accept them
            // right where we expect them, or they make the box jump ahead.
            int window = norm.Length <= 3 ? 2 : lookAhead;
            for (int k = cursor; k < Math.Min(ocrWords.Count, cursor + window); k++)
            {
                if (IsSimilar(norm, ocrWords[k].norm))
                {
                    lock (_rects) _rects[i] = ocrWords[k].rect;
                    cursor = k + 1;
                    break;
                }
            }
        }
    }

    internal static OcrEngine? CreateOcrEngine(CultureInfo? culture)
    {
        try
        {
            if (culture != null && !string.IsNullOrEmpty(culture.Name))
            {
                var lang = new Windows.Globalization.Language(culture.Name);
                if (OcrEngine.IsLanguageSupported(lang))
                    return OcrEngine.TryCreateFromLanguage(lang);

                // Try the neutral language (e.g. "es" for "es-ES")
                var neutral = new Windows.Globalization.Language(culture.TwoLetterISOLanguageName);
                if (OcrEngine.IsLanguageSupported(neutral))
                    return OcrEngine.TryCreateFromLanguage(neutral);
            }
        }
        catch { }

        try { return OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch { return null; }
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool IsSimilar(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen < 4) return false;
        // OCR glued punctuation-separated parts, or cut a word: accept prefixes
        if (Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b) || b.StartsWith(a))) return true;
        return Levenshtein(a, b) <= Math.Max(1, maxLen / 4);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ─── Screen capture ─────────────────────────────────────────

    /// <summary>
    /// Captures the element's area of the screen as PNG. Small text is upscaled
    /// because Windows OCR is much more accurate on larger glyphs.
    /// Returns the PNG and the screen rect it covers (physical pixels).
    /// </summary>
    internal static (byte[] png, Rect rect) CaptureScreen(Rect elementRect)
    {
        // Virtual screen bounds in physical pixels (the app is PerMonitorV2 aware)
        int vsLeft = Helpers.NativeInterop.GetSystemMetrics(Helpers.NativeInterop.SM_XVIRTUALSCREEN);
        int vsTop = Helpers.NativeInterop.GetSystemMetrics(Helpers.NativeInterop.SM_YVIRTUALSCREEN);
        int vsRight = vsLeft + Helpers.NativeInterop.GetSystemMetrics(Helpers.NativeInterop.SM_CXVIRTUALSCREEN);
        int vsBottom = vsTop + Helpers.NativeInterop.GetSystemMetrics(Helpers.NativeInterop.SM_CYVIRTUALSCREEN);

        int x = (int)Math.Floor(Math.Max(elementRect.X, vsLeft));
        int y = (int)Math.Floor(Math.Max(elementRect.Y, vsTop));
        int right = (int)Math.Ceiling(Math.Min(elementRect.Right, vsRight));
        int bottom = (int)Math.Ceiling(Math.Min(elementRect.Bottom, vsBottom));
        int w = right - x, h = bottom - y;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("Element is off screen");

        using var shot = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(shot))
            g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));

        // Upscale small captures (OCR wants roughly 20px+ tall glyphs),
        // keeping within OcrEngine.MaxImageDimension.
        double scale = h < 200 ? 2.0 : 1.0;
        int maxDim = (int)OcrEngine.MaxImageDimension;
        scale = Math.Min(scale, Math.Min((double)maxDim / w, (double)maxDim / h));
        scale = Math.Max(scale, 0.1);

        using var ms = new MemoryStream();
        if (Math.Abs(scale - 1.0) > 0.01)
        {
            int sw = Math.Max(1, (int)(w * scale)), sh = Math.Max(1, (int)(h * scale));
            using var scaled = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(shot, 0, 0, sw, sh);
            }
            scaled.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        }
        else
        {
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        }

        return (ms.ToArray(), new Rect(x, y, w, h));
    }

    private static bool IsUsableRect(Rect r) =>
        !r.IsEmpty && r.Width > 0 && r.Height > 0 &&
        !double.IsInfinity(r.Width) && !double.IsInfinity(r.Height) &&
        !double.IsNaN(r.X) && !double.IsNaN(r.Y);
}
