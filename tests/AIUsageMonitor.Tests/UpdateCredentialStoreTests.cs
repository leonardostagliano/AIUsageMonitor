using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Updates;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class UpdateCredentialStoreTests
{
    private const string Token = "gho_fake_token_123";
    private const string Account = "octocat";

    [Fact]
    public void A_missing_file_or_folder_means_not_connected()
    {
        using var dir = new TempDir();
        var protector = new XorProtector();

        Assert.Equal(UpdateCredential.Missing(CredentialFailure.NotConnected),
            new UpdateCredentialStore(Path.Combine(dir.Path, "updates-auth.json"), protector).Read());
        Assert.Equal(UpdateCredential.Missing(CredentialFailure.NotConnected),
            new UpdateCredentialStore(Path.Combine(dir.Path, "missing", "nested", "updates-auth.json"), protector).Read());
    }

    [Fact]
    public void Save_and_read_round_trip_through_the_protector()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "AIUsageMonitor", "updates-auth.json");
        var store = new UpdateCredentialStore(path, new XorProtector());

        store.Save(Token, Account);

        Assert.Equal(UpdateCredential.Connected(Token, Account), store.Read());
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, saved.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(XorProtector.Cipher, saved.RootElement.GetProperty("cipher").GetString());
        var data = Convert.FromBase64String(saved.RootElement.GetProperty("data").GetString()!);
        // Nel file non c'e' nulla in chiaro: ne' il token ne' l'account.
        var raw = File.ReadAllText(path) + Encoding.UTF8.GetString(data);
        Assert.DoesNotContain(Token, raw);
        Assert.DoesNotContain(Account, raw);
        Assert.Equal($$"""{"token":"{{Token}}","account":"{{Account}}"}""", Encoding.UTF8.GetString(XorProtector.Xor(data[XorProtector.Magic.Length..])));
    }

    [Fact]
    public void Save_replaces_the_previous_session_atomically_without_leaving_temporary_files()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "updates-auth.json");
        var store = new UpdateCredentialStore(path, new XorProtector());

        store.Save("gho_first", "first-account");
        store.Save("gho_second", "second_account");

        Assert.Equal(UpdateCredential.Connected("gho_second", "second_account"), store.Read());
        Assert.Equal(["updates-auth.json"], Directory.GetFiles(dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Tokens_with_quotes_and_backslashes_survive_the_round_trip()
    {
        using var dir = new TempDir();
        var store = new UpdateCredentialStore(Path.Combine(dir.Path, "updates-auth.json"), new XorProtector());
        const string token = "gho_\"quoted\\slash\"_é€";

        store.Save(token, Account);

        Assert.Equal(UpdateCredential.Connected(token, Account), store.Read());
    }

    [Theory]
    [InlineData("", Account)]
    [InlineData("gho token", Account)]
    [InlineData("gho\ttoken", Account)]
    [InlineData("gho token", Account)]
    [InlineData("gho\u0001token", Account)]
    [InlineData("gho\u007Ftoken", Account)]
    [InlineData(Token, "")]
    [InlineData(Token, "octo cat")]
    [InlineData(Token, "octo.cat")]
    [InlineData(Token, "octocat\n")]
    [InlineData(Token, "octo١")] // cifra araba: [0-9], non \d
    [InlineData(Token, "òctocat")]
    public void Save_rejects_an_invalid_token_or_account_without_writing(string token, string account)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "updates-auth.json");

        var error = Assert.Throws<UpdateException>(() => new UpdateCredentialStore(path, new XorProtector()).Save(token, account));

        Assert.Equal("UPDATES_AUTH_SAVE", error.Code);
        Assert.Equal(UpdateMessages.AuthSaveFailed, error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Token_and_account_lengths_are_bounded()
    {
        using var dir = new TempDir();
        var store = new UpdateCredentialStore(Path.Combine(dir.Path, "updates-auth.json"), new XorProtector());
        var longest = new string('t', 8192);
        var longestAccount = new string('a', 100);

        store.Save(longest, longestAccount);
        Assert.Equal(UpdateCredential.Connected(longest, longestAccount), store.Read());

        Assert.Equal("UPDATES_AUTH_SAVE", Assert.Throws<UpdateException>(() => store.Save(longest + "t", Account)).Code);
        Assert.Equal("UPDATES_AUTH_SAVE", Assert.Throws<UpdateException>(() => store.Save(Token, longestAccount + "a")).Code);
    }

    [Fact]
    public void Save_reports_storage_unavailable_when_the_protector_is_unavailable()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "updates-auth.json");

        var error = Assert.Throws<UpdateException>(() =>
            new UpdateCredentialStore(path, new XorProtector { Available = false }).Save(Token, Account));

        Assert.Equal("UPDATES_AUTH_SAVE", error.Code);
        Assert.Equal(UpdateMessages.AuthStorageUnavailable, error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Save_failures_become_update_exceptions_without_secrets()
    {
        using var dir = new TempDir();
        // Il protector che lancia e una cartella che in realta' e' un file.
        var throwing = Assert.Throws<UpdateException>(() =>
            new UpdateCredentialStore(Path.Combine(dir.Path, "a.json"), new XorProtector { ThrowOnProtect = true }).Save(Token, Account));
        var notADirectory = dir.File("occupied", "file");
        var blocked = Assert.Throws<UpdateException>(() =>
            new UpdateCredentialStore(Path.Combine(notADirectory, "updates-auth.json"), new XorProtector()).Save(Token, Account));

        foreach (var error in new[] { throwing, blocked })
        {
            Assert.Equal("UPDATES_AUTH_SAVE", error.Code);
            Assert.Equal(UpdateMessages.AuthSaveFailed, error.Message);
            Assert.DoesNotContain(Token, error.Message);
            Assert.DoesNotContain(Account, error.Message);
        }
        Assert.Equal(["occupied"], Directory.GetFileSystemEntries(dir.Path).Select(Path.GetFileName));
    }

    public static TheoryData<string> CorruptFiles()
    {
        var valid = XorProtector.Encrypt($$"""{"token":"{{Token}}","account":"{{Account}}"}""");
        return new TheoryData<string>
        {
            "{ not json",
            "",
            "[]",
            "null",
            $$"""{"version":2,"cipher":"{{XorProtector.Cipher}}","data":"{{valid}}"}""",
            $$"""{"version":"1","cipher":"{{XorProtector.Cipher}}","data":"{{valid}}"}""",
            $$"""{"cipher":"{{XorProtector.Cipher}}","data":"{{valid}}"}""",
            $$"""{"version":1,"cipher":"electron-safe-storage","data":"{{valid}}"}""",
            $$"""{"version":1,"data":"{{valid}}"}""",
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}"}""",
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":42}""",
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":""}""",
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":"@@@@"}""",
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":"QUJ"}""",
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":"{{valid}} "}""",
            // Blob che il protector rifiuta (non cifrato da lui).
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":"QUJD"}""",
            // Chiavi duplicate: file manomesso.
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":"{{valid}}","data":"{{valid}}"}""",
            // JSON interno non valido o con campi non validi.
            Envelope("not json"),
            Envelope("[]"),
            Envelope($$"""{"token":"{{Token}}"}"""),
            Envelope($$"""{"account":"{{Account}}"}"""),
            Envelope($$"""{"token":"gho token","account":"{{Account}}"}"""),
            Envelope($$"""{"token":"{{Token}}","account":"octo cat"}"""),
            Envelope($$"""{"token":42,"account":"{{Account}}"}"""),
        };

        static string Envelope(string inner) =>
            $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":"{{XorProtector.Encrypt(inner)}}"}""";
    }

    [Theory]
    [MemberData(nameof(CorruptFiles))]
    public void A_corrupt_session_file_is_reported_as_unavailable(string content)
    {
        using var dir = new TempDir();
        var path = dir.File("updates-auth.json", content);

        Assert.Equal(UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable),
            new UpdateCredentialStore(path, new XorProtector()).Read());
    }

    [Fact]
    public void The_valid_envelope_used_by_the_corrupt_cases_is_itself_readable()
    {
        using var dir = new TempDir();
        var data = XorProtector.Encrypt($$"""{"token":"{{Token}}","account":"{{Account}}"}""");
        var path = dir.File("updates-auth.json", $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":"{{data}}"}""");

        Assert.Equal(UpdateCredential.Connected(Token, Account), new UpdateCredentialStore(path, new XorProtector()).Read());
    }

    [Fact]
    public void Read_reports_unavailable_when_the_protector_is_unavailable_or_throws()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "updates-auth.json");
        new UpdateCredentialStore(path, new XorProtector()).Save(Token, Account);

        var unavailable = UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable);
        Assert.Equal(unavailable, new UpdateCredentialStore(path, new XorProtector { Available = false }).Read());
        Assert.Equal(unavailable, new UpdateCredentialStore(path, new XorProtector { ThrowOnUnprotect = true }).Read());
        Assert.Equal(unavailable, new UpdateCredentialStore(path, new XorProtector { AvailabilityThrows = true }).Read());
    }

    [Fact]
    public void A_file_over_64_kilobytes_is_not_read()
    {
        using var dir = new TempDir();
        var data = XorProtector.Encrypt($$"""{"token":"{{Token}}","account":"{{Account}}"}""");
        var envelope = $$"""{"version":1,"cipher":"{{XorProtector.Cipher}}","data":"{{data}}"}""";
        var path = dir.File("updates-auth.json", envelope + new string(' ', UpdateCredentialStore.MaxFileBytes - envelope.Length));
        var store = new UpdateCredentialStore(path, new XorProtector());

        Assert.Equal(UpdateCredential.Connected(Token, Account), store.Read());

        File.AppendAllText(path, " ");
        Assert.Equal(UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable), store.Read());
    }

    [Fact]
    public void A_directory_in_place_of_the_file_is_unavailable_not_disconnected()
    {
        using var dir = new TempDir();
        var path = dir.Sub("updates-auth.json");

        Assert.Equal(UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable),
            new UpdateCredentialStore(path, new XorProtector()).Read());
    }

    [Fact]
    public void Delete_removes_the_session_and_tolerates_a_missing_file_or_folder()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "updates-auth.json");
        var store = new UpdateCredentialStore(path, new XorProtector());
        store.Save(Token, Account);

        store.Delete();

        Assert.False(File.Exists(path));
        Assert.Equal(UpdateCredential.Missing(CredentialFailure.NotConnected), store.Read());
        store.Delete();
        new UpdateCredentialStore(Path.Combine(dir.Path, "missing", "updates-auth.json"), new XorProtector()).Delete();
    }

    [Fact]
    public void Delete_failures_become_update_exceptions()
    {
        using var dir = new TempDir();
        var path = dir.Sub("updates-auth.json");
        dir.File(Path.Combine("updates-auth.json", "inside.txt"), "x");

        var error = Assert.Throws<UpdateException>(() => new UpdateCredentialStore(path, new XorProtector()).Delete());

        Assert.Equal("UPDATES_AUTH_DISCONNECT", error.Code);
        Assert.Equal(UpdateMessages.DisconnectFailed, error.Message);
    }

    [Fact]
    public void Dpapi_is_available_only_on_windows()
    {
        var protector = new DpapiSecretProtector();

        Assert.Equal("dpapi-current-user", protector.CipherName);
        Assert.Equal(OperatingSystem.IsWindows(), protector.IsAvailable);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => protector.Protect([1, 2, 3]));
            Assert.Throws<PlatformNotSupportedException>(() => protector.Unprotect([1, 2, 3]));
        }
    }

    [Fact]
    public void Dpapi_round_trips_and_rejects_tampered_blobs()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new DpapiSecretProtector();
        var plaintext = Encoding.UTF8.GetBytes($$"""{"token":"{{Token}}","account":"{{Account}}"}""");

        var ciphertext = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, ciphertext);
        Assert.DoesNotContain(Token, Encoding.UTF8.GetString(ciphertext));
        Assert.Equal(plaintext, protector.Unprotect(ciphertext));
        ciphertext[^1] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(ciphertext));
        Assert.Throws<CryptographicException>(() => protector.Unprotect([1, 2, 3, 4]));
    }

    [Fact]
    public void Dpapi_store_round_trip_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "updates-auth.json");
        var store = new UpdateCredentialStore(path, new DpapiSecretProtector());

        store.Save(Token, Account);

        Assert.Equal(UpdateCredential.Connected(Token, Account), store.Read());
        Assert.DoesNotContain(Token, File.ReadAllText(path));
    }

    /// <summary>Protector reversibile al posto di DPAPI: al test serve solo un round trip che controlla.</summary>
    private sealed class XorProtector : ISecretProtector
    {
        public const string Cipher = "test-xor";
        public static readonly byte[] Magic = "XOR1"u8.ToArray();

        public bool Available { get; init; } = true;
        public bool AvailabilityThrows { get; init; }
        public bool ThrowOnProtect { get; init; }
        public bool ThrowOnUnprotect { get; init; }

        public string CipherName => Cipher;

        public bool IsAvailable => AvailabilityThrows ? throw new InvalidOperationException("probe failed") : Available;

        public byte[] Protect(byte[] plaintext)
        {
            if (ThrowOnProtect) throw new CryptographicException("protect failed");
            return [.. Magic, .. Xor(plaintext)];
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            if (ThrowOnUnprotect) throw new CryptographicException("unprotect failed");
            if (!ciphertext.AsSpan().StartsWith(Magic)) throw new CryptographicException("not encrypted by this protector");
            return Xor(ciphertext[Magic.Length..]);
        }

        public static byte[] Xor(byte[] bytes) => bytes.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public static string Encrypt(string plaintext) =>
            Convert.ToBase64String([.. Magic, .. Xor(Encoding.UTF8.GetBytes(plaintext))]);
    }
}
