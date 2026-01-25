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
    private string? _lastArtistName;
    private string? _cachedSummary;

    public ArtistInfoService()
    {
        LoadApiKey();
    }

    private void LoadApiKey()
    {
        // Try to load from .env file in app directory or parent directories
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
        // First priority: tag artist
        if (!string.IsNullOrWhiteSpace(tagArtist))
        {
            return tagArtist.Trim();
        }

        // Second priority: current folder name
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            var folderName = Path.GetFileName(directory);
            if (!string.IsNullOrWhiteSpace(folderName) && !IsDriveLetter(folderName))
            {
                return folderName;
            }

            // Third priority: parent folder name
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

    public async Task<string> GetArtistSummaryAsync(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName))
        {
            return "Unknown artist";
        }

        // Return cached result if same artist
        if (_lastArtistName == artistName && _cachedSummary != null)
        {
            return _cachedSummary;
        }

        if (_client == null)
        {
            return "API key not configured. Add ANTHROPIC_API_KEY to .env file.";
        }

        try
        {
            var prompt = $@"Provide a very brief summary (2-3 sentences max) about the music artist ""{artistName}"".
Include their main genre and a notable fact. If you don't know this artist, just say ""Unknown artist"" - don't make up information.
Keep the response concise and factual.";

            var messages = new List<Message>
            {
                new Message(RoleType.User, prompt)
            };

            var parameters = new MessageParameters
            {
                Model = "claude-haiku-4-5",
                MaxTokens = 150,
                Messages = messages
            };

            var response = await _client.Messages.GetClaudeMessageAsync(parameters);
            var summary = response.Message?.ToString() ?? "Unable to get artist info.";

            _lastArtistName = artistName;
            _cachedSummary = summary;

            return summary;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public bool IsConfigured => _client != null;
}
