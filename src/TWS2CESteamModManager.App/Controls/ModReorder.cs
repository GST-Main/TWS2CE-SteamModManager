namespace TWS2CESteamModManager.App.Controls;

public static class ModReorder
{
    /// <summary>Converts an insertion boundary in the original list into a final zero-based index.</summary>
    public static int TargetIndex(int sourceIndex, int insertionBoundary, int count)
    {
        if (count <= 0 || sourceIndex < 0 || sourceIndex >= count || insertionBoundary < 0 || insertionBoundary > count)
            throw new ArgumentOutOfRangeException(nameof(insertionBoundary));
        return insertionBoundary > sourceIndex ? insertionBoundary - 1 : insertionBoundary;
    }
}
