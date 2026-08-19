using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MusicOrganiser.Models;

namespace MusicOrganiser.Services;

public class ArtistInfoResult
{
    public string Summary { get; set; } = string.Empty;
    public string? IdentifiedArtist { get; set; }
    public bool IsConfident { get; set; }
    public bool FromCache { get; set; }
}

public class ArtistInfoService
{
    private HttpClient? _httpClient;
    private string? _apiKey;
    private readonly ArtistInfoCache _cache;

    public ArtistInfoService()
    {
        _cache = ArtistInfoCache.Load();
        LoadApiKey();
    }

    private void LoadApiKey()
    {
        var currentDir = AppDomain.CurrentDomain.BaseDirectory;
        var envPath = FindEnvFile(currentDir);

        if (envPath != null)
        {
            DotNetEnv.Env.Load(envPath);
        }

        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient = new HttpClient();
            _httpClient.BaseAddress = new Uri("https://api.anthropic.com/");
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
    }

    private static string? FindEnvFile(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            var envPath = Path.Combine(dir, ".env");
            if (File.Exists(envPath))
            {
                return envPath;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    public string? DetectArtistName(string? tagArtist, string filePath)
    {
        if (!string.IsNullOrWhiteSpace(tagArtist))
        {
            return tagArtist.Trim();
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            var folderName = Path.GetFileName(directory);
            if (!string.IsNullOrWhiteSpace(folderName) && !IsDriveLetter(folderName))
            {
                return folderName;
            }

            var parentDir = Directory.GetParent(directory);
            if (parentDir != null)
            {
                var parentFolderName = parentDir.Name;
                if (!string.IsNullOrWhiteSpace(parentFolderName) && !IsDriveLetter(parentFolderName))
                {
                    return parentFolderName;
                }
            }
        }

        return null;
    }

    private static bool IsDriveLetter(string name)
    {
        return name.Length <= 3 && name.Contains(':');
    }

    public async Task<ArtistInfoResult> GetArtistSummaryAsync(string? artistName, string? songTitle, string? album, string fileName, bool forceRefresh = false)
    {
        if (_httpClient == null)
        {
            return new ArtistInfoResult
            {
                Summary = "API key not configured. Add ANTHROPIC_API_KEY to .env file.",
                IsConfident = false
            };
        }

        // Check persistent cache first (unless forcing refresh)
        if (!forceRefresh && !string.IsNullOrWhiteSpace(artistName))
        {
            var cached = _cache.Get(artistName);
            if (cached != null)
            {
                return new ArtistInfoResult
                {
                    Summary = cached.Summary,
                    IdentifiedArtist = cached.ArtistName,
                    IsConfident = true,
                    FromCache = true
                };
            }
        }

        try
        {
            // Build context with all available information
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("I need to identify a music artist. Here's the information from the audio file:");
            contextBuilder.AppendLine();

            if (!string.IsNullOrWhiteSpace(artistName))
                contextBuilder.AppendLine($"- Artist tag/folder: {artistName}");
            if (!string.IsNullOrWhiteSpace(songTitle))
                contextBuilder.AppendLine($"- Song title: {songTitle}");
            if (!string.IsNullOrWhiteSpace(album))
                contextBuilder.AppendLine($"- Album: {album}");

            var cleanFileName = Path.GetFileNameWithoutExtension(fileName);
            if (!string.IsNullOrWhiteSpace(cleanFileName))
                contextBuilder.AppendLine($"- File name: {cleanFileName}");

            contextBuilder.AppendLine();
            contextBuilder.AppendLine("Please use web search to find information about this artist. Search for the artist name combined with the song title if needed.");
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("IMPORTANT: At the start of your response, indicate your confidence:");
            contextBuilder.AppendLine("- Start with [CONFIDENT] if you found reliable information about the artist");
            contextBuilder.AppendLine("- Start with [UNCERTAIN] if you couldn't identify the artist reliably");
            contextBuilder.AppendLine();
            contextBuilder.AppendLine("Then provide a brief summary (2-3 sentences) about the artist including their genre and a notable fact.");

            var response = await SendQueryWithWebSearchAsync(contextBuilder.ToString());
            var result = ParseResponse(response, artistName);

            // Save to cache if confident
            if (result.IsConfident && !string.IsNullOrWhiteSpace(result.IdentifiedArtist))
            {
                _cache.Set(result.IdentifiedArtist, result.Summary, true);
            }

            return result;
        }
        catch (Exception ex)
        {
            return new ArtistInfoResult
            {
                Summary = $"Error: {ex.Message}",
                IsConfident = false
            };
        }
    }

    private ArtistInfoResult ParseResponse(string response, string? providedArtist)
    {
        var isConfident = response.Contains("[CONFIDENT]");
        var summary = response
            .Replace("[CONFIDENT]", "")
            .Replace("[UNCERTAIN]", "")
            .Trim();

        return new ArtistInfoResult
        {
            Summary = summary,
            IdentifiedArtist = isConfident ? providedArtist : null,
            IsConfident = isConfident,
            FromCache = false
        };
    }

    public void ClearCache(string? artistName)
    {
        if (!string.IsNullOrWhiteSpace(artistName))
        {
            _cache.Remove(artistName);
        }
    }

    public ArtistInfoResult? TryGetFromCache(string? artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return null;

        var cached = _cache.Get(artistName);
        if (cached != null)
        {
            return new ArtistInfoResult
            {
                Summary = cached.Summary,
                IdentifiedArtist = cached.ArtistName,
                IsConfident = true,
                FromCache = true
            };
        }

        return null;
    }

    /// <summary>Identifies a band from its folder name + album subfolder names via web search
    /// and suggests genre/style tags, preferring the given vocabulary's spellings. Returns
    /// null when no API key is configured or on error; an empty list when the band couldn't
    /// be reliably identified.</summary>
    public async Task<IReadOnlyList<string>?> SuggestFolderTagsAsync(string bandName, IReadOnlyList<string> albums, IReadOnlyList<string> existingTags)
    {
        if (_httpClient == null) return null;

        try
        {
            var promptBuilder = new StringBuilder();
            promptBuilder.AppendLine($"Band/artist folder name: {bandName}");
            if (albums.Count > 0)
            {
                promptBuilder.AppendLine("Album folders found inside it:");
                foreach (var album in albums.Take(20))
                    promptBuilder.AppendLine($"- {album}");
            }
            promptBuilder.AppendLine();
            promptBuilder.AppendLine($"Check Bandcamp specifically (\"{bandName}\" bandcamp) for this artist's own genre/style tags before falling back to general sources.");
            if (existingTags.Count > 0)
            {
                promptBuilder.AppendLine();
                promptBuilder.AppendLine("Existing tag vocabulary, reuse these exact spellings where they fit: " +
                    string.Join(", ", existingTags));
            }

            var response = await SendQueryWithWebSearchAsync(promptBuilder.ToString(), FolderTagSystemPrompt, 300);
            return ParseTagsResponse(response);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ParseTagsResponse(string response)
    {
        var line = response
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("TAGS:", StringComparison.OrdinalIgnoreCase));
        if (line == null) return Array.Empty<string>();

        var value = line[5..];
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private const string ArtistSystemPrompt = @"You are a music expert assistant. Your task is to identify music artists and provide brief, accurate summaries.

IMPORTANT: Always use the web_search tool to look up information about the artist. Search for:
1. The artist name + song title together
2. Just the song title if the artist is unknown
3. The artist name alone

Use the search results to provide accurate, up-to-date information about the artist.
Keep responses to 2-3 sentences. Include the artist's primary genre and one notable achievement or fact.";

    private const string FolderTagSystemPrompt = @"You are a music genre tagging assistant. Given a band/artist folder name and a list of their album folder names, identify the band via web search and suggest genre tags for their music.

Search Bandcamp specifically (e.g. ""<band name>"" bandcamp, or site:bandcamp.com <band name>) in addition to general web search — Bandcamp artist and album pages carry curated genre/style tags written by the artist or label, which are usually more precise than generic genre labels. Prefer Bandcamp's own tags when you find them; fall back to general web search otherwise.

Respond with ONLY a single line in this exact format:
TAGS: tag1, tag2, tag3

Provide 3-8 lowercase genre/style tags (genre, subgenre, and era only when clearly known — e.g. ""80s"", ""90s""). Do NOT include country, nationality, or location tags (no ""swedish"", ""german"", ""uk"", ""american"", etc.) — tags must describe musical genre/style only.

Tags must be genre/style names, not standalone descriptive/mood adjectives. Never use a bare mood word as its own tag — NOT ""dark"", ""sad"", ""heavy"", ""melancholic"", ""aggressive"", ""atmospheric"", ""epic"", ""brutal"", ""raw"", or ""melodic"" by itself. This does NOT apply when the same word is part of an established genre name — that's a genre/style tag in its own right and is allowed, e.g. ""atmospheric black metal"", ""depressive black metal"", ""melodic death metal"", ""progressive rock"", ""post-rock"", and ""dark ambient"" (a real genre) are all fine. Only the bare adjective standing alone as its own tag is forbidden, not the same word inside a recognised genre name.

If you cannot reliably identify the band, respond with:
TAGS:
and nothing else.";

    private Task<string> SendQueryWithWebSearchAsync(string prompt) =>
        SendQueryWithWebSearchAsync(prompt, ArtistSystemPrompt, 300);

    private async Task<string> SendQueryWithWebSearchAsync(string prompt, string systemPrompt, int maxTokens)
    {
        var requestBody = new
        {
            model = "claude-haiku-4-5",
            max_tokens = maxTokens,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            tools = new object[]
            {
                new
                {
                    type = "web_search_20250305",
                    name = "web_search",
                    max_uses = 3
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient!.PostAsync("v1/messages", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return $"API error: {response.StatusCode}";
        }

        // Parse the response to extract the text content
        return ExtractTextFromResponse(responseBody);
    }

    private string ExtractTextFromResponse(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("content", out var contentArray))
            {
                var textParts = new StringBuilder();

                foreach (var block in contentArray.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var typeElement) &&
                        typeElement.GetString() == "text" &&
                        block.TryGetProperty("text", out var textElement))
                    {
                        textParts.Append(textElement.GetString());
                    }
                }

                var result = textParts.ToString().Trim();
                if (!string.IsNullOrEmpty(result))
                {
                    return result;
                }
            }

            return "Could not parse response.";
        }
        catch
        {
            return "Could not parse response.";
        }
    }

    public bool IsConfigured => _httpClient != null;
}
