using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Scrap.Platform.Secrets;

/// <summary>
/// 使用 macOS Keychain generic-password item 保存主密钥。
/// Stores the master key in a macOS Keychain generic-password item.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainMasterKeyProvider : IMasterKeyProvider
{
    private const int Success = 0;
    private const int DuplicateItem = -25299;
    private const int ItemNotFound = -25300;
    private readonly string _serviceName;
    private readonly string _accountName;

    /// <summary>创建 Keychain provider。Creates a Keychain provider.</summary>
    /// <param name="serviceName">generic-password service 属性。Generic-password service attribute.</param>
    /// <param name="accountName">profile 的非秘密稳定 account 属性。Non-secret stable account attribute for the profile.</param>
    public MacKeychainMasterKeyProvider(string serviceName, string accountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        _serviceName = serviceName;
        _accountName = accountName;
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var query = KeychainDictionary.CreateQuery(_serviceName, _accountName);
            query.AddConstant("kSecReturnData", KeychainNative.CoreFoundationBooleanTrue);
            query.AddConstants("kSecMatchLimit", "kSecMatchLimitOne");

            int status = KeychainNative.SecItemCopyMatching(query.Handle, out nint result);
            if (status == ItemNotFound)
            {
                return ValueTask.FromResult<byte[]?>(null);
            }

            ThrowForStatus(status, "load");
            if (result == 0)
            {
                throw ProviderError("load", "Keychain returned success without secret data.");
            }

            try
            {
                nint length = KeychainNative.CFDataGetLength(result);
                if (length <= 0 || length > 4096)
                {
                    throw ProviderError("load", "Keychain returned master-key data with an invalid length.");
                }

                var key = new byte[(int)length];
                Marshal.Copy(KeychainNative.CFDataGetBytePtr(result), key, 0, key.Length);
                return ValueTask.FromResult<byte[]?>(key);
            }
            finally
            {
                KeychainNative.CFRelease(result);
            }
        }
        catch (MasterKeyProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw ProviderError("load", "macOS Keychain Services is unavailable.", exception);
        }
    }

    /// <inheritdoc />
    public ValueTask StoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("The master key cannot be empty.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var secretData = CoreFoundationValue.FromData(key.Span);
            using var add = KeychainDictionary.CreateQuery(_serviceName, _accountName);
            add.AddConstant("kSecValueData", secretData.Handle);
            int status = KeychainNative.SecItemAdd(add.Handle, 0);
            if (status == DuplicateItem)
            {
                using var query = KeychainDictionary.CreateQuery(_serviceName, _accountName);
                using var update = new KeychainDictionary();
                update.AddConstant("kSecValueData", secretData.Handle);
                status = KeychainNative.SecItemUpdate(query.Handle, update.Handle);
            }

            ThrowForStatus(status, "store");
            return ValueTask.CompletedTask;
        }
        catch (MasterKeyProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw ProviderError("store", "macOS Keychain Services is unavailable.", exception);
        }
    }

    private static void ThrowForStatus(int status, string operation)
    {
        if (status != Success)
        {
            throw ProviderError(operation, $"macOS Keychain operation failed with OSStatus {status}.");
        }
    }

    private static MasterKeyProviderException ProviderError(string operation, string message, Exception? innerException = null) =>
        new("macOS Keychain", operation, message, innerException);

    private sealed class KeychainDictionary : IDisposable
    {
        public KeychainDictionary()
        {
            Handle = KeychainNative.CFDictionaryCreateMutable(
                0,
                0,
                KeychainNative.GetCoreFoundationExport("kCFTypeDictionaryKeyCallBacks"),
                KeychainNative.GetCoreFoundationExport("kCFTypeDictionaryValueCallBacks"));
            if (Handle == 0)
            {
                throw ProviderError("initialize", "CoreFoundation could not allocate a Keychain query dictionary.");
            }
        }

        public nint Handle { get; private set; }

        public static KeychainDictionary CreateQuery(string serviceName, string accountName)
        {
            var dictionary = new KeychainDictionary();
            try
            {
                dictionary.AddConstants("kSecClass", "kSecClassGenericPassword");
                dictionary.AddString("kSecAttrService", serviceName);
                dictionary.AddString("kSecAttrAccount", accountName);
                return dictionary;
            }
            catch
            {
                dictionary.Dispose();
                throw;
            }
        }

        public void AddConstants(string key, string value) => AddConstant(key, KeychainNative.GetSecurityConstant(value));

        public void AddString(string key, string value)
        {
            using var nativeValue = CoreFoundationValue.FromString(value);
            AddConstant(key, nativeValue.Handle);
        }

        public void AddConstant(string key, nint value) =>
            KeychainNative.CFDictionarySetValue(Handle, KeychainNative.GetSecurityConstant(key), value);

        public void Dispose()
        {
            nint handle = Handle;
            Handle = 0;
            if (handle != 0)
            {
                KeychainNative.CFRelease(handle);
            }
        }
    }

    private sealed class CoreFoundationValue : IDisposable
    {
        private CoreFoundationValue(nint handle)
        {
            Handle = handle != 0 ? handle : throw ProviderError("initialize", "CoreFoundation could not allocate a value.");
        }

        public nint Handle { get; private set; }

        public static CoreFoundationValue FromString(string value) =>
            new(KeychainNative.CFStringCreateWithCString(0, value, KeychainNative.Utf8Encoding));

        public static CoreFoundationValue FromData(ReadOnlySpan<byte> data)
        {
            byte[] copy = data.ToArray();
            try
            {
                return new CoreFoundationValue(KeychainNative.CFDataCreate(0, copy, copy.Length));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(copy);
            }
        }

        public void Dispose()
        {
            nint handle = Handle;
            Handle = 0;
            if (handle != 0)
            {
                KeychainNative.CFRelease(handle);
            }
        }
    }

    private static class KeychainNative
    {
        private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly nint SecurityHandle = NativeLibrary.Load(SecurityLibrary);
        private static readonly nint CoreFoundationHandle = NativeLibrary.Load(CoreFoundationLibrary);

        public const uint Utf8Encoding = 0x08000100;

        public static nint CoreFoundationBooleanTrue => ReadConstant(CoreFoundationHandle, "kCFBooleanTrue");

        public static nint GetSecurityConstant(string name) => ReadConstant(SecurityHandle, name);

        public static nint GetCoreFoundationExport(string name) => NativeLibrary.GetExport(CoreFoundationHandle, name);

        private static nint ReadConstant(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));

        [DllImport(SecurityLibrary)]
        public static extern int SecItemAdd(nint attributes, nint result);

        [DllImport(SecurityLibrary)]
        public static extern int SecItemCopyMatching(nint query, out nint result);

        [DllImport(SecurityLibrary)]
        public static extern int SecItemUpdate(nint query, nint attributesToUpdate);

        [DllImport(CoreFoundationLibrary)]
        public static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);

        [DllImport(CoreFoundationLibrary)]
        public static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);

        [DllImport(CoreFoundationLibrary, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        public static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

        [DllImport(CoreFoundationLibrary)]
        public static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);

        [DllImport(CoreFoundationLibrary)]
        public static extern nint CFDataGetLength(nint data);

        [DllImport(CoreFoundationLibrary)]
        public static extern nint CFDataGetBytePtr(nint data);

        [DllImport(CoreFoundationLibrary)]
        public static extern void CFRelease(nint value);
    }
}
