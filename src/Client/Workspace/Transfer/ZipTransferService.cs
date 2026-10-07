using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using Client.Workspace.Domain;
using Client.Workspace.Storage;

namespace Client.Workspace.Transfer;

/// <summary>
/// Bounded C# ZIP import/export over <see cref="System.IO.Compression.ZipArchive"/>.
/// C# owns all path/size/count/depth validation, CRC-32 verification, content
/// classification and the staged→publish workflow; no JS archive library is used
/// and no backend is involved. ZIP entries are project-relative files at the
/// archive root (no wrapper directory, embedded app database, credentials,
/// session data, history or mandatory proprietary metadata). Empty directory
/// entries are optional and are not persisted as project objects. A round trip
/// preserves paths and bytes, not the source application's IDs or history.
/// </summary>
public sealed class ZipTransferService : IProjectTransfer
{
    private readonly IProjectRepository _repo;

    public ZipTransferService(IProjectRepository repo) => _repo = repo;

    // ---- import staging ----

    public Task<TransferStageResult> StageZipImportAsync(byte[] zipBytes, string defaultName, CancellationToken ct)
        => Task.FromResult(StageZipImport(zipBytes, defaultName, ct));

    private static TransferStageResult StageZipImport(byte[] zipBytes, string defaultName, CancellationToken ct)
    {
        if (zipBytes is null || zipBytes.Length == 0)
            return Invalid("Archive is empty.", defaultName);
        if (zipBytes.Length > ArchiveLimits.ZipInputSize)
            return Invalid($"Archive exceeds the {ArchiveLimits.ZipInputSize / (1024 * 1024)} MiB input limit.", defaultName);

        // Check for a multi-volume archive (spanned) signature early so it is
        // reported before ZipArchive tries to interpret it. The spanned-signature
        // PK\x07\x08 is the only common multi-volume marker ZIP emits; we reject
        // it explicitly. (Split members are rejected below as they cannot be read.)
        if (zipBytes.Length >= 4)
        {
            uint sig = (uint)(zipBytes[0] | (zipBytes[1] << 8) | (zipBytes[2] << 16) | (zipBytes[3] << 24));
            if (sig == 0x08074b50u) { /* data descriptor, common and valid */ }
            if ((zipBytes[2] == 0x07 && zipBytes[3] == 0x08) ||
                (zipBytes[2] == 0x07 && zipBytes[3] == 0x09))
                return Invalid("Multi-volume archives are not supported.", defaultName);
        }

        using var ms = new MemoryStream(zipBytes, writable: false);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            return Invalid($"Archive is malformed or truncated: {ex.Message}", defaultName);
        }

        using (zip)
        {
            if (zip.Entries.Count > ArchiveLimits.MaxEntries)
                return Invalid($"Archive has too many entries (limit {ArchiveLimits.MaxEntries}).", defaultName);

            long expanded = 0;
            var accepted = new List<StagedFile>();
            var errors = new List<ValidationError>();
            var seenKeys = new HashSet<string>(); // NFC + ordinal-ci comparison keys
            var directories = new HashSet<string>(); // directory comparison keys (with trailing slash)
            // Track accepted file paths (comparison keys) for prefix-conflict checks.
            var filePathKeys = new HashSet<string>();

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                // Reject nested archives outright (do not recursively expand).
                if (IsZipLikeName(entry.Name) && entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new ValidationError(entry.FullName, "NestedArchive",
                        "Nested archives are not expanded; extract and import separately."));
                    continue;
                }

                bool isDirectory = entry.FullName.EndsWith('/') || entry.Length == 0 && string.IsNullOrEmpty(entry.Name);
                // Reject external-attribute symlink/special file entries (Unix
                // socket/char/block/fifo) using the high external-attribute byte.
                if (entry.ExternalAttributes != 0)
                {
                    int unixMode = (int)((uint)entry.ExternalAttributes >> 16);
                    int typeBits = unixMode & 0xF000;
                    // 0xA000 = symlink, 0xC000 = socket, 0x2000=char, 0x6000=block, 0x1000=fifo
                    if (typeBits == 0xA000 || typeBits == 0xC000 || typeBits == 0x2000 || typeBits == 0x6000 || typeBits == 0x1000)
                    {
                        errors.Add(new ValidationError(entry.FullName, "SpecialFile",
                            "Symlinks and special file entries are rejected."));
                        continue;
                    }
                }

