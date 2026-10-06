using Archive.Backend.Assets;
using Archive.Backend.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Archive.Backend.Recordings;

public sealed class RecordingPassageModelConfiguration : IEntityTypeConfiguration<RecordingPassage>
{
	public void Configure(EntityTypeBuilder<RecordingPassage> builder)
	{
		builder.ToTable("recording_passages", t =>
		{
			t.HasCheckConstraint("CK_recording_passages_start", "\"StartSeconds\" >= 0");
			t.HasCheckConstraint("CK_recording_passages_order", "\"EndSeconds\" > \"StartSeconds\"");
		});
		builder.HasKey(x => x.Id);
		builder.Property(x => x.RowVersion).IsConcurrencyToken();
		// A passage is neither a performance nor a recording: it only ties
		// the two together. Restrict everywhere, so no deletion removes
		// editor-made index work (or the evidence it hangs on) silently; the
		// callers that delete performances answer with a clear conflict and
		// the trash slices (ARC-039/ARC-040) decide explicitly.
		// The composite foreign keys carry the event: the recording and the
		// occurrence of one passage can only belong to the same event.
		builder.HasOne(x => x.Recording)
			.WithMany()
			.HasForeignKey(x => new { x.RecordingId, x.EventId })
			.HasPrincipalKey(x => new { x.Id, x.EventId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne(x => x.Performance)
			.WithMany()
			.HasForeignKey(x => new { x.PerformanceId, x.EventId })
			.HasPrincipalKey(x => new { x.Id, x.EventId })
			.OnDelete(DeleteBehavior.Restrict);
		builder.HasOne(x => x.PlaybackRevision)
			.WithMany()
			.HasForeignKey(x => x.PlaybackRevisionId)
			.OnDelete(DeleteBehavior.Restrict);
		// One passage per occurrence and recording (a performance split over
		// several passages is out of scope); the same occurrence may appear
		// once in each of several recordings.
		builder.HasIndex(x => new { x.RecordingId, x.PerformanceId }).IsUnique();
		builder.HasIndex(x => x.PerformanceId);
		builder.HasIndex(x => x.PlaybackRevisionId);
	}
}
