using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Scrap.Crypto;

/// <summary>
/// 使用 AES-256-GCM、96-bit 随机 nonce 和 128-bit tag 加密 record value。
/// Encrypts record values with AES-256-GCM, 96-bit random nonces, and 128-bit tags.
/// </summary>
/// <remarks>
/// 每次 <see cref="Encrypt"/> 都生成新 nonce。调用者不得并发调用 <see cref="Dispose"/> 与加解密操作。
/// Every <see cref="Encrypt"/> generates a fresh nonce. Callers must not race <see cref="Dispose"/> with encryption operations.
/// </remarks>
public sealed class AesGcmRecordEncryptor : IRecordEncryptor
{
    /// <summary>AES-256 主密钥长度。AES-256 master-key length.</summary>
    public const int KeySize = 32;

    /// <summary>GCM 推荐 nonce 长度。Recommended GCM nonce length.</summary>
    public const int NonceSize = 12;

    /// <summary>GCM authentication tag 长度。GCM authentication-tag length.</summary>
    public const int TagSize = 16;

    private const int HeaderSize = 8;
    private static readonly byte[] DomainSeparator = "SCRAP-RECORD-AAD\0"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private byte[]? _key;

    /// <summary>从恰好 256-bit 的主密钥创建 encryptor，并复制密钥。Creates an encryptor from, and copies, an exactly 256-bit master key.</summary>
    /// <param name="masterKey">256-bit 主密钥。The 256-bit master key.</param>
    public AesGcmRecordEncryptor(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != KeySize)
        {
            throw new ArgumentException($"The AES-256 master key must contain exactly {KeySize} bytes.", nameof(masterKey));
        }

        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("AES-GCM is not supported by this platform.");
        }

        _key = masterKey.ToArray();
    }

    /// <inheritdoc />
    public EncryptedRecordValue Encrypt(ReadOnlySpan<byte> plaintext, in RecordEncryptionContext context)
    {
        byte[] key = GetKey();
        byte[] envelope = GC.AllocateUninitializedArray<byte>(HeaderSize + TagSize + plaintext.Length);
        WriteHeader(envelope);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] aad = BuildAssociatedData(envelope.AsSpan(0, HeaderSize), context);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(
                nonce,
                plaintext,
                envelope.AsSpan(HeaderSize + TagSize),
                envelope.AsSpan(HeaderSize, TagSize),
                aad);
            return new EncryptedRecordValue(envelope, nonce);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(nonce);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    /// <inheritdoc />
    public byte[] Decrypt(ReadOnlySpan<byte> encryptedValue, ReadOnlySpan<byte> nonce, in RecordEncryptionContext context)
    {
        byte[] key = GetKey();
        ValidateEnvelope(encryptedValue, nonce);
        byte[] plaintext = GC.AllocateUninitializedArray<byte>(encryptedValue.Length - HeaderSize - TagSize);
        byte[] aad = BuildAssociatedData(encryptedValue[..HeaderSize], context);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                nonce,
                encryptedValue[(HeaderSize + TagSize)..],
                encryptedValue.Slice(HeaderSize, TagSize),
                plaintext,
                aad);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new RecordAuthenticationException(exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    /// <summary>清零 encryptor 持有的主密钥副本。Zeros the master-key copy held by the encryptor.</summary>
    public void Dispose()
    {
        byte[]? key = Interlocked.Exchange(ref _key, null);
        if (key is not null)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] GetKey() => Volatile.Read(ref _key) ?? throw new ObjectDisposedException(nameof(AesGcmRecordEncryptor));

    private static void WriteHeader(Span<byte> header)
    {
        "SCRP"u8.CopyTo(header);
        header[4] = 1; // Envelope format version.
        header[5] = 1; // AES-256-GCM algorithm identifier.
        header[6] = TagSize;
        header[7] = 0;
    }

    private static void ValidateEnvelope(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceSize)
        {
            throw new EncryptedRecordFormatException($"The encrypted record nonce must contain exactly {NonceSize} bytes.");
        }

        if (envelope.Length < HeaderSize + TagSize)
        {
            throw new EncryptedRecordFormatException("The encrypted record envelope is truncated.");
        }

        Span<byte> expected = stackalloc byte[HeaderSize];
        WriteHeader(expected);
        if (!envelope[..HeaderSize].SequenceEqual(expected))
        {
            throw new EncryptedRecordFormatException("The encrypted record envelope format or algorithm is unsupported.");
        }
    }

    private static byte[] BuildAssociatedData(ReadOnlySpan<byte> header, in RecordEncryptionContext context)
    {
        if (context.SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), "Schema version must be positive.");
        }

        ValidateField(context.RecordId, "record ID");
        ValidateField(context.Scope, "scope");
        ValidateField(context.Key, "key");

        var writer = new ArrayBufferWriter<byte>();
        Write(writer, DomainSeparator);
        Write(writer, header);
        Span<byte> number = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(number, context.SchemaVersion);
        writer.Advance(sizeof(int));
        WriteString(writer, context.RecordId);
        WriteString(writer, context.Scope);
        WriteString(writer, context.Key);
        return writer.WrittenSpan.ToArray();
    }

    private static void ValidateField(string? value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException($"The encryption context {fieldName} cannot be null or empty.");
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException($"The encryption context {fieldName} is not valid Unicode.", exception);
        }
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        int byteCount = StrictUtf8.GetByteCount(value);
        Span<byte> output = writer.GetSpan(sizeof(int) + byteCount);
        BinaryPrimitives.WriteInt32LittleEndian(output, byteCount);
        StrictUtf8.GetBytes(value, output[sizeof(int)..]);
        writer.Advance(sizeof(int) + byteCount);
    }

    private static void Write(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }
}
