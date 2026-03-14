using System.Windows;
using System.Windows.Automation;

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
    }

    /// <summary>
    /// Extracts text at the given screen coordinates.
    /// Returns the clicked element PLUS following siblings for continuous reading.
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
        string? text = GetTextFromElement(element);
        text = CleanText(text);
        if (!string.IsNullOrWhiteSpace(text) && IsReadableText(text))
        {
            var rect = GetSafeRect(element);
            return new TextElement { Text = text.Trim(), BoundingRect = rect };
        }
        return null;
    }

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
    private string? GetTextFromElement(AutomationElement element)
    {
        try
        {
            // TextPattern (documents, browsers) — best source for real content
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? textPatternObj))
            {
                var text = ((TextPattern)textPatternObj).DocumentRange.GetText(10000);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            // Name property — usually the visible label/text
            var name = element.Current.Name;
            if (!string.IsNullOrWhiteSpace(name) && !LooksLikeUrl(name))
                return name;

            // ValuePattern (text boxes, links) — skip if it looks like a URL
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePatternObj))
            {
                var text = ((ValuePattern)valuePatternObj).Current.Value;
                if (!string.IsNullOrWhiteSpace(text) && !LooksLikeUrl(text))
                    return text;
            }

            // Fall back to Name even if it was skipped
            if (!string.IsNullOrWhiteSpace(name)) return name;
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
