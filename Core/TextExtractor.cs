using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace MagniReader.Core;

/// <summary>
/// Extracts text from UI elements using the Windows UI Automation API.
/// 
/// Strategy:
///   1. Get element at exact click point
///   2. If it has text → use it
///   3. If not → search siblings/nearby elements for the closest one with text
///   4. Return the bounding rect of the actual text element for in-place highlighting
/// </summary>
public class TextExtractor
{
    public class TextElement
    {
        public string Text { get; set; } = string.Empty;
        public Rect BoundingRect { get; set; }

        /// <summary>
        /// The UI Automation element the text was read from, when the text came
        /// from a single element. Used to locate individual words on screen.
        /// Null when the text was combined from several children.
        /// </summary>
        public AutomationElement? Source { get; set; }

        /// <summary>
        /// The document range the text came from (a whole paragraph in a browser or
        /// document). Lets the word locator search only inside this paragraph.
        /// </summary>
        public TextPatternRange? Range { get; set; }

        /// <summary>
        /// True when the text may not be what's under the click: a label of a big
        /// container ("Page 1", the document's title), the start of a whole document,
        /// or the nearest text found next to the click. Word processors and canvas
        /// apps (Google Docs) give results like these, so the caller tries OCR first
        /// and only uses this text when OCR finds nothing.
        /// </summary>
        public bool Uncertain { get; set; }
    }

    /// <summary>
    /// Context from a fast extraction, used to defer sibling collection.
    /// </summary>
    public class ExtractionContext
    {
        internal AutomationElement? Element { get; set; }
        internal AutomationElement? SiblingStartElement { get; set; }
        internal int ClickX { get; set; }
        internal int ClickY { get; set; }
        /// <summary>Set when reading by paragraphs of a document (browser, Word…).</summary>
        internal TextPatternRange? ParagraphRange { get; set; }
        internal AutomationElement? DocumentElement { get; set; }

        /// <summary>How the text was found ("paragraph", "element", "children", "nearest", "none"). For the log.</summary>
        public string Method { get; internal set; } = "none";

        /// <summary>The app and element under the click, e.g. "WINWORD Document (_WwG)". For the log.</summary>
        public string Where { get; internal set; } = "";
    }

    /// <summary>
    /// Fast extraction: gets ONLY the first text element at the click point.
    /// Skips sibling traversal for minimum latency.
    /// Returns the extraction context so siblings can be collected later.
    /// </summary>
    public (TextElement? firstElement, ExtractionContext? context) ExtractFirstElementAtPoint(int x, int y)
    {
        var ctx = new ExtractionContext { ClickX = x, ClickY = y };
        try
        {
            var point = new System.Windows.Point(x, y);
            var element = AutomationElement.FromPoint(point);
            if (element == null) return (null, ctx);

            ctx.Element = element;
            ctx.Where = DescribeElement(element);
            System.Diagnostics.Debug.WriteLine(
                $"[TextExtractor] Click at ({x},{y}): " +
                $"Name='{Truncate(element.Current.Name, 60)}', " +
                $"Type={element.Current.ControlType.ProgrammaticName}");

            // STEP 0: Inside a web page or document, read the whole paragraph under the
            // click. Browsers split a paragraph into many UIA elements (text runs, links,
            // citation marks); reading them one by one restarted the voice and the
            // language detection for every fragment.
            var paragraph = TryExtractParagraphAtPoint(element, point, ctx);
            if (paragraph != null)
            {
                ctx.Method = "paragraph";
                return (paragraph, ctx);
            }

            // STEP 1: Try the exact element
            var result = TryExtractFromElement(element);
            if (result != null)
            {
                ctx.Method = "element";
                ctx.SiblingStartElement = element;
                return (result, ctx);
            }

            // STEP 2: Try immediate children (the element might be a container)
            var childResult = TryChildren(element, maxChildren: 10, maxDepth: 2);
            if (childResult != null)
            {
                ctx.Method = "children";
                // Text of children that aren't under the click (toolbar labels, page numbers…)
                if (!NearPoint(childResult.BoundingRect, x, y)) childResult.Uncertain = true;
                ctx.SiblingStartElement = element;
                return (childResult, ctx);
            }

            // STEP 3: Walk up to parent and find the nearest sibling with text
            var nearestResult = FindNearestTextElement(element, x, y);
            if (nearestResult != null)
            {
                ctx.Method = "nearest";
                if (!NearPoint(nearestResult.BoundingRect, x, y)) nearestResult.Uncertain = true;
                // Find the matching automation element for later sibling collection
                var walker = TreeWalker.ControlViewWalker;
                var parent = walker.GetParent(element);
                if (parent != null)
                {
                    var sibling = walker.GetFirstChild(parent);
                    while (sibling != null)
                    {
                        var txt = GetTextFromElement(sibling);
                        if (txt != null && txt.Trim() == nearestResult.Text)
                        {
                            ctx.SiblingStartElement = sibling;
                            break;
                        }
                        sibling = walker.GetNextSibling(sibling);
                    }
                }
                return (nearestResult, ctx);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextExtractor] Error: {ex.Message}");
        }

        return (null, ctx);
    }