                // Encrypted or unsupported-compression entries are rejected by the
                // read path: .NET's ZipArchive throws InvalidDataException when an
                // entry is encrypted or uses an unsupported compression method. We
                // classify the message below rather than relying on a public flag,
                // which ZipArchiveEntry does not expose.

                if (isDirectory)
                {
                    // Optional directory declaration. Validate its path and reject
                    // duplicates/prefix conflicts. Not persisted as a project object.
                    var dirPath = entry.FullName.TrimEnd('/');
                    var normalized = ProjectPathValidator.Normalize(dirPath);
                    if (normalized is null)
                    {
                        errors.Add(new ValidationError(entry.FullName, "InvalidPath", "Directory path is invalid."));
                        continue;
                    }
                    var v = ProjectPathValidator.Validate(normalized);
                    if (v is { } ve)
                    {
                        errors.Add(new ValidationError(entry.FullName, ve.ToString(), "Directory path is invalid."));
                        continue;
                    }
                    var key = ProjectPathValidator.ComparisonKey(normalized) + "/";
                    if (ProjectPathValidator.FindConflict(normalized, isDirectory: true, filePathKeys, directories) is { } dirConflict)
                    {
                        errors.Add(new ValidationError(entry.FullName,
                            dirConflict.IsDuplicate ? "DuplicatePath" : "PrefixConflict",
                            $"Directory path conflicts: {dirConflict.Path}"));
                        continue;
                    }
                    directories.Add(key);
                    continue;
                }

                // File entry.
                var name = ProjectPathValidator.Normalize(entry.FullName);
                if (name is null)
                {
                    errors.Add(new ValidationError(entry.FullName, "InvalidPath", "Path is empty."));
                    continue;
                }
                if (name.EndsWith('/'))
                {
                    errors.Add(new ValidationError(entry.FullName, "InvalidPath", "File path ends with a slash."));
                    continue;
                }
                var pathErr = ProjectPathValidator.Validate(name);
                if (pathErr is { } pe)
                {
                    errors.Add(new ValidationError(entry.FullName, pe.ToString(), "Path is invalid."));
                    continue;
                }

                // Declared size check first (header), but enforce actual byte count
                // while streaming below.
                if (entry.Length > ArchiveLimits.SingleFile)
                {
                    errors.Add(new ValidationError(entry.FullName, "FileTooLarge",
                        $"File exceeds the {ArchiveLimits.SingleFile / (1024 * 1024)} MiB single-file limit."));
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = ReadEntryWithCrcCheck(entry);
                }
                catch (InvalidDataException ex)
                {
                    var msg = ex.Message ?? "";
                    var code = msg.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
                        ? "Encrypted"
                        : msg.Contains("compression", StringComparison.OrdinalIgnoreCase) ||
                          msg.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                        ? "UnsupportedCompression"
                        : "CorruptEntry";
                    errors.Add(new ValidationError(entry.FullName, code, msg));
                    continue;
                }
                ct.ThrowIfCancellationRequested();

                if (bytes.LongLength > ArchiveLimits.SingleFile)
                {
                    errors.Add(new ValidationError(entry.FullName, "FileTooLarge",
                        "Actual extracted size exceeds the single-file limit."));
                    continue;
                }
                expanded += bytes.LongLength;
                if (expanded > ArchiveLimits.ExpandedSize)
                {
                    errors.Add(new ValidationError(entry.FullName, "ExpandedTooLarge",
                        $"Expanded project exceeds the {ArchiveLimits.ExpandedSize / (1024 * 1024)} MiB limit."));
                    continue;
                }

                // Duplicate / prefix-conflict detection using NFC + ordinal-ci.
                if (ProjectPathValidator.FindConflict(name, isDirectory: false, filePathKeys, directories) is { } dup)
                {
                    errors.Add(new ValidationError(entry.FullName,
                        dup.IsDuplicate ? "DuplicatePath" : "PrefixConflict",
                        $"Path conflicts with an existing entry: {dup.Path}"));
                    continue;
                }
                filePathKeys.Add(ProjectPathValidator.ComparisonKey(name));

