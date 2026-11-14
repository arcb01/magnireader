using System.IO;
using System.Windows;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MagniReader.Core;

/// <summary>
/// Reading fallback for apps that don't expose their text to UI Automation
/// (images of text, some games, remote desktops, canvas-drawn apps): screenshots
/// the area around the click, recognises it with Windows OCR and returns the
/// paragraph under the cursor.
/// </summary>
public static class OcrReader
{
    // Area around the click that is recognised, in physical pixels
    private const int HalfWidth = 800;
    private const int HalfHeight = 450;

    /// <summary>
    /// Returns the paragraph of recognised text at (x, y), or null when OCR isn't
    /// available or finds nothing there. Safe to call off the UI thread.
    /// </summary>
    public static async Task<TextExtractor.TextElement?> ReadParagraphAtAsync(int x, int y, CancellationToken token = default)
    {
        var engine = WordLocator.CreateOcrEngine(null);
        if (engine == null) return null;

        var area = new Rect(x - HalfWidth, y - HalfHeight, HalfWidth * 2, HalfHeight * 2);
        byte[] png;
        Rect captureRect;
        try { (png, captureRect) = WordLocator.CaptureScreen(area); }
        catch { return null; }

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
        if (lines.Count == 0) return null;

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
        if (hit < 0) return null;

        // Grow to the paragraph: neighbouring lines that overlap horizontally and
        // follow with a normal line gap.
        var ordered = lines.OrderBy(l => l.rect.Top).ToList();
        int idx = ordered.IndexOf(lines[hit]);
        int first = idx, last = idx;
        while (first > 0 && SameParagraph(ordered[first - 1].rect, ordered[first].rect)) first--;
        while (last < ordered.Count - 1 && SameParagraph(ordered[last].rect, ordered[last + 1].rect)) last++;

        var sb = new System.Text.StringBuilder();
        var bounds = Rect.Empty;
        for (int i = first; i <= last; i++)
        {
            var t = ordered[i].text;
            if (sb.Length > 0)
            {
                // Re-join words hyphenated across lines
                if (sb[^1] == '-' && sb.Length > 1 && char.IsLetter(sb[^2])) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(t);
            bounds.Union(ordered[i].rect);
        }

        var text = sb.ToString().Trim();
        if (text.Length == 0) return null;
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
