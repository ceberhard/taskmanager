using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace TaskOrganizer.Web.Integrations;

public class CredentialOptions
{
    public const string SectionName = "Credentials";

    /// <summary>Folder outside the repository for credentials. Empty means %LOCALAPPDATA%\TaskOrganizer.</summary>
    public string? Folder { get; set; }
}

/// <summary>
/// Keeps the API tokens the integrations need. They are encrypted with ASP.NET Core Data
/// Protection (DPAPI on Windows, so only this Windows user can read them) and written to a file
/// outside the repository and the database. A token can be saved, checked and removed; it is
/// only ever read back to call the service it belongs to, never to show it.
/// </summary>
public class CredentialStore
{
    public const string JiraApiToken = "JiraApiToken";
    public const string AzureDevOpsToken = "AzureDevOpsToken";

    private const string CredentialFileName = "credentials.json";
    private const string LocalApiKeyFileName = "local-api-key.txt";
    private const int LocalApiKeyBytes = 32;

    private readonly IDataProtector _protector;
    private readonly object _fileLock = new();

    public CredentialStore(IDataProtectionProvider protection, IOptions<CredentialOptions> options)
    {
        _protector = protection.CreateProtector("TaskOrganizer.Credentials.v1");
        string? configured = options.Value.Folder;
        Folder = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskOrganizer")
            : Path.GetFullPath(configured);
    }

    public string Folder { get; }

    public string LocalApiKeyPath => Path.Combine(Folder, LocalApiKeyFileName);

    private string CredentialFilePath => Path.Combine(Folder, CredentialFileName);

    public void Save(string name, string value)
    {
        lock (_fileLock)
        {
            Dictionary<string, string> stored = ReadAll();
            stored[name] = _protector.Protect(value.Trim());
            WriteAll(stored);
        }
    }

    public void Remove(string name)
    {
        lock (_fileLock)
        {
            Dictionary<string, string> stored = ReadAll();
            if (stored.Remove(name))
            {
                WriteAll(stored);
            }
        }
    }

    public bool Has(string name) => Read(name) is not null;

    /// <summary>The token, or null when none is saved or it can no longer be decrypted (for example after a Windows profile reset).</summary>
    public string? Read(string name)
    {
        lock (_fileLock)
        {
            if (!ReadAll().TryGetValue(name, out string? protectedValue))
            {
                return null;
            }

            try
            {
                return _protector.Unprotect(protectedValue);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The key a local tool (the Slack capture skill) sends to add tasks. Created on first use and
    /// kept in its own plain file in the credential folder, where only this Windows user's tools read it.
    /// </summary>
    public string GetOrCreateLocalApiKey()
    {
        lock (_fileLock)
        {
            if (File.Exists(LocalApiKeyPath))
            {
                string existing = File.ReadAllText(LocalApiKeyPath).Trim();
                if (existing.Length > 0)
                {
                    return existing;
                }
            }

            Directory.CreateDirectory(Folder);
            string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(LocalApiKeyBytes)).ToLowerInvariant();
            File.WriteAllText(LocalApiKeyPath, key);
            return key;
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(CredentialFilePath))
        {
            return new Dictionary<string, string>();
        }

        string json = File.ReadAllText(CredentialFilePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private void WriteAll(Dictionary<string, string> stored)
    {
        Directory.CreateDirectory(Folder);
        string temporary = CredentialFilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stored));
        File.Move(temporary, CredentialFilePath, overwrite: true);
    }
}
