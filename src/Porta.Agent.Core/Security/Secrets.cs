using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Porta.Agent.Core.Security;

[SupportedOSPlatform("windows")]
public static class Secrets
{
    public sealed record ApiTokenReadResult(
        bool Success,
        byte[]? TokenBytes,
        string? ErrorCode);

    private const string RootDirectoryName = "PORTA";
    private const string SecretsDirectoryName = "secrets";
    private const string InstallSecretFileName = "install_secret.bin";
    private const string ApiTokenFileName = "api_token.bin";

    private const string LegacyRootDirectoryName = "PortaAgent";
    private const string LegacySecretsFileName = "secrets.secure.json";
    private const string LegacyLocalApiSettingsFileName = "local-api.secure.json";

    private const DataProtectionScope ProtectionScope = DataProtectionScope.LocalMachine;
    private const string InstallSecretEntropyContext = "PORTA/install_secret/v1";
    private const string ApiTokenEntropyContext = "PORTA/api_token/v1";
    private static readonly byte[] InstallSecretEntropy = Encoding.UTF8.GetBytes(InstallSecretEntropyContext);
    private static readonly byte[] ApiTokenEntropy = Encoding.UTF8.GetBytes(ApiTokenEntropyContext);
    private static readonly object Gate = new();
    private static byte[]? CachedInstallSecret;
    private static byte[]? CachedApiToken;

    public static byte[] GetInstallSecret()
    {
        lock (Gate)
        {
            if (CachedInstallSecret is { Length: 32 })
            {
                return CachedInstallSecret.ToArray();
            }

            byte[] secret = ReadOrCreateSecretBytes(
                secretName: "install_secret",
                GetInstallSecretPath(),
                InstallSecretEntropy,
                readErrorCode: "INSTALL_SECRET_READ_FAILED",
                invalidLengthCode: "INSTALL_SECRET_INVALID_LENGTH",
                writeErrorCode: "INSTALL_SECRET_WRITE_FAILED",
                TryLoadLegacyInstallSecretBytes);

            CachedInstallSecret = secret.ToArray();
            return secret;
        }
    }

