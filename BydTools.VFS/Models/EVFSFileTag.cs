namespace BydTools.VFS;

/// <summary>
/// File tag for VFS assets, introduced alongside the patch merge mechanism.
/// Binary enum (2 values) registered via manual XLua wrap, not reflection.
/// Actual member names are runtime-obfuscated (StringLiteral_dEDO / StringLiteral_j_OB_);
/// names below are inferred from AddPatchInfo semantics and require runtime
/// confirmation at BeyondVFSEVFSFileTagWrap::__Register.
/// </summary>
public enum EVFSFileTag : byte
{
    /// <summary>
    /// Base package file (inferred).
    /// </summary>
    Base = 0,

    /// <summary>
    /// Patch / hotfix override file (inferred).
    /// </summary>
    Patch = 1,
}
