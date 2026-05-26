namespace Sqlity.Storage.BTree;

/// <summary>
/// Describes a key range for <see cref="SecondaryBPlusTree.RangeSeek"/>.
/// A null bound means "unbounded in that direction".
/// </summary>
public sealed class IndexSeekRange
{
    private readonly bool _isPrefixRange;

    public IndexSeekRange(
        byte[]? lowerKey,
        bool lowerInclusive,
        byte[]? upperKey,
        bool upperInclusive)
        : this(lowerKey, lowerInclusive, upperKey, upperInclusive, isPrefixRange: false)
    {
    }

    private IndexSeekRange(
        byte[]? lowerKey,
        bool lowerInclusive,
        byte[]? upperKey,
        bool upperInclusive,
        bool isPrefixRange)
    {
        LowerKey = lowerKey;
        LowerInclusive = lowerInclusive;
        UpperKey = upperKey;
        UpperInclusive = upperInclusive;
        _isPrefixRange = isPrefixRange;
    }

    public byte[]? LowerKey { get; }
    public bool LowerInclusive { get; }
    public byte[]? UpperKey { get; }
    public bool UpperInclusive { get; }

    /// <summary>Creates an equality range: lower == upper, both inclusive.</summary>
    public static IndexSeekRange Equality(byte[] key) =>
        new(key, lowerInclusive: true, key, upperInclusive: true);

    /// <summary>
    /// Creates a prefix equality range: matches all keys that start with <paramref name="prefix"/>.
    /// Used for non-unique or partial-coverage index seeks where the actual stored key
    /// has additional columns or PK bytes appended after the prefix.
    /// </summary>
    public static IndexSeekRange PrefixEquality(byte[] prefix) =>
        new(prefix, lowerInclusive: true, prefix, upperInclusive: true, isPrefixRange: true);

    /// <summary>Returns true if <paramref name="key"/> is within the range bounds.</summary>
    public bool Contains(ReadOnlySpan<byte> key)
    {
        if (_isPrefixRange)
        {
            // Key is in range iff it starts with LowerKey (== UpperKey in prefix mode).
            if (LowerKey is null) return true;
            return key.Length >= LowerKey.Length
                && key[..LowerKey.Length].SequenceEqual(LowerKey);
        }

        if (LowerKey is not null)
        {
            var cmp = key.SequenceCompareTo(LowerKey);
            if (LowerInclusive ? cmp < 0 : cmp <= 0) return false;
        }

        if (UpperKey is not null)
        {
            var cmp = key.SequenceCompareTo(UpperKey);
            if (UpperInclusive ? cmp > 0 : cmp >= 0) return false;
        }

        return true;
    }

    /// <summary>Returns true if <paramref name="key"/> is past the upper bound (stop iteration early).</summary>
    public bool ExceedsUpperBound(ReadOnlySpan<byte> key)
    {
        if (_isPrefixRange)
        {
            if (UpperKey is null) return false;
            // Stop when the first UpperKey.Length bytes of key sort after UpperKey.
            var len = Math.Min(key.Length, UpperKey.Length);
            var cmp = key[..len].SequenceCompareTo(UpperKey[..len]);
            if (cmp != 0) return cmp > 0;
            // Equal up to len: if key is shorter, key < prefix → not exceeded.
            return false;
        }

        if (UpperKey is null) return false;
        var c = key.SequenceCompareTo(UpperKey);
        return UpperInclusive ? c > 0 : c >= 0;
    }
}