    public static ApiTokenReadResult GetApiToken()
    {
        lock (Gate)
        {
            if (CachedApiToken is { Length: 32 })
            {
                return new ApiTokenReadResult(
                    Success: true,
                    TokenBytes: CachedApiToken.ToArray(),
                    ErrorCode: null);
            }

            try
            {
                byte[] token = ReadOrCreateSecretBytes(
                    secretName: "api_token",
                    GetApiTokenPath(),
                    ApiTokenEntropy,
                    readErrorCode: "TOKEN_READ_FAILED",
                    invalidLengthCode: "TOKEN_INVALID_LENGTH",
                    writeErrorCode: "TOKEN_WRITE_FAILED",
                    TryLoadLegacyApiTokenBytes);

                CachedApiToken = token.ToArray();
                return new ApiTokenReadResult(
                    Success: true,
                    TokenBytes: token,
                    ErrorCode: null);
            }
            catch (InvalidOperationException)
            {
                return new ApiTokenReadResult(
                    Success: false,
                    TokenBytes: null,
                    ErrorCode: "TOKEN_READ_FAILED");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"secrets_error code=TOKEN_READ_FAILED secret=api_token path={GetApiTokenPath()} error={ex.GetType().Name}:{ex.Message}");
                return new ApiTokenReadResult(
                    Success: false,
                    TokenBytes: null,
                    ErrorCode: "TOKEN_READ_FAILED");
            }
        }
    }

    public static string GetSecretsDirectoryPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, RootDirectoryName, SecretsDirectoryName);
    }

    public static string GetInstallSecretPath()
    {
        return Path.Combine(GetSecretsDirectoryPath(), InstallSecretFileName);
    }

    public static string GetApiTokenPath()
    {
        return Path.Combine(GetSecretsDirectoryPath(), ApiTokenFileName);
    }

    private static byte[] ReadOrCreateSecretBytes(
        string secretName,
        string secretPath,
        byte[] entropy,
        string readErrorCode,
        string invalidLengthCode,
        string writeErrorCode,
        Func<byte[]?> legacyLoader)
    {
        EnsureSecretsDirectory();

        if (File.Exists(secretPath))
        {
            return ReadExistingSecretBytes(secretName, secretPath, entropy, readErrorCode, invalidLengthCode);
        }

        byte[] secretBytes = legacyLoader() ?? RandomNumberGenerator.GetBytes(32);
        if (secretBytes.Length != 32)
        {
            throw FailClosed(
                invalidLengthCode,
                secretName,
                secretPath,
                detail: "generated_or_legacy_secret_length_not_32");
        }

        byte[] protectedBytes = Protect(secretBytes, entropy);

        bool wroteNew = WriteProtectedBytesAtomic(
            secretName,
            secretPath,
            protectedBytes,
            writeErrorCode);

        if (wroteNew)
        {
            TryHardenFileAcl(secretPath);
            return secretBytes.ToArray();
        }

        return ReadExistingSecretBytes(secretName, secretPath, entropy, readErrorCode, invalidLengthCode);
    }

    private static void EnsureSecretsDirectory()
    {
        string directory = GetSecretsDirectoryPath();
        Directory.CreateDirectory(directory);
        TryHardenDirectoryAcl(directory);
    }

    private static byte[] ReadExistingSecretBytes(
        string secretName,
        string path,
        byte[] entropy,
        string readErrorCode,
        string invalidLengthCode)
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw FailClosed(readErrorCode, secretName, path, ex);
        }

        byte[] secretBytes = UnprotectRequired(protectedBytes, entropy, readErrorCode, secretName, path);
        if (secretBytes.Length != 32)
        {
            throw FailClosed(
                invalidLengthCode,
                secretName,
                path,
                detail: "unprotected_secret_length_not_32");
        }

        TryHardenFileAcl(path);
        return secretBytes;
    }

    private static bool WriteProtectedBytesAtomic(string secretName, string path, byte[] protectedBytes, string writeErrorCode)
    {
        string directory = Path.GetDirectoryName(path) ?? GetSecretsDirectoryPath();
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tempPath, protectedBytes);

        try
        {
            File.Move(tempPath, path, overwrite: false);
            return true;
        }
        catch (IOException)
        {
            if (!File.Exists(path))
            {
                throw FailClosed(writeErrorCode, secretName, path);
            }

            return false;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static byte[]? TryLoadLegacyInstallSecretBytes()
    {
        try
        {
            LegacySecretsDocument? legacy = TryLoadLegacySecretsDocument();
            if (legacy == null || string.IsNullOrWhiteSpace(legacy.InstallSecretProtected))
            {
                return null;
            }

            byte[] protectedBytes = Convert.FromBase64String(legacy.InstallSecretProtected);
            byte[] unprotected = ProtectedData.Unprotect(protectedBytes, InstallSecretEntropy, ProtectionScope);
            return unprotected.Length == 32 ? unprotected : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryLoadLegacyApiTokenBytes()
    {
        try
        {
            LegacySecretsDocument? legacy = TryLoadLegacySecretsDocument();
            if (legacy != null && !string.IsNullOrWhiteSpace(legacy.ApiTokenProtected))
            {
                byte[] protectedBytes = Convert.FromBase64String(legacy.ApiTokenProtected);
                byte[] unprotected = ProtectedData.Unprotect(protectedBytes, ApiTokenEntropy, ProtectionScope);
                if (unprotected.Length == 32)
                {
                    return unprotected;
                }
            }
        }
        catch
        {
        }

        try
        {
            string path = Path.Combine(GetLegacySecurityDirectoryPath(), LegacyLocalApiSettingsFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            string raw = File.ReadAllText(path);
            LegacyLocalApiSettingsDocument? doc = JsonSerializer.Deserialize<LegacyLocalApiSettingsDocument>(raw);
            if (doc == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(doc.AuthTokenProtected))
            {
                byte[] protectedBytes = Convert.FromBase64String(doc.AuthTokenProtected);
                byte[] unprotected = ProtectedData.Unprotect(protectedBytes, ApiTokenEntropy, ProtectionScope);
                if (unprotected.Length == 32)
                {
                    return unprotected;
                }
            }

            if (!string.IsNullOrWhiteSpace(doc.AuthToken))
            {
                try
                {
                    byte[] hex = Convert.FromHexString(doc.AuthToken.Trim());
                    if (hex.Length == 32)
                    {
                        return hex;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static LegacySecretsDocument? TryLoadLegacySecretsDocument()
    {
        string path = Path.Combine(GetLegacySecurityDirectoryPath(), LegacySecretsFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        string raw = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LegacySecretsDocument>(raw);
    }

    private static string GetLegacySecurityDirectoryPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, LegacyRootDirectoryName, "security");
    }

    private static byte[] Protect(byte[] plain, byte[] entropy)
    {
        return ProtectedData.Protect(plain, entropy, ProtectionScope);
    }

    private static byte[] UnprotectRequired(
        byte[] protectedBytes,
        byte[] entropy,
        string errorCode,
        string secretName,
        string path)
    {
        try
        {
            return ProtectedData.Unprotect(protectedBytes, entropy, ProtectionScope);
        }
        catch (Exception ex)
        {
            throw FailClosed(errorCode, secretName, path, ex);
        }
    }

    private static InvalidOperationException FailClosed(
        string errorCode,
        string secretName,
        string path,
        Exception? ex = null,
        string? detail = null)
    {
        string message = $"secrets_error code={errorCode} secret={secretName} path={path}";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            message = $"{message} detail={detail}";
        }

        if (ex != null)
        {
            message = $"{message} error={ex.GetType().Name}:{ex.Message}";
        }

        Console.Error.WriteLine(message);
        return ex == null ? new InvalidOperationException(errorCode) : new InvalidOperationException(errorCode, ex);
    }

    private static void TryHardenDirectoryAcl(string directoryPath)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            AddDirectoryFullControlRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            AddDirectoryFullControlRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            AddDirectoryReadRule(security, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));

            SecurityIdentifier? currentSid = WindowsIdentity.GetCurrent().User;
            if (currentSid != null)
            {
                AddDirectoryFullControlRule(security, currentSid);
            }

            var dirInfo = new DirectoryInfo(directoryPath);
            dirInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"secrets_acl_weaker_permissions target=directory path={directoryPath} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static void TryHardenFileAcl(string filePath)
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            AddFileFullControlRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            AddFileFullControlRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            AddFileReadRule(security, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));

            SecurityIdentifier? currentSid = WindowsIdentity.GetCurrent().User;
            if (currentSid != null)
            {
                AddFileFullControlRule(security, currentSid);
            }

            var fileInfo = new FileInfo(filePath);
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"secrets_acl_weaker_permissions target=file path={filePath} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static void AddDirectoryFullControlRule(DirectorySecurity security, SecurityIdentifier sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void AddDirectoryReadRule(DirectorySecurity security, SecurityIdentifier sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void AddFileFullControlRule(FileSecurity security, SecurityIdentifier sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void AddFileReadRule(FileSecurity security, SecurityIdentifier sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.Read,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class LegacySecretsDocument
    {
        public string? InstallSecretProtected { get; init; }
        public string? ApiTokenProtected { get; init; }
    }

    private sealed class LegacyLocalApiSettingsDocument
    {
        public string? AuthTokenProtected { get; init; }
        public string? AuthToken { get; init; }
    }
}
