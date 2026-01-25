using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;

namespace MusicOrganiser.Services;

public class ArtistInfoService
{
    private AnthropicClient? _client;
    private string? _lastArtistKey;
    private string? _cachedSummary;

    public ArtistInfoService()
    {
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

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            _client = new AnthropicClient(apiKey);
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

    public async Task<string> GetArtistSummaryAsync(string? artistName, string? songTitle, string? album, string fileName)
    {
        if (_client == null)
        {
            return "API key not configured. Add ANTHROPIC_API_KEY to .env file.";
        }

        // Create a cache key from all available info
        var cacheKey = $"{artistName}|{songTitle}|{album}|{fileName}";
        if (_lastArtistKey == cacheKey && _cachedSummary != null)
        {
            return _cachedSummary;
        }

        try
        {
            // Step 1: Try with artist name if available
            string? summary = null;
            bool isUncertain = true;

            if (!string.IsNullOrWhiteSpace(artistName))
            {
                summary = await QueryArtistAsync(artistName, null, null);
                isUncertain = IsUncertainResult(summary);
            }

            // Step 2: If uncertain or no artist, try with more context
            if (isUncertain && HasAdditionalContext(songTitle, album, fileName))
            {
                var contextualQuery = await QueryWithFullContextAsync(artistName, songTitle, album, fileName);
                if (!IsUncertainResult(contextualQuery))
                {
                    summary = contextualQuery;
                }
            }

            // Step 3: If still uncertain, try one more approach - search by song
            if (IsUncertainResult(summary) && !string.IsNullOrWhiteSpace(songTitle))
            {
                var songBasedQuery = await QueryBySongAsync(songTitle, fileName);
                if (!IsUncertainResult(songBasedQuery))
                {
                    summary = songBasedQuery;
                }
            }

            summary ??= "Could not find information about this artist.";

            _lastArtistKey = cacheKey;
            _cachedSummary = summary;

            return summary;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private bool HasAdditionalContext(string? songTitle, string? album, string fileName)
    {
        return !string.IsNullOrWhiteSpace(songTitle) ||
               !string.IsNullOrWhiteSpace(album) ||
               !string.IsNullOrWhiteSpace(fileName);
    }

    private bool IsUncertainResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return true;

        var lowerResult = result.ToLowerInvariant();
        return lowerResult.Contains("unknown artist") ||
               lowerResult.Contains("i don't have information") ||
               lowerResult.Contains("i don't know") ||
               lowerResult.Contains("i'm not familiar") ||
               lowerResult.Contains("i am not familiar") ||
               lowerResult.Contains("couldn't find") ||
               lowerResult.Contains("could not find") ||
               lowerResult.Contains("no information") ||
               lowerResult.Contains("not sure who") ||
               lowerResult.Contains("unable to identify");
    }

    private async Task<string> QueryArtistAsync(string artistName, string? songTitle, string? album)
    {
        var prompt = $@"Search your knowledge for the music artist ""{artistName}"".
Provide a brief summary (2-3 sentences) about this artist including their main genre and a notable fact.
If you're not certain about this artist, say ""Unknown artist"".";

        return await SendQueryAsync(prompt);
    }

    private async Task<string> QueryWithFullContextAsync(string? artistName, string? songTitle, string? album, string fileName)
    {
        var contextParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(artistName))
            contextParts.Add($"Artist: {artistName}");
        if (!string.IsNullOrWhiteSpace(songTitle))
            contextParts.Add($"Song title: {songTitle}");
        if (!string.IsNullOrWhiteSpace(album))
            contextParts.Add($"Album: {album}");

        var cleanFileName = Path.GetFileNameWithoutExtension(fileName);
        contextParts.Add($"File name: {cleanFileName}");

        var context = string.Join("\n", contextParts);

        var prompt = $@"I'm trying to identify a music artist. Here's all the information I have:

{context}

Based on this information, search your knowledge to identify the artist and provide a brief summary (2-3 sentences) about them, including their main genre and a notable fact.

If the artist name seems incomplete or ambiguous, use the song title and other context to determine who this might be.
If you still cannot identify the artist with reasonable confidence, say ""Unknown artist"".";

        return await SendQueryAsync(prompt);
    }

    private async Task<string> QueryBySongAsync(string songTitle, string fileName)
    {
        var cleanFileName = Path.GetFileNameWithoutExtension(fileName);

        var prompt = $@"I'm trying to identify a music artist from a song.
Song title: {songTitle}
File name: {cleanFileName}

Search your knowledge to identify who performs this song. If you can identify the artist, provide a brief summary (2-3 sentences) about them, including their main genre and a notable fact.

If you cannot identify the artist with reasonable confidence, say ""Unknown artist"".";

        return await SendQueryAsync(prompt);
    }

    private async Task<string> SendQueryAsync(string prompt)
    {
        var messages = new List<Message>
        {
            new Message(RoleType.User, prompt)
        };

        var parameters = new MessageParameters
        {
            Model = "claude-haiku-4-5",
            MaxTokens = 200,
            Messages = messages,
            System = new List<SystemMessage>
            {
                new SystemMessage(@"You are a music expert assistant. Your task is to identify music artists and provide brief, accurate summaries about them.

When searching for artist information:
1. Use your comprehensive knowledge of music artists across all genres and eras
2. Consider different spellings, stage names, and band name variations
3. Use song titles and album names as clues when the artist name is ambiguous
4. Be confident when you have reliable information, but say ""Unknown artist"" if genuinely uncertain

Keep responses concise (2-3 sentences) and factual. Include the artist's primary genre and one notable achievement or fact.")
            }
        };

        var response = await _client!.Messages.GetClaudeMessageAsync(parameters);
        return response.Message?.ToString() ?? "Unable to get artist info.";
    }

    public bool IsConfigured => _client != null;
}
