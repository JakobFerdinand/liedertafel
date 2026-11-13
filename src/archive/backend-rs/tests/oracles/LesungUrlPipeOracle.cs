// Actual public FormPipeReader fast (single sequence) / slow (multi sequence).
// Sequence layout is controlled, not a handwritten form parser.
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
var input=JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));var rows=new List<object>();
foreach(var row in input.RootElement.EnumerateArray()) {
 var content=row.GetProperty("contentType").GetString()!;if(!content.StartsWith("application/x-www-form-urlencoded"))continue;
 var bytes=Convert.FromBase64String(row.GetProperty("body").GetString()!);
 foreach(var segmented in new[]{false,true}) {
  try {
   var encoding=MediaTypeHeaderValue.Parse(content).Encoding??Encoding.UTF8;
   // UrlDecoder mutates its PipeReader buffer in place. Each mode gets the
   // identical ORIGINAL bytes, never the prior mode's decoded backing array.
   var result=await new FormPipeReader(new OnceReader(bytes.ToArray(),segmented),encoding).ReadFormAsync();
   rows.Add(new{name=row.GetProperty("name").GetString(),segmented,error=(string?)null,entwurfId=(string?)result.GetValueOrDefault("entwurfId"),anfrageId=(string?)result.GetValueOrDefault("anfrageId"),keys=result.Count,values=result.Values.Sum(v=>v.Count)});
  }catch(Exception error) when(error is InvalidDataException or NotSupportedException){rows.Add(new{name=row.GetProperty("name").GetString(),segmented,error=error.GetType().Name});}
 }
}
Console.WriteLine(JsonSerializer.Serialize(rows));
sealed class Piece:ReadOnlySequenceSegment<byte> {
 public Piece(ReadOnlyMemory<byte> bytes){Memory=bytes;}
 public Piece Append(ReadOnlyMemory<byte> bytes){var next=new Piece(bytes){RunningIndex=RunningIndex+Memory.Length};Next=next;return next;}
}
sealed class OnceReader:PipeReader {
 ReadOnlySequence<byte> remaining;
 public OnceReader(byte[] bytes,bool segmented){
  if(!segmented||bytes.Length==0){remaining=new ReadOnlySequence<byte>(bytes);return;}
  var first=new Piece(bytes.AsMemory(0,Math.Min(257,bytes.Length)));var last=first;
  for(var at=first.Memory.Length;at<bytes.Length;at+=257)last=last.Append(bytes.AsMemory(at,Math.Min(257,bytes.Length-at)));
  remaining=new ReadOnlySequence<byte>(first,0,last,last.Memory.Length);
 }
 public override void AdvanceTo(SequencePosition consumed)=>remaining=remaining.Slice(consumed);
 public override void AdvanceTo(SequencePosition consumed,SequencePosition examined)=>AdvanceTo(consumed);
 public override void CancelPendingRead(){}
 public override void Complete(Exception? exception=null){}
 public override bool TryRead(out ReadResult result){result=new ReadResult(remaining,false,true);return true;}
 public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken=default)=>ValueTask.FromResult(new ReadResult(remaining,false,true));
}
