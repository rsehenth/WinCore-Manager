using System.IO;
using System.Text.Json;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

internal sealed partial class WinGetInterruptionStore
{
    private const int QueueSizeLimit = 256 * 1024;
    private string QueuePath => _file + ".queue";

    internal SuspendedUpdateQueue? LoadSuspendedQueue()
    {
        if (!File.Exists(QueuePath)) return null;
        using var lease = AcquireLease();
        var saved = ReadQueue();
        if (saved is null) return null;
        if (!HasReceipt) return saved.Reviewed ? saved : null;
        using var receipt = Load(lease, allowUnreadable: false);
        return receipt.OperationId == saved.ReceiptId ? saved : null;
    }

    internal bool SaveSuspendedQueue(Guid receiptId, WinGetPackage[] packages)
    {
        using var receipt = Open();
        if (receiptId != receipt.OperationId) return false;
        SaveSuspendedQueue(receipt, packages);
        return true;
    }

    // The active receipt already holds the same lease as recovery and resume.
    internal void SaveSuspendedQueue(Receipt receipt, WinGetPackage[] packages)
    {
        if (receipt.OperationId is not { } id || receipt.IsExternal)
            throw new IOException("The update queue has no owning receipt.");
        if (packages.Length > 0)
        {
            WriteQueue(new(id, packages));
            return;
        }
        // The last attempt removes the tail handed over by the previous one.
        if (!File.Exists(QueuePath)) return;
        CheckRegularFile(QueuePath);
        File.Delete(QueuePath);
    }

    internal void DiscardQueueCheckpoint(Guid receiptId)
    {
        if (ReadQueue() is { Reviewed: false } saved && saved.ReceiptId == receiptId)
            File.Delete(QueuePath);
    }

    // Called only while the reviewed receipt still holds its lease. The receipt guards the
    // window between writing the review record here and the existing supervised receipt clear.
    // A conclusive attempt uses it too: the tail outlives the receipt until the next attempt owns it,
    // so an abrupt exit between attempts still offers a resume that rescans and asks for review.
    internal void MarkSuspendedQueueReviewed(Receipt receipt)
    {
        if (!receipt.IsUnreadable && ReadQueue() is { } saved && saved.ReceiptId == receipt.OperationId && !saved.Reviewed)
            WriteQueue(saved with { Reviewed = true });
    }

    internal bool ConsumeSuspendedQueue(Guid receiptId)
    {
        using var lease = AcquireLease();
        if (HasReceipt || ReadQueue() is not { Reviewed: true } saved || saved.ReceiptId != receiptId) return false;
        File.Delete(QueuePath);
        return true;
    }

    private SuspendedUpdateQueue? ReadQueue()
    {
        try
        {
            CheckRegularFile(QueuePath);
            using var stream = new FileStream(QueuePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > QueueSizeLimit) return null;
            var saved = JsonSerializer.Deserialize<SuspendedUpdateQueue>(stream, ReceiptJson);
            var unique = new HashSet<(string Source, string Id)>();
            return saved is { Packages.Length: > 0 } && saved.ReceiptId != Guid.Empty &&
                saved.Packages.All(package => package is { Id.Length: > 0, Source.Length: > 0, Name.Length: > 0,
                    InstalledVersion: not null, AvailableVersion.Length: > 0 } &&
                    WingetTableParser.IsActionableId(package.Id) && WinGetTrust.IsAllowedSource(package.Source) &&
                    unique.Add((package.Source.ToUpperInvariant(), package.Id.ToUpperInvariant())))
                ? saved : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException) { return null; }
    }

    private void WriteQueue(SuspendedUpdateQueue saved)
    {
        CheckRegularFile(QueuePath, allowMissing: true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, ReceiptJson);
        if (bytes.Length > QueueSizeLimit) throw new IOException("The suspended update queue exceeds its size limit.");
        PersistFile(QueuePath, bytes, overwrite: true);
    }

    internal sealed record SuspendedUpdateQueue(Guid ReceiptId, WinGetPackage[] Packages, bool Reviewed = false);
}
