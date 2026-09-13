namespace Scrap.Crypto;

/// <summary>指定缺少平台主密钥时的合法动作。Specifies the permitted action when the platform master key is missing.</summary>
public enum MasterKeyAccess
{
    /// <summary>只打开既有 store；缺 key 必须报错。Open an existing store only; a missing key is an error.</summary>
    OpenExisting = 0,

    /// <summary>只用于已确认全新 store；缺 key 时创建。Use only for a confirmed fresh store; create a missing key.</summary>
    InitializeNew = 1,
}