                var (mediaKind, hasBom) = Classify(bytes, name);
                var hash = SHA256.FromBytes(System.Security.Cryptography.SHA256.HashData(bytes));
                accepted.Add(new StagedFile(name, mediaKind, hasBom, bytes, hash, bytes.Length));
            }

            var projectName = string.IsNullOrWhiteSpace(defaultName) ? "Imported project" : defaultName.Trim();
            var staged = new StagedProject(projectName, accepted.ToImmutableArray(), errors);
            // If any entry failed validation, the stage is not publishable as-is.
            bool valid = errors.Count == 0;
            return new TransferStageResult(valid, staged, Array.Empty<string>());
        }
    }

    private static bool IsZipLikeName(string name) =>
        !string.IsNullOrEmpty(name) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Read an entry's bytes while enforcing the actual byte count, and
    /// verify CRC-32 against <see cref="ZipArchiveEntry.Crc32"/>. .NET 10 does
    /// not auto-validate CRC on read, so this is mandatory.</summary>
    private static byte[] ReadEntryWithCrcCheck(ZipArchiveEntry entry)
    {
        // Enforce actual size during streaming: copy into a bounded buffer, refusing\n        // to materialize more than the single-file limit even if the header lied.
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > ArchiveLimits.SingleFile)
                throw new InvalidDataException("Actual entry size exceeds the single-file limit.");
            ms.Write(buffer, 0, read);
        }
        var bytes = ms.ToArray();
        if (bytes.Length != entry.Length)
            throw new InvalidDataException($"Actual length {bytes.Length} != declared length {entry.Length}.");
        // ZipArchiveEntry.Crc32 is the stored CRC over uncompressed bytes.
        // .NET 10 does not auto-validate CRC on read, so compare always — even
        // when the stored CRC is 0 (the legitimate CRC of an empty file is 0;
        // a zero CRC on a non-empty file is a corrupt/edited archive and must be
        // rejected, not bypassed).
        {
            uint crc = Crc32.Compute(bytes);
            if (crc != entry.Crc32)
                throw new InvalidDataException("CRC-32 mismatch; entry is corrupt.");
        }
        return bytes;
    }

    private static (string MediaKind, bool HasBom) Classify(byte[] bytes, string normalizedPath)
    {
        if (ProjectPathValidator.MediaKindForPath(normalizedPath) != "text")
            return ("binary", false);
        if (!TextContent.TryDecode(bytes, out _, out var hasBom))
            return ("binary", false); // invalid UTF-8 → binary asset
        return ("text", hasBom);
    }

    private static TransferStageResult Invalid(string message, string defaultName)
    {
        var staged = new StagedProject(defaultName ?? "Imported project",
            ImmutableArray<StagedFile>.Empty,
            new[] { new ValidationError("", "InvalidArchive", message) });
        return new TransferStageResult(false, staged, Array.Empty<string>());
    }

    // ---- publish ----

    public async Task<ImportPublishResult> PublishAsync(StagedProject staged, string projectName, CancellationToken ct)
    {
        if (staged.ValidationErrors.Count > 0)
            return new ImportPublishResult(false, null, null, "ValidationFailed",
                "Cannot publish a staged import with validation errors.");
        var name = string.IsNullOrWhiteSpace(projectName) ? staged.Name : projectName.Trim();
        var renamed = staged with { Name = name };
        return await _repo.PublishImportAsync(renamed, ct);
    }

    // ---- export ----

    public async Task<ExportReceipt> ExportZipAsync(ExportSource source, CancellationToken ct)
    {
        var revision = source.Revision;
        ct.ThrowIfCancellationRequested();

        long total = 0;
        foreach (var f in revision.Files)
        {
            total += f.ByteLength;
            if (total > ArchiveLimits.ExpandedSize)
                throw new InvalidOperationException("Export exceeds the project-size limit.");
            if (f.ByteLength > ArchiveLimits.SingleFile)
                throw new InvalidOperationException($"File {f.RelativePath} exceeds the single-file limit.");
        }
        if (revision.Files.Length > ArchiveLimits.MaxEntries)
            throw new InvalidOperationException("Too many entries to export.");

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in revision.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (used.Contains(f.RelativePath)) continue; // defensive; manifest is unique
                used.Add(f.RelativePath);
                var entry = zip.CreateEntry(f.RelativePath, CompressionLevel.Optimal);
                entry.ExternalAttributes = 0; // plain file
                var bytes = await source.ReadContent(f.ContentHash, ct);
                if (bytes.Length != f.ByteLength)
                    throw new InvalidDataException($"Content length mismatch for {f.RelativePath}.");
                using (var es = entry.Open())
                    es.Write(bytes, 0, bytes.Length);
            }
        }

        ms.Position = 0;
        return new ExportReceipt(revision.Id, ms.Length, revision.Files.Length, null);
    }
}

/// <summary>Exception carrying typed transfer error codes for the UI.</summary>
public sealed class TransferException : Exception
{
    public string Code { get; }
    public TransferException(string code, string message) : base(message) => Code = code;
}
