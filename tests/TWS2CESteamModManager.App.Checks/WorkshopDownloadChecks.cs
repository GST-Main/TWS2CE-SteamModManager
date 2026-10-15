using System.IO;
using TWS2CESteamModManager.Steam;

internal static class WorkshopDownloadChecks
{
    public static int Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"workshop-size-checks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "mod.pack");
        int checks = 0;
        try
        {
            File.WriteAllBytes(path, new byte[1181]);
            Accept(0, 1181, "Zero completion size uses matching Workshop metadata.");
            Accept(1181, 1181, "Positive completion size verifies the downloaded file.");
            Accept(1181, 1000, "Positive completion size takes precedence over older metadata.");
            Reject(0, 1182, "Metadata fallback still rejects a truncated download.");
            Reject(0, 1180, "Metadata fallback rejects an oversized download.");
            Reject(1182, 1181, "A positive completion mismatch cannot fall back to metadata.");
            Reject(0, 0, "A nonempty file cannot pass without any matching size.");
            Reject(0, -1, "Invalid fallback metadata cannot validate a download.");
            Reject(-1, 1181, "An invalid completion size cannot validate a download.");
            File.Delete(path);
            Reject(0, 1181, "A missing file fails even with usable metadata.");
            Reject(0, 0, "A missing file cannot be mistaken for an empty file.");
            return checks;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }

        void Accept(int completion, int metadata, string description)
        {
            try { WorkshopDownloader.ValidateLegacyDownload(path, completion, metadata); }
            catch (IOException error) { throw new InvalidOperationException(description, error); }
            checks++;
        }

        void Reject(int completion, int metadata, string description)
        {
            try { WorkshopDownloader.ValidateLegacyDownload(path, completion, metadata); }
            catch (IOException error)
            {
                if (!error.Message.Contains("completion:") || !error.Message.Contains("metadata:"))
                    throw new InvalidOperationException("Size errors include Steam's reported values.", error);
                checks++;
                return;
            }
            throw new InvalidOperationException(description);
        }
    }
}
