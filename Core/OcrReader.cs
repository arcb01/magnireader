using System.IO;
using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MagniReader.Core;

/// <summary>
/// Reading fallback for apps that don't expose their text to UI Automation
/// (images of text, some games, remote desktops, canvas-drawn apps): screenshots
/// the area around the click, recognises it with Windows OCR and returns the
/// paragraph under the cursor and the ones below it.
/// </summary>
public static class OcrReader
{
    // Area recognised around the click, in physical pixels. It reaches further down
    // than up, so the paragraphs after the clicked one are read too.
    private const int HalfWidth = 800;
    private const int Above = 350;
    private const int Below = 950;

    /// <summary>Most paragraphs returned after the clicked one.</summary>
    private const int MaxFollowing = 20;

    /// <summary>True when Windows has a text recognition (OCR) language installed.</summary>
    public static bool IsAvailable => WordLocator.CreateOcrEngine(null) != null;

    /// <summary>
    /// Returns the paragraph of recognised text at (x, y), or null when OCR isn't
    /// available or finds nothing there. Safe to call off the UI thread.
    /// </summary>
    public static async Task<TextExtractor.TextElement?> ReadParagraphAtAsync(int x, int y, CancellationToken token = default)
    {
        var paragraphs = await ReadParagraphsFromAsync(x, y, token);
        return paragraphs.Count > 0 ? paragraphs[0] : null;
    }

    /// <summary>
    /// Returns the paragraph of recognised text at (x, y) followed by the paragraphs
    /// below it in the same column, for continuous reading. Empty when OCR isn't
    /// available or finds nothing at the click. Safe to call off the UI thread.
    /// </summary>
    public static async Task<List<TextExtractor.TextElement>> ReadParagraphsFromAsync(int x, int y, CancellationToken token = default)
    {
        var found = new List<TextExtractor.TextElement>();
        var engine = WordLocator.CreateOcrEngine(null);
        if (engine == null) return found;

        var area = new Rect(x - HalfWidth, y - Above, HalfWidth * 2, Above + Below);
        byte[] png;
        Rect captureRect;
        try { (png, captureRect) = WordLocator.CaptureScreen(area); }
        catch { return found; }

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

        // Lines with their screen rectangles
        var lines = new List<(string text, Rect rect)>();
        foreach (var line in result.Lines)
        {
            var r = Rect.Empty;
            foreach (var w in line.Words)
            {
                var b = w.BoundingRect;
                r.Union(new Rect(captureRect.X + b.X / scale, captureRect.Y + b.Y / scale, b.Width / scale, b.Height / scale));
            }
            if (!r.IsEmpty && !string.IsNullOrWhiteSpace(line.Text))
                lines.Add((line.Text.Trim(), r));
        }
        if (lines.Count == 0) return found;

        // The line under the click, or the nearest one within a line height
        int hit = -1;
        double best = double.MaxValue;
        for (int i = 0; i < lines.Count; i++)
        {
            var r = lines[i].rect;
            double dx = x < r.Left ? r.Left - x : x > r.Right ? x - r.Right : 0;
            double dy = y < r.Top ? r.Top - y : y > r.Bottom ? y - r.Bottom : 0;
            if (dx > r.Height * 2 || dy > r.Height) continue;
            double d = dx + dy * 2;
            if (d < best) { best = d; hit = i; }
        }
        if (hit < 0) return found;

        // Grow to the paragraph: neighbouring lines that overlap horizontally and
        // follow with a normal line gap.
        var ordered = lines.OrderBy(l => l.rect.Top).ToList();
        int idx = ordered.IndexOf(lines[hit]);
        int first = idx, last = idx;
        while (first > 0 && SameParagraph(ordered[first - 1].rect, ordered[first].rect)) first--;
        while (last < ordered.Count - 1 && SameParagraph(ordered[last].rect, ordered[last + 1].rect)) last++;

        var paragraph = Join(ordered, first, last);
        if (paragraph == null) return found;
        found.Add(paragraph);

        // The paragraphs below it in the same column (a document page, an article),
        // so reading goes on after the clicked one. Lines beside the column (a
        // sidebar, the other page) are skipped; a big gap (a picture) ends it.
        double colLeft = paragraph.BoundingRect.Left, colRight = paragraph.BoundingRect.Right;
        double lineHeight = paragraph.BoundingRect.Height / (last - first + 1);
        double bottom = paragraph.BoundingRect.Bottom;
        var below = ordered.Skip(last + 1)
            .Where(l => l.rect.Top >= bottom - lineHeight * 0.3 &&
                        l.rect.Left < colRight && colLeft < l.rect.Right &&
                        l.rect.Height < lineHeight * 2.5)
            .ToList();

        int i2 = 0;
        while (i2 < below.Count && found.Count <= MaxFollowing)
        {
            if (below[i2].rect.Top - bottom > Math.Max(lineHeight, below[i2].rect.Height) * 4) break;
            int end = i2;
            while (end < below.Count - 1 && SameParagraph(below[end].rect, below[end + 1].rect)) end++;
            var next = Join(below, i2, end);
            if (next != null)
            {
                found.Add(next);
                bottom = next.BoundingRect.Bottom;
            }
            i2 = end + 1;
        }
        return found;
    }

    /// <summary>Joins lines first..last into one paragraph, re-joining hyphenated words.</summary>
    private static TextExtractor.TextElement? Join(List<(string text, Rect rect)> lines, int first, int last)
    {
        var sb = new System.Text.StringBuilder();
        var bounds = Rect.Empty;
        for (int i = first; i <= last; i++)
        {
            var t = lines[i].text;
            if (sb.Length > 0)
            {
                if (sb[^1] == '-' && sb.Length > 1 && char.IsLetter(sb[^2])) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(t);
            bounds.Union(lines[i].rect);
        }

        var text = sb.ToString().Trim();
        if (text.Count(char.IsLetter) < 2) return null;
        return new TextExtractor.TextElement { Text = text, BoundingRect = bounds };
    }

    private static bool SameParagraph(Rect upper, Rect lower)
    {
        double h = Math.Max(upper.Height, lower.Height);
        double gap = lower.Top - upper.Bottom;
        bool overlapX = lower.Left < upper.Right && upper.Left < lower.Right;
        bool similarHeight = Math.Min(upper.Height, lower.Height) > h * 0.6;
        return overlapX && similarHeight && gap >= -h * 0.3 && gap < h * 0.9;
    }
}
