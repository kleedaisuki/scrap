namespace Scrap.Crypto;

/// <summary>
/// 定义必须由 AEAD 鉴权绑定的不可变 record 身份与明文元数据。
/// Defines immutable record identity and plaintext metadata that AEAD authentication must bind.
/// </summary>
/// <param name="SchemaVersion">数据库 schema 版本。Database schema version.</param>
/// <param name="RecordId">不可变 record ID。Immutable record ID.</param>
/// <param name="Scope">大小写敏感的 scope 名称。Case-sensitive scope name.</param>
/// <param name="Key">大小写敏感的 record key。Case-sensitive record key.</param>
public readonly record struct RecordEncryptionContext(int SchemaVersion, string RecordId, string Scope, string Key);
