using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Sessione GitHub dell'app in <c>%LOCALAPPDATA%\AIUsageMonitor\updates-auth.json</c>:
/// <c>{"version":1,"cipher":"&lt;CipherName&gt;","data":"&lt;base64&gt;"}</c>, dove data e' il JSON
/// <c>{"token":…,"account":…}</c> cifrato con <see cref="ISecretProtector"/>. Mai token o account nei log.
/// </summary>
public sealed class UpdateCredentialStore : IUpdateCredentialStore
{
    /// <summary>Dimensione massima del file della sessione: un file piu' grande non viene nemmeno interpretato.</summary>
    public const int MaxFileBytes = 64 * 1024;

    private const int FormatVersion = 1;

    // Il file e' piatto: una profondita' minima basta e le chiavi duplicate sono un file manomesso, non una scelta.
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public UpdateCredentialStore(string path, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = path;
        _protector = protector;
    }

    public UpdateCredential Read()
    {
        byte[]? file;
        try
        {
            file = ReadBounded(_path, MaxFileBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return UpdateCredential.Missing(CredentialFailure.NotConnected);
        }
        catch (Exception)
        {
            return UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable);
        }

        try
        {
            return (file is null ? null : Decode(file)) ?? UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable);
        }
        catch (Exception)
        {
            // JSON non valido, base64 non valido, DPAPI che rifiuta il blob (altro utente/PC): la sessione non e' usabile.
            return UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable);
        }
    }

    public void Save(string token, string account)
    {
        if (!UpdateCredentialRules.IsValidToken(token) || !UpdateCredentialRules.IsValidAccount(account)) throw SaveFailed();
        if (!ProtectorAvailable()) throw new UpdateException("UPDATES_AUTH_SAVE", UpdateMessages.AuthStorageUnavailable);

        byte[] ciphertext;
        var plaintext = SerializeSession(token, account);
        try
        {
            ciphertext = _protector.Protect(plaintext);
        }
        catch (Exception)
        {
            throw SaveFailed();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", FormatVersion);
                    writer.WriteString("cipher", _protector.CipherName);
                    writer.WriteString("data", Convert.ToBase64String(ciphertext));
                    writer.WriteEndObject();
                }
                stream.Flush(flushToDisk: true);
            }
            // Il file e' sempre o la sessione precedente o quella nuova completa: mai un JSON troncato.
            ExecutableSwap.MoveWithRetry(temporary, _path, overwrite: true);
        }
        catch (Exception)
        {
            TryDelete(temporary);
            throw SaveFailed();
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(_path);
        }
        catch (DirectoryNotFoundException)
        {
            // Nessuna cartella dati: non c'e' nemmeno una sessione da eliminare.
        }
        catch (Exception)
        {
            throw new UpdateException("UPDATES_AUTH_DISCONNECT", UpdateMessages.DisconnectFailed);
        }
    }

    private UpdateCredential? Decode(byte[] file)
    {
        string data;
        using (var document = JsonDocument.Parse(file, DocumentOptions))
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetDouble(out var number) || number != FormatVersion)
                return null;
            if (!root.TryGetProperty("cipher", out var cipher) || cipher.ValueKind != JsonValueKind.String
                || !string.Equals(cipher.GetString(), _protector.CipherName, StringComparison.Ordinal))
                return null;
            if (!root.TryGetProperty("data", out var payload) || payload.ValueKind != JsonValueKind.String) return null;
            data = payload.GetString()!;
        }
        if (!UpdateCredentialRules.IsBase64(data) || !ProtectorAvailable()) return null;

        var plaintext = _protector.Unprotect(Convert.FromBase64String(data));
        try
        {
            using var session = JsonDocument.Parse(plaintext, DocumentOptions);
            var root = session.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var token = StringProperty(root, "token");
            var account = StringProperty(root, "account");
            if (!UpdateCredentialRules.IsValidToken(token) || !UpdateCredentialRules.IsValidAccount(account)) return null;
            return UpdateCredential.Connected(token!, account!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private bool ProtectorAvailable()
    {
        try
        {
            return _protector.IsAvailable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Contenuto del file se non supera <paramref name="limit"/> byte, altrimenti null. Le eccezioni di IO passano.</summary>
    private static byte[]? ReadBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length > limit) return null;
        var buffer = new byte[limit + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        return total > limit ? null : buffer.AsSpan(0, total).ToArray();
    }

    /// <summary>
    /// <c>{"token":"…","account":"…"}</c> in UTF-8, costruito a mano in buffer che vengono azzerati: il serializzatore
    /// userebbe buffer condivisi che tornerebbero al pool con il token dentro. Token e account sono gia' validati
    /// (niente caratteri di controllo), quindi basta l'escape di virgolette e backslash.
    /// </summary>
    private static byte[] SerializeSession(string token, string account)
    {
        const string prefix = "{\"token\":\"";
        const string middle = "\",\"account\":\"";
        const string suffix = "\"}";
        var chars = new char[prefix.Length + (token.Length * 2) + middle.Length + (account.Length * 2) + suffix.Length];
        try
        {
            var length = 0;
            Append(prefix, escape: false);
            Append(token, escape: true);
            Append(middle, escape: false);
            Append(account, escape: true);
            Append(suffix, escape: false);
            return Encoding.UTF8.GetBytes(chars, 0, length);

            void Append(string value, bool escape)
            {
                foreach (var c in value)
                {
                    if (escape && c is '"' or '\\') chars[length++] = '\\';
                    chars[length++] = c;
                }
            }
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    private static UpdateException SaveFailed() => new("UPDATES_AUTH_SAVE", UpdateMessages.AuthSaveFailed);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Resta un .tmp orfano: non contiene la sessione in chiaro ed e' innocuo.
        }
    }
}

/// <summary>Regole condivise da store e login per token e nome account restituiti da GitHub.</summary>
internal static partial class UpdateCredentialRules
{
    public const int MaxTokenLength = 8192;

    /// <summary>1..8192 caratteri, nessuno spazio (anche Unicode) e nessun carattere di controllo.</summary>
    public static bool IsValidToken(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTokenLength) return false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '﻿') return false;
        }
        return true;
    }

    public static bool IsValidAccount(string? value) => value is not null && AccountPattern().IsMatch(value);

    public static bool IsBase64(string value) => Base64Pattern().IsMatch(value);

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9+/]+={0,2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Pattern();
}

