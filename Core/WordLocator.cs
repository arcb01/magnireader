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
/// Two strategies, tried in this order:
///   1. UI Automation TextPattern: search each word in the element's document
///      range with FindText and ask for its bounding rectangles. Exact, works in
///      browsers, Word, Notepad, Edge PDFs and most text controls.
///   2. OCR (Windows.Media.Ocr): screenshot the element, recognise the words and
///      align them in order with the spoken words. Used when the text came from
///      a Name/Value property (labels, buttons, most simple controls) or when
///      TextPattern can't find the words.
///
/// Rectangles are computed on a background thread, ahead of the speech, and
/// stored per word. All rectangles are in physical screen pixels, the same
/// space as AutomationElement.BoundingRectangle.
/// </summary>
public sealed class WordLocator : IDisposable
{
    private readonly string _text;
    private readonly List<(int start, int length)> _words;
    private readonly Rect?[] _rects;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Which strategy ended up being used (for logs).</summary>
    public string Strategy { get; private set; } = "none";

    private WordLocator(string text)
    {
        _text = text;
        _words = Tokenize(text);
        _rects = new Rect?[_words.Count];
    }

    /// <summary>
    /// Starts locating the words of <paramref name="spokenText"/>.
    /// Must be called BEFORE the overlay is drawn over the element, because the
    /// OCR path takes its screenshot synchronously here.
    /// </summary>
    public static WordLocator Start(string spokenText, Rect elementRect,
        AutomationElement? source, CultureInfo? culture, TextPatternRange? range = null)
    {
        var locator = new WordLocator(spokenText);
        if (locator._words.Count == 0) return locator;

        // Take the OCR screenshot now, while the element is still unobstructed.
        // It's cheap (a single BitBlt) and lets us fall back to OCR later.
        byte[]? png = null;
        Rect captureRect = Rect.Empty;
        try
        {
            if (IsUsableRect(elementRect))
                (png, captureRect) = CaptureScreen(elementRect);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WordLocator] Capture failed: {ex.Message}");
        }

        var token = locator._cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                bool uiaWorked = false;
                if (source != null)
                    uiaWorked = locator.LocateWithTextPattern(source, range, token);

                if (!uiaWorked && png != null && !token.IsCancellationRequested)
                    await locator.LocateWithOcrAsync(png, captureRect, culture, token);

                Infrastructure.CrashLogger.LogInfo(
                    $"[WordLocator] {locator.Strategy}: located " +
                    $"{locator.LocatedCount()}/{locator._rects.Length} words");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WordLocator] Failed: {ex.Message}");
            }
        }, token);

        return locator;
    }

    /// <summary>
    /// Returns the on-screen rectangle (physical pixels) of the word that contains
    /// the character at <paramref name="charPosition"/> in the spoken text,
    /// or null if it isn't known (yet).
    /// </summary>
    public Rect? GetWordRect(int charPosition)
    {
        int index = WordIndexAt(charPosition);
        if (index < 0) return null;
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
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
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

    // ─── Strategy 1: UI Automation TextPattern ──────────────────

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

        Strategy = "UIA TextPattern";
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
                lock (_rects) _rects[i] = rect;
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

    // ─── Strategy 2: OCR ────────────────────────────────────────

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

    private static OcrEngine? CreateOcrEngine(CultureInfo? culture)
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
    private static (byte[] png, Rect rect) CaptureScreen(Rect elementRect)
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