    /// <summary>"WINWORD Document (_WwG)": the app and the kind of element, for the log. Never the text.</summary>
    private static string DescribeElement(AutomationElement element)
    {
        try
        {
            string process = "";
            try { process = System.Diagnostics.Process.GetProcessById(element.Current.ProcessId).ProcessName; }
            catch { }
            var type = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            var cls = element.Current.ClassName;
            return string.IsNullOrEmpty(cls) ? $"{process} {type}" : $"{process} {type} ({Truncate(cls, 40)})";
        }
        catch { return "?"; }
    }

    /// <summary>
    /// True when the screen rect contains the point, give or take a few pixels.
    /// An empty rect can't be checked and counts as false.
    /// </summary>
    private static bool NearPoint(Rect rect, int x, int y, double tolerance = 8)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return false;
        var r = rect;
        r.Inflate(tolerance, tolerance);
        return r.Contains(new System.Windows.Point(x, y));
    }

    /// <summary>Elements this big are page or window containers, not a piece of text.</summary>
    private static bool IsLargeContainer(Rect rect) =>
        !rect.IsEmpty && rect.Width > 500 && rect.Height > 300;

    /// <summary>
    /// Collects following siblings from a previous extraction context.
    /// Designed to be called on a background thread after speech has started.
    /// </summary>
    public List<TextElement> CollectSiblingsFromContext(ExtractionContext context)
    {
        var elements = new List<TextElement>();
        try
        {
            if (context.ParagraphRange != null && context.DocumentElement != null)
            {
                CollectFollowingParagraphs(context.ParagraphRange, context.DocumentElement, elements, maxCount: 30);
                return elements;
            }
            if (context.SiblingStartElement != null)
            {
                CollectFollowingSiblings(context.SiblingStartElement, elements, maxCount: 10);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextExtractor] Sibling collection error: {ex.Message}");
        }
        return elements;
    }

    /// <summary>
    /// Extracts text at the given screen coordinates.
    /// Returns the clicked element PLUS following siblings for continuous reading.
    /// (Legacy method — kept for compatibility)
    /// </summary>
    public List<TextElement> ExtractTextAtPoint(int x, int y)
    {
        var elements = new List<TextElement>();

        try
        {
            var point = new System.Windows.Point(x, y);
            var element = AutomationElement.FromPoint(point);
            if (element == null) return elements;

            System.Diagnostics.Debug.WriteLine(
                $"[TextExtractor] Click at ({x},{y}): " +
                $"Name='{Truncate(element.Current.Name, 60)}', " +
                $"Type={element.Current.ControlType.ProgrammaticName}");

            // STEP 1: Try the exact element
            var result = TryExtractFromElement(element);
            if (result != null)
            {
                elements.Add(result);
                // Collect following siblings for continuous reading
                CollectFollowingSiblings(element, elements, maxCount: 10);
                return elements;
            }

            // STEP 2: Try immediate children (the element might be a container)
            var childResult = TryChildren(element, maxChildren: 10, maxDepth: 2);
            if (childResult != null)
            {
                elements.Add(childResult);
                CollectFollowingSiblings(element, elements, maxCount: 10);
                return elements;
            }

            // STEP 3: Walk up to parent and find the nearest sibling with text
            var nearestResult = FindNearestTextElement(element, x, y);
            if (nearestResult != null)
            {
                elements.Add(nearestResult);
                // Try to find the matching automation element for continuous reading
                var walker = TreeWalker.ControlViewWalker;
                var parent = walker.GetParent(element);
                if (parent != null)
                {
                    var sibling = walker.GetFirstChild(parent);
                    while (sibling != null)
                    {
                        var txt = GetTextFromElement(sibling);
                        if (txt != null && txt.Trim() == nearestResult.Text)
                        {
                            CollectFollowingSiblings(sibling, elements, maxCount: 10);
                            break;
                        }
                        sibling = walker.GetNextSibling(sibling);
                    }
                }
                return elements;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextExtractor] Error: {ex.Message}");
        }

        return elements;
    }

    // ─── Selected text ───────────────────────────────────────────

    private const int MaxSelectionChars = 20000;

    /// <summary>
    /// Returns the text selected in the focused app (via UI Automation TextPattern),
    /// or null when there is no selection or the app doesn't expose it.
    /// </summary>
    public TextElement? ExtractSelection()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            var walker = TreeWalker.ControlViewWalker;
            for (int depth = 0; element != null && depth < 6; depth++, element = walker.GetParent(element))
            {
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var obj)) continue;
                var pattern = (TextPattern)obj;
                if (pattern.SupportedTextSelection == SupportedTextSelection.None) continue;

                var ranges = pattern.GetSelection();
                if (ranges == null || ranges.Length == 0) return null;

                var sb = new System.Text.StringBuilder();
                var bounds = Rect.Empty;
                foreach (var range in ranges)
                {
                    var part = range.GetText(MaxSelectionChars - sb.Length);
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(part);
                    }
                    try
                    {
                        foreach (var r in range.GetBoundingRectangles())
                            if (!r.IsEmpty && r.Width > 0 && r.Height > 0) bounds.Union(r);
                    }
                    catch { }
                    if (sb.Length >= MaxSelectionChars) break;
                }

                var text = CleanText(sb.ToString());
                if (string.IsNullOrWhiteSpace(text)) return null;
                return new TextElement
                {
                    Text = text,
                    BoundingRect = bounds,
                    Source = element,
                    Range = ranges.Length == 1 ? ranges[0] : null,
                };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextExtractor] Selection error: {ex.Message}");
        }
        return null;
    }

    // ─── Paragraph reading (browsers, documents) ─────────────────

    private const int MaxParagraphChars = 4000;

    /// <summary>
    /// If the click is inside a document that exposes UIA TextPattern (Chrome, Edge,
    /// Firefox, Word…), returns the full paragraph under the click, links included.
    /// Returns null to use the element-by-element extraction instead.
    /// </summary>
    private TextElement? TryExtractParagraphAtPoint(AutomationElement element, System.Windows.Point point, ExtractionContext ctx)
    {
        try
        {
            var doc = FindDocumentAncestor(element);
            if (doc == null) return null;
            if (!doc.TryGetCurrentPattern(TextPattern.Pattern, out var obj)) return null;
            var pattern = (TextPattern)obj;

            var range = pattern.RangeFromPoint(point);
            range.ExpandToEnclosingUnit(TextUnit.Paragraph);
            var para = MakeParagraphElement(range, doc);
            if (para == null) return null;

            // Apps that draw their text on a canvas (Google Docs) or keep it off screen
            // for screen readers return a range that isn't where the user clicked.
            if (!RangeContainsPoint(range, point))
                para.Uncertain = true;

            ctx.ParagraphRange = range;
            ctx.DocumentElement = doc;
            System.Diagnostics.Debug.WriteLine(
                $"[TextExtractor] Paragraph at click: '{Truncate(para.Text, 60)}' ({para.Text.Length} chars)");
            return para;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextExtractor] Paragraph extraction failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when one of the range's line rectangles is under the point (a few pixels of slack).</summary>
    private static bool RangeContainsPoint(TextPatternRange range, System.Windows.Point point)
    {
        try
        {
            foreach (var r in range.GetBoundingRectangles())
                if (NearPoint(r, (int)point.X, (int)point.Y, tolerance: 6)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// The element itself or its nearest ancestor that is a document or rich edit
    /// box with TextPattern (browsers, Word, Notepad, WordPad…).
    /// </summary>
    private static AutomationElement? FindDocumentAncestor(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (int depth = 0; current != null && depth < 25; depth++)
        {
            try
            {
                if (current == AutomationElement.RootElement) return null;
                var type = current.Current.ControlType;
                if ((type == ControlType.Document || type == ControlType.Edit) &&
                    (bool)current.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty))
                    return current;
            }
            catch { return null; }
            current = walker.GetParent(current);
        }
        return null;
    }

    private static TextElement? MakeParagraphElement(TextPatternRange range, AutomationElement doc)
    {
        var raw = range.GetText(MaxParagraphChars + 1);
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxParagraphChars) return null;
        var text = CleanText(raw);
        if (string.IsNullOrWhiteSpace(text) || !IsReadableText(text)) return null;

        Rect rect = Rect.Empty;
        try
        {
            foreach (var r in range.GetBoundingRectangles())
                if (!r.IsEmpty && r.Width > 0 && r.Height > 0) rect = rect.IsEmpty ? r : Rect.Union(rect, r);
        }
        catch { }

        return new TextElement { Text = text.Trim(), BoundingRect = rect, Source = doc, Range = range };
    }

    /// <summary>Collects the paragraphs after <paramref name="start"/> for continuous reading.</summary>
    private static void CollectFollowingParagraphs(TextPatternRange start, AutomationElement doc,
        List<TextElement> elements, int maxCount)
    {
        var range = start.Clone();
        for (int steps = 0; steps < maxCount * 3 && elements.Count < maxCount; steps++)
        {
            int moved;
            try { moved = range.Move(TextUnit.Paragraph, 1); }
            catch { break; }
            if (moved == 0) break;
            try { range.ExpandToEnclosingUnit(TextUnit.Paragraph); } catch { break; }

            var para = MakeParagraphElement(range.Clone(), doc);
            if (para != null && (elements.Count == 0 || elements[^1].Text != para.Text))
                elements.Add(para);
        }
        System.Diagnostics.Debug.WriteLine($"[TextExtractor] Collected {elements.Count} following paragraph(s)");
    }

    /// <summary>
    /// Collects text from following sibling elements for continuous reading.
    /// </summary>
    private void CollectFollowingSiblings(AutomationElement startElement, List<TextElement> elements, int maxCount)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var sibling = walker.GetNextSibling(startElement);
            int count = 0;

            while (sibling != null && count < maxCount)
            {
                var result = TryExtractFromElement(sibling);
                if (result == null)
                {
                    // Try one level of children
                    result = TryChildren(sibling, maxChildren: 5, maxDepth: 1);
                }

                if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                {
                    elements.Add(result);
                    count++;
                }

                sibling = walker.GetNextSibling(sibling);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[TextExtractor] Collected {count} following siblings for continuous reading");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TextExtractor] Sibling collection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to extract text from a single element.
    /// Returns null if no text found.
    /// </summary>
    private TextElement? TryExtractFromElement(AutomationElement element)
    {
        string? text = GetTextFromElement(element, out var origin);
        text = CleanText(text);
        if (!string.IsNullOrWhiteSpace(text) && IsReadableText(text))
        {
            var rect = GetSafeRect(element);
            // A page-sized element gives either its label ("Page 1", the file name) or,
            // through TextPattern, its whole text from the top: neither is what was clicked.
            bool uncertain = IsLargeContainer(rect) &&
                             (origin == TextOrigin.WholeDocument || (origin == TextOrigin.Name && text.Length < 60));
            return new TextElement { Text = text.Trim(), BoundingRect = rect, Source = element, Uncertain = uncertain };
        }
        return null;
    }

    private enum TextOrigin { None, WholeDocument, Name, Value }

    /// <summary>
    /// Searches children of an element for text.
    /// Returns the combined text or the first child with text.
    /// </summary>
    private TextElement? TryChildren(AutomationElement parent, int maxChildren, int maxDepth, int depth = 0)
    {
        if (depth >= maxDepth) return null;

        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(parent);
            int count = 0;
            var texts = new List<(string text, Rect rect)>();

            while (child != null && count < maxChildren)
            {
                string? childText = GetTextFromElement(child);
                if (!string.IsNullOrWhiteSpace(childText) && childText.Length > 0)
                {
                    texts.Add((childText.Trim(), GetSafeRect(child)));
                }
                else if (depth + 1 < maxDepth)
                {
                    var deepResult = TryChildren(child, maxChildren: 5, maxDepth, depth + 1);
                    if (deepResult != null)
                        return deepResult;
                }

                child = walker.GetNextSibling(child);
                count++;
            }

            if (texts.Count > 0)
            {
                // Combine all child texts, use union of bounding rects
                string combined = string.Join(" ", texts.Select(t => t.text));
                var rects = texts.Select(t => t.rect).Where(r => r != Rect.Empty).ToList();
                Rect combinedRect = rects.Count > 0
                    ? new Rect(
                        rects.Min(r => r.X), rects.Min(r => r.Y),
                        rects.Max(r => r.X + r.Width) - rects.Min(r => r.X),
                        rects.Max(r => r.Y + r.Height) - rects.Min(r => r.Y))
                    : GetSafeRect(parent);

                return new TextElement { Text = combined, BoundingRect = combinedRect };
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Finds the nearest element with text when the clicked element has none.
    /// Walks up to the parent and checks all siblings, picking the closest by screen distance.
    /// </summary>
    private TextElement? FindNearestTextElement(AutomationElement clickedElement, int clickX, int clickY)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var parent = walker.GetParent(clickedElement);
            if (parent == null || parent == AutomationElement.RootElement)
                return null;

            // Try parent's text first
            var parentResult = TryExtractFromElement(parent);
            if (parentResult != null) return parentResult;

            // Search all siblings for the closest one with text
            var candidates = new List<(TextElement element, double distance)>();
            var sibling = walker.GetFirstChild(parent);
            int count = 0;

            while (sibling != null && count < 30)
            {
                string? text = GetTextFromElement(sibling);
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 0)
                {
                    var rect = GetSafeRect(sibling);
                    if (rect != Rect.Empty)
                    {
                        // Calculate distance from click point to element center
                        double centerX = rect.X + rect.Width / 2;
                        double centerY = rect.Y + rect.Height / 2;
                        double dist = Math.Sqrt(
                            Math.Pow(clickX - centerX, 2) +
                            Math.Pow(clickY - centerY, 2));

                        candidates.Add((
                            new TextElement { Text = text.Trim(), BoundingRect = rect },
                            dist));
                    }
                }
                else
                {
                    // Check one level deeper in siblings
                    var childResult = TryChildren(sibling, maxChildren: 5, maxDepth: 1);
                    if (childResult != null)
                    {
                        var rect = childResult.BoundingRect;
                        if (rect != Rect.Empty)
                        {
                            double centerX = rect.X + rect.Width / 2;
                            double centerY = rect.Y + rect.Height / 2;
                            double dist = Math.Sqrt(
                                Math.Pow(clickX - centerX, 2) +
                                Math.Pow(clickY - centerY, 2));
                            candidates.Add((childResult, dist));
                        }
                    }
                }

                sibling = walker.GetNextSibling(sibling);
                count++;
            }

            if (candidates.Count > 0)
            {
                // Return the closest element
                var nearest = candidates.OrderBy(c => c.distance).First();
                System.Diagnostics.Debug.WriteLine(
                    $"[TextExtractor] Found nearest text: '{Truncate(nearest.element.Text, 60)}' " +
                    $"at distance {nearest.distance:F0}px");
                return nearest.element;
            }

            // Last resort: walk up one more level
            var grandparent = walker.GetParent(parent);
            if (grandparent != null && grandparent != AutomationElement.RootElement)
            {
                var gpResult = TryExtractFromElement(grandparent);
                if (gpResult != null) return gpResult;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Gets text from a single element. Priority: TextPattern → Name → ValuePattern.
    /// Avoids returning bare URLs from ValuePattern (common in browsers).
    /// </summary>
    private string? GetTextFromElement(AutomationElement element) => GetTextFromElement(element, out _);

    private string? GetTextFromElement(AutomationElement element, out TextOrigin origin)
    {
        origin = TextOrigin.None;
        try
        {
            // TextPattern (documents, browsers) — best source for real content
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? textPatternObj))
            {
                var text = ((TextPattern)textPatternObj).DocumentRange.GetText(10000);
                if (!string.IsNullOrWhiteSpace(text)) { origin = TextOrigin.WholeDocument; return text; }
            }

            // Name property — usually the visible label/text
            var name = element.Current.Name;
            if (!string.IsNullOrWhiteSpace(name) && !LooksLikeUrl(name))
            {
                origin = TextOrigin.Name;
                return name;
            }

            // ValuePattern (text boxes, links) — skip if it looks like a URL
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePatternObj))
            {
                var text = ((ValuePattern)valuePatternObj).Current.Value;
                if (!string.IsNullOrWhiteSpace(text) && !LooksLikeUrl(text))
                {
                    origin = TextOrigin.Value;
                    return text;
                }
            }

            // Fall back to Name even if it was skipped
            if (!string.IsNullOrWhiteSpace(name)) { origin = TextOrigin.Name; return name; }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Cleans extracted text: removes URLs, normalizes whitespace, strips junk.
    /// </summary>
    private static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Remove URLs
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"https?://\S+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"www\.\S+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Normalize whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[\t ]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        text = text.Trim();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Checks if text is worth reading aloud.
    /// Rejects very short text, single characters, pure numbers, emojis-only, etc.
    /// </summary>
    private static bool IsReadableText(string text)
    {
        if (text.Length < 2) return false;

        // Count alphabetic characters
        int alphaCount = text.Count(c => char.IsLetter(c));
        if (alphaCount < 2) return false;

        // Reject if mostly non-readable (icons, symbols)
        double alphaRatio = (double)alphaCount / text.Length;
        if (alphaRatio < 0.3 && text.Length < 20) return false;

        return true;
    }

    private static bool LooksLikeUrl(string text)
    {
        var t = text.Trim();
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
               (t.Contains("://") && !t.Contains(' '));
    }

    private static Rect GetSafeRect(AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            if (rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height))
                return Rect.Empty;
            return rect;
        }
        catch { return Rect.Empty; }
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        text = text.Replace("\r", "").Replace("\n", " ");
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }
}