/// <summary>DPAPI con ambito utente corrente (CryptProtectData via P/Invoke, nessun pacchetto NuGet). Solo Windows.</summary>
/// <remarks>
/// Il blob e' legato all'utente Windows corrente e a un'entropia fissa dell'app: un altro account, un altro PC o un altro
/// programma dello stesso utente che non conosce l'entropia non lo decifrano. <see cref="Protect"/> e
/// <see cref="Unprotect"/> lanciano <see cref="CryptographicException"/> se DPAPI rifiuta i dati e
/// <see cref="PlatformNotSupportedException"/> fuori da Windows; lo store le trasforma nei propri esiti.
/// </remarks>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string Name = "dpapi-current-user";

    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = "AIUsageMonitor/updates-auth/v1"u8.ToArray();

    public string CipherName => Name;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Transform(plaintext, protect: true);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return Transform(ciphertext, protect: false);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI richiede Windows.");
        var data = default(DataBlob);
        var entropy = default(DataBlob);
        var output = default(DataBlob);
        try
        {
            data = Allocate(input);
            entropy = Allocate(Entropy);
            var ok = protect
                ? CryptProtectData(ref data, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref data, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.cbData < 0 || (output.cbData > 0 && output.pbData == IntPtr.Zero)) throw new CryptographicException("DPAPI ha restituito un blob non valido.");
            var result = new byte[output.cbData];
            if (output.cbData > 0) Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Release(data);
            Release(entropy);
            if (output.pbData != IntPtr.Zero)
            {
                // Dopo CryptUnprotectData il buffer di DPAPI contiene la sessione in chiaro: azzeralo prima di liberarlo.
                Zero(output);
                LocalFree(output.pbData);
            }
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        if (bytes.Length > 0) Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { cbData = bytes.Length, pbData = pointer };
    }

    private static void Release(DataBlob blob)
    {
        if (blob.pbData == IntPtr.Zero) return;
        Zero(blob);
        Marshal.FreeHGlobal(blob.pbData);
    }

    private static void Zero(DataBlob blob)
    {
        if (blob.cbData > 0) Marshal.Copy(new byte[blob.cbData], 0, blob.pbData, blob.cbData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, IntPtr szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
