namespace Archive.Backend.Inventur;

/// <summary>
/// #98 Quellen-Inventur: the serializable result of one deterministic walk
/// over a source folder export. The shapes live here so the bulk import (#143)
/// can rely on the same field names the JSON report uses (camelCase).
/// </summary>
public sealed record InventurRun(
	string Root,
	InventurTotals Totals,
	IReadOnlyList<InventurByType> ByType,
	IReadOnlyList<InventurLargest> Largest,
	IReadOnlyList<InventurDuplicate> Duplicates,
	IReadOnlyList<InventurShortcut> Shortcuts,
	InventurPdfTotals Pdf,
	InventurMediaTotals Media,
	InventurReadingCost ReadingCost,
	IReadOnlyList<InventurFileEntry> Files);

/// <summary>The walk totals; paths are '/'-relative to the walk root.</summary>
public sealed record InventurTotals(
	int FileCount,
	long Bytes,
	int FolderCount,
	IReadOnlyList<string> EmptyFolders);

/// <summary>Count and aggregate bytes per detected content type; null when the leading bytes name no container.</summary>
public sealed record InventurByType(string? ContentType, int FileCount, long Bytes);

/// <summary>One entry of the largest-files view for upload resumability (#98, ARC-017).</summary>
public sealed record InventurLargest(string Path, long Bytes);

/// <summary>
/// One same-name group inside a folder: a duplicate candidate is reported,
/// never resolved. Byte equality and correction relationships are unknown to
/// the deterministic walk and stay with the editor.
/// </summary>
public sealed record InventurDuplicate(
	string Folder,
	string Stem,
	string Extension,
	IReadOnlyList<string> Paths,
	IReadOnlyList<long> Sizes);

/// <summary>A shortcut candidate at or between the walked folders with its recorded target.</summary>
public sealed record InventurShortcut(string Path, string? LinkTarget);

/// <summary>PDF totals: extractable embedded text versus scan, plus the unreadable rest.</summary>
public sealed record InventurPdfTotals(
	int Files,
	int EmbeddedText,
	int Scanned,
	int ScannedPages,
	IReadOnlyList<InventurPdfFailure> Unreadable);

/// <summary>One PDF the deterministic probes could not read back, with the German reason.</summary>
public sealed record InventurPdfFailure(string Path, string Reason);

/// <summary>Audio/video/MIDI presence: what the container check can say without a decoder.</summary>
public sealed record InventurMediaTotals(int MidiFiles, int MediaFiles, int PlaybackReady, int NeedingConversion);

/// <summary>
/// Analytical reading-cost estimate inputs (architecture §14): spreadsheet of
/// the scanned pages through the reading job's own page and call bounds, priced
/// from <c>Archive:Ai:Models</c>. No model is ever called for this estimate.
/// </summary>
public sealed record InventurReadingCost(
	string Model,
	decimal? PriceInputEurPerMillion,
	decimal? PriceOutputEurPerMillion,
	int ScannedDocuments,
	int ScannedPagesRead,
	int Calls,
	long InputTokens,
	long OutputTokens,
	decimal? EstimatedEur,
	string? Note,
	InventurReadingCostAssumptions Assumptions);

/// <summary>
/// The priced assumptions, taken from the reading job's configuration. A
/// missing price answers null fields honestly instead of inventing one.
/// </summary>
public sealed record InventurReadingCostAssumptions(
	int MaxPagesPerRequest,
	int ImagesPerCall,
	int TokensPerImage,
	int PromptTokensPerCall,
	int TranscriptionMaxOutputTokens);

/// <summary>One walked file with its leading-byte facts.</summary>
/// <param name="Flags">Facts the walk itself observed; free of any catalogue interpretation.</param>
public sealed record InventurFileEntry(
	string Path,
	long Bytes,
	string? ContentType,
	bool? Playable,
	string? LinkTarget,
	IReadOnlyList<string> Flags,
	InventurPdfFacts? Pdf);

/// <summary>
/// What the two deterministic PDF probes say about one document. <see
/// cref="Text"/> is <c>embeddedText</c> (PdfPig found extractable text),
/// <c>scan</c> (parsed but zero characters — the reading job would read the
/// pages as images) or <c>failed</c> with an explained German <see
/// cref="Reason"/>; <see cref="Pages"/> comes from the PDFium page count and
/// may be null when the renderer cannot open the bytes.
/// </summary>
public sealed record InventurPdfFacts(int? Pages, string Text, string? Reason);
