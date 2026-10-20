using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
// Validates recoverability without writing credentials or resolving paths into live storage.
internal sealed class CloudBackupRecoveryStructure(string password)
{
    private readonly HashSet<long> _pilots = new(), _tokens = new(), _missing = new();
    private bool _tokenFile;
    internal int Stored => _tokens.Count;
    internal int Missing => _missing.Count;
    internal static void ValidateDestination(string logical)
    {
        string[] parts = logical.Split('/');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Backup has an invalid recovery path.");
        if (logical is "eve/refresh-tokens.json" or "cloud/preferences.json") return;
        if (parts[0] is not ("config" or "mining" or "local" or "app" or "game-logs")) throw new InvalidDataException("Backup has an unsupported recovery destination.");
        if (parts[0] == "local" && (parts[1].Equals("CloudBackup", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("RestoreBackups", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Backup must not replace local cloud credentials or restore safety copies.");
    }
    internal void Observe(string logical, byte[] bytes)
    {
        if (logical.Equals("local/PilotData/pilots.json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Pilot list is not recoverable.");
            foreach (var pilot in document.RootElement.EnumerateArray())
            {
                if (!pilot.TryGetProperty("CharacterId", out var id) || !id.TryGetInt64(out long value) || value <= 0 || !_pilots.Add(value))
                    throw new InvalidDataException("Pilot list contains an invalid or repeated identity.");
            }
        }
        else if (logical == "eve/refresh-tokens.json")
        {
            if (_tokenFile) throw new InvalidDataException("Repeated EVE login payload.");
            _tokenFile = true;
            byte[] plain = CloudBackupPortableSecurity.Decrypt(bytes, password);
            try
            {
                using var document = JsonDocument.Parse(plain);
                if (!document.RootElement.TryGetProperty("RefreshTokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("MissingCharacterIds", out var missing) || missing.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Encrypted EVE login payload is not recoverable.");
                foreach (var pair in tokens.EnumerateObject())
                    if (!long.TryParse(pair.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || pair.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pair.Value.GetString()) || !_tokens.Add(id))
                        throw new InvalidDataException("Encrypted EVE login payload contains an invalid entry.");
                foreach (var item in missing.EnumerateArray())
                    if (!item.TryGetInt64(out var id) || id <= 0 || !_missing.Add(id) || _tokens.Contains(id)) throw new InvalidDataException("EVE reconnect list is invalid.");
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        else if (logical == "cloud/preferences.json")
        {
            var preferences = JsonSerializer.Deserialize<CloudBackupPreferences>(bytes);
            if (preferences == null || preferences.AutoBackupMinutes < 5 || preferences.AutoBackupMinutes > 43200) throw new InvalidDataException("Backup schedule is not recoverable.");
        }
    }
    internal void Complete()
    {
        if (_pilots.Count > 0 && !_tokenFile) throw new InvalidDataException("Linked pilots are present but the encrypted EVE login payload is missing.");
        if (!_pilots.SetEquals(_tokens.Concat(_missing))) throw new InvalidDataException("EVE logins do not match the archived pilot list.");
    }
}
