using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MagniReader.Core;

/// <summary>
/// Sends a screenshot to a Vision Language Model (Gemini or OpenAI) for analysis.
/// Returns a text description/summary of the image content.
/// </summary>
public class VisionAnalyzer : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public enum Provider { Gemini, OpenAI }

    // Gemini models to try in order (different tiers have access to different models)
    private static readonly string[] GeminiModels = [
        "gemini-2.5-pro",
        "gemini-2.5-flash"
    ];

    private const string PromptQuick =
        "Briefly describe this screenshot in 2-3 sentences. What is it showing? " +
        "Mention any visible text or key information. " +
        "Respond in the same language as any text visible in the image.";

    private const string PromptDetailed =
        "Analyze this screenshot thoroughly. Describe everything you see in detail: " +
        "all visible text (transcribe it), UI elements, layout, colors, icons, images, " +
        "what application or website this appears to be, and any context you can infer. " +
        "Be comprehensive. Respond in the same language as any text visible in the image.";

    /// <summary>
    /// Analyzes an image using the configured VLM provider.
    /// When detailed=false, returns a brief summary. When detailed=true, returns a thorough analysis.
    /// </summary>
    public async Task<string> AnalyzeImageAsync(byte[] imageBytes, Provider provider, string apiKey, bool detailed = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "⚠ No API key configured. Go to Settings → Vision to set up your API key.";

        try
        {
            var prompt = detailed ? PromptDetailed : PromptQuick;
            return provider switch
            {
                Provider.Gemini => await AnalyzeWithGemini(imageBytes, apiKey, prompt),
                Provider.OpenAI => await AnalyzeWithOpenAI(imageBytes, apiKey, prompt),
                _ => "Unknown provider"
            };
        }
        catch (TaskCanceledException)
        {
            return "⚠ Request timed out. Check your internet connection.";
        }
        catch (HttpRequestException ex)
        {
            return $"⚠ Network error: {ex.Message}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VisionAnalyzer] Error: {ex}");
            return $"⚠ Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Validates the API key by making a lightweight request.
    /// Returns (success, message).
    /// </summary>
    public async Task<(bool success, string message)> ValidateKeyAsync(Provider provider, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "No API key provided.");

        try
        {
            if (provider == Provider.Gemini)
            {
                // Try listing models — lightweight call to verify the key
                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}";
                var response = await _http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    if (body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                        return (false, "Invalid API key.");
                    return (false, $"Error: {response.StatusCode}");
                }

                // Find which vision model is available
                using var doc = JsonDocument.Parse(body);
                var models = doc.RootElement.GetProperty("models");
                var visionModels = new List<string>();
                foreach (var model in models.EnumerateArray())
                {
                    var name = model.GetProperty("name").GetString() ?? "";
                    if (name.Contains("gemini", StringComparison.OrdinalIgnoreCase))
                        visionModels.Add(name);
                }

                var bestModel = FindBestGeminiModel(visionModels);
                return (true, $"✅ Key valid! Available model: {bestModel ?? "unknown"}\n{visionModels.Count} Gemini model(s) found.");
            }
            else
            {
                // OpenAI: try listing models
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                var response = await _http.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        return (false, "Invalid API key.");
                    return (false, $"Error: {response.StatusCode}");
                }

                return (true, "✅ OpenAI key valid! Model: gpt-4o-mini");
            }
        }
        catch (TaskCanceledException)
        {
            return (false, "Request timed out. Check your internet connection.");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    private async Task<string> AnalyzeWithGemini(byte[] imageBytes, string apiKey, string prompt)
    {
        var base64 = Convert.ToBase64String(imageBytes);

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new { inline_data = new { mime_type = "image/png", data = base64 } }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);

        // Try each model until one works
        string lastError = "";
        foreach (var model in GeminiModels)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            System.Diagnostics.Debug.WriteLine($"[VisionAnalyzer] Trying Gemini model: {model}");
            var response = await _http.PostAsync(url, content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                System.Diagnostics.Debug.WriteLine($"[VisionAnalyzer] Model '{model}' not found, trying next...");
                lastError = $"Model '{model}' not available";
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[VisionAnalyzer] Gemini error ({model}): {responseJson}");
                if (responseJson.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                    return "⚠ Invalid Gemini API key. Check your key in Settings → Vision.";
                lastError = $"API error: {response.StatusCode}";
                continue;
            }

            // Parse response
            using var doc = JsonDocument.Parse(responseJson);
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() > 0)
            {
                var parts = candidates[0].GetProperty("content").GetProperty("parts");
                if (parts.GetArrayLength() > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[VisionAnalyzer] Success with model: {model}");
                    return parts[0].GetProperty("text").GetString() ?? "No response from Gemini.";
                }
            }

            return "No response from Gemini.";
        }

        return $"⚠ No compatible Gemini model found. Last error: {lastError}. Check your API key permissions.";
    }

    private async Task<string> AnalyzeWithOpenAI(byte[] imageBytes, string apiKey, string prompt)
    {
        var base64 = Convert.ToBase64String(imageBytes);

        var payload = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64}" } }
                    }
                }
            },
            max_tokens = 1000
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = content;

        var response = await _http.SendAsync(request);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine($"[VisionAnalyzer] OpenAI error: {responseJson}");
            if (responseJson.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase))
                return "⚠ Invalid OpenAI API key. Check your key in Settings → Vision.";
            return $"⚠ OpenAI API error ({response.StatusCode}). Check your API key.";
        }

        using var doc = JsonDocument.Parse(responseJson);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() > 0)
        {
            return choices[0].GetProperty("message").GetProperty("content").GetString()
                ?? "No response from OpenAI.";
        }

        return "No response from OpenAI.";
    }

    private static string? FindBestGeminiModel(List<string> availableModels)
    {
        foreach (var preferred in GeminiModels)
        {
            var match = availableModels.FirstOrDefault(m =>
                m.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return availableModels.FirstOrDefault();
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
