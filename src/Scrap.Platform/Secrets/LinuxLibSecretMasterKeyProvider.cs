using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Scrap.Platform.Secrets;

/// <summary>
/// 通过 libsecret 将主密钥保存到 Linux Secret Service 的默认持久 collection。
/// Stores the master key in the default persistent Linux Secret Service collection through libsecret.
/// </summary>
/// <remarks>
/// provider 只存储 Base64 编码的密钥文本，并在 native 调用后清零暂存区；检索属性不是秘密。
/// The provider stores only Base64-encoded key text and clears staging buffers after native calls; lookup attributes are not secret.
/// libsecret 的同步 API 可能阻塞，因此只能由 daemon 后台启动路径调用。
/// libsecret synchronous APIs may block and must only be called from the daemon's background startup path.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxLibSecretMasterKeyProvider : IMasterKeyProvider
{
    private const int MasterKeyBytes = 32;
    private const int EncodedKeyBytes = 44;
    private readonly string _serviceName;
    private readonly string _accountName;

    /// <summary>创建 libsecret provider。Creates a libsecret provider.</summary>
    /// <param name="serviceName">非秘密 application 检索属性。Non-secret application lookup attribute.</param>
    /// <param name="accountName">非秘密 profile 检索属性。Non-secret profile lookup attribute.</param>
    public LinuxLibSecretMasterKeyProvider(string serviceName, string accountName)
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
            using var attributes = CreateAttributes();
            nint secret = LibSecretNative.SecretPasswordLookup(attributes.Handle, out nint error);
            ThrowIfError(error, "load");
            if (secret == 0)
            {
                return ValueTask.FromResult<byte[]?>(null);
            }

            byte[] encoded = ArrayPool<byte>.Shared.Rent(EncodedKeyBytes);
            try
            {
                for (int index = 0; index < EncodedKeyBytes; index++)
                {
                    encoded[index] = Marshal.ReadByte(secret, index);
                }

                if (Marshal.ReadByte(secret, EncodedKeyBytes) != 0)
                {
                    throw ProviderError("load", "Secret Service returned a master key with an invalid encoded length.");
                }

                var key = new byte[MasterKeyBytes];
                OperationStatus status = Base64.DecodeFromUtf8(encoded.AsSpan(0, EncodedKeyBytes), key, out int consumed, out int written);
                if (status != OperationStatus.Done || consumed != EncodedKeyBytes || written != MasterKeyBytes)
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw ProviderError("load", "Secret Service returned invalid master-key data.");
                }

                return ValueTask.FromResult<byte[]?>(key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
                ArrayPool<byte>.Shared.Return(encoded);
                LibSecretNative.SecretPasswordFree(secret);
            }
        }
        catch (MasterKeyProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw ProviderError("load", "Linux Secret Service/libsecret is unavailable.", exception);
        }
    }

    /// <inheritdoc />
    public ValueTask StoreAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        if (key.Length != MasterKeyBytes)
        {
            throw new ArgumentException($"The master key must contain exactly {MasterKeyBytes} bytes.", nameof(key));
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] encoded = ArrayPool<byte>.Shared.Rent(EncodedKeyBytes);
        nint nativePassword = 0;
        try
        {
            OperationStatus status = Base64.EncodeToUtf8(key.Span, encoded, out int consumed, out int written);
            if (status != OperationStatus.Done || consumed != MasterKeyBytes || written != EncodedKeyBytes)
            {
                throw ProviderError("store", "The master key could not be encoded for Secret Service.");
            }

            nativePassword = Marshal.AllocHGlobal(EncodedKeyBytes + 1);
            Marshal.Copy(encoded, 0, nativePassword, EncodedKeyBytes);
            Marshal.WriteByte(nativePassword, EncodedKeyBytes, 0);
            using var attributes = CreateAttributes();
            bool stored = LibSecretNative.SecretPasswordStore(
                attributes.Handle,
                $"Scrap master key ({_accountName})",
                nativePassword,
                out nint error);
            ThrowIfError(error, "store");
            if (!stored)
            {
                throw ProviderError("store", "Secret Service did not store the Scrap master key.");
            }

            return ValueTask.CompletedTask;
        }
        catch (MasterKeyProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw ProviderError("store", "Linux Secret Service/libsecret is unavailable.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            ArrayPool<byte>.Shared.Return(encoded);
            if (nativePassword != 0)
            {
                for (int index = 0; index <= EncodedKeyBytes; index++)
                {
                    Marshal.WriteByte(nativePassword, index, 0);
                }

                Marshal.FreeHGlobal(nativePassword);
            }
        }
    }

    private NativeAttributeTable CreateAttributes() => new(
        ("application", _serviceName),
        ("profile", _accountName));

    private static void ThrowIfError(nint error, string operation)
    {
        if (error == 0)
        {
            return;
        }

        try
        {
            GError nativeError = Marshal.PtrToStructure<GError>(error);
            string detail = Marshal.PtrToStringUTF8(nativeError.Message) ?? "unknown libsecret error";
            throw ProviderError(operation, $"Linux Secret Service operation failed: {detail}");
        }
        finally
        {
            GlibNative.ErrorFree(error);
        }
    }

    private static MasterKeyProviderException ProviderError(string operation, string message, Exception? innerException = null) =>
        new("Linux Secret Service/libsecret", operation, message, innerException);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct GError
    {
        public readonly uint Domain;
        public readonly int Code;
        public readonly nint Message;
    }

    private sealed class NativeAttributeTable : IDisposable
    {
        public NativeAttributeTable(params (string Key, string Value)[] attributes)
        {
            GlibExports exports = GlibNative.Exports;
            Handle = GlibNative.HashTableNewFull(exports.StringHash, exports.StringEqual, exports.Free, exports.Free);
            if (Handle == 0)
            {
                throw ProviderError("initialize", "GLib could not allocate a Secret Service attribute table.");
            }

            try
            {
                foreach ((string key, string value) in attributes)
                {
                    nint nativeKey = GlibNative.StringDuplicate(key);
                    nint nativeValue = GlibNative.StringDuplicate(value);
                    if (nativeKey == 0 || nativeValue == 0)
                    {
                        if (nativeKey != 0)
                        {
                            GlibNative.Free(nativeKey);
                        }

                        if (nativeValue != 0)
                        {
                            GlibNative.Free(nativeValue);
                        }

                        throw ProviderError("initialize", "GLib could not allocate a Secret Service attribute.");
                    }

                    GlibNative.HashTableInsert(Handle, nativeKey, nativeValue);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public nint Handle { get; private set; }

        public void Dispose()
        {
            nint handle = Handle;
            Handle = 0;
            if (handle != 0)
            {
                GlibNative.HashTableUnref(handle);
            }
        }
    }

    private readonly record struct GlibExports(nint StringHash, nint StringEqual, nint Free);

    private static class LibSecretNative
    {
        private const string Library = "libsecret-1.so.0";

        [DllImport(Library, EntryPoint = "secret_password_lookupv_sync", CallingConvention = CallingConvention.Cdecl)]
        public static extern nint SecretPasswordLookup(nint schema, nint attributes, nint cancellable, out nint error);

        public static nint SecretPasswordLookup(nint attributes, out nint error) => SecretPasswordLookup(0, attributes, 0, out error);

        [DllImport(Library, EntryPoint = "secret_password_storev_sync", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SecretPasswordStore(
            nint schema,
            nint attributes,
            nint collection,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
            nint password,
            nint cancellable,
            out nint error);

        public static bool SecretPasswordStore(nint attributes, string label, nint password, out nint error) =>
            SecretPasswordStore(0, attributes, 0, label, password, 0, out error);

        [DllImport(Library, EntryPoint = "secret_password_free", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SecretPasswordFree(nint password);
    }

    private static class GlibNative
    {
        private const string Library = "libglib-2.0.so.0";
        private static readonly Lazy<GlibExports> LazyExports = new(LoadExports, LazyThreadSafetyMode.ExecutionAndPublication);

        public static GlibExports Exports => LazyExports.Value;

        [DllImport(Library, EntryPoint = "g_hash_table_new_full", CallingConvention = CallingConvention.Cdecl)]
        public static extern nint HashTableNewFull(nint hashFunction, nint equalFunction, nint keyDestroy, nint valueDestroy);

        [DllImport(Library, EntryPoint = "g_hash_table_insert", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool HashTableInsert(nint table, nint key, nint value);

        [DllImport(Library, EntryPoint = "g_hash_table_unref", CallingConvention = CallingConvention.Cdecl)]
        public static extern void HashTableUnref(nint table);

        [DllImport(Library, EntryPoint = "g_strdup", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        public static extern nint StringDuplicate([MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Library, EntryPoint = "g_free", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Free(nint memory);

        [DllImport(Library, EntryPoint = "g_error_free", CallingConvention = CallingConvention.Cdecl)]
        public static extern void ErrorFree(nint error);

        private static GlibExports LoadExports()
        {
            nint library = NativeLibrary.Load(Library);
            return new GlibExports(
                NativeLibrary.GetExport(library, "g_str_hash"),
                NativeLibrary.GetExport(library, "g_str_equal"),
                NativeLibrary.GetExport(library, "g_free"));
        }
    }
}
