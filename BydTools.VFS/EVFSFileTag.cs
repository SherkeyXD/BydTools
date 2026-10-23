namespace BydTools.VFS;

/// <summary>
/// File tag introduced with the patch-merge mechanism. Present only when the
/// BLC code version is 4. Version 5 uses an int64 in this position instead.
/// Names are inferred and not confirmed against the runtime XLua registration.
/// </summary>
public enum EVFSFileTag : byte
{
    Base = 0,
    Patch = 1,
}
