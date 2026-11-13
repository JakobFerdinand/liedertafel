// Execute real FormFeature on exactly the public bytes used by native Rust gates.
using System.Text.Json;
using Microsoft.AspNetCore.Http;
var cases=JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var results=new List<object>();
foreach(var row in cases.RootElement.EnumerateArray()) {
 var context=new DefaultHttpContext();context.Request.ContentType=row.GetProperty("contentType").GetString();
 context.Request.Body=new MemoryStream(Convert.FromBase64String(row.GetProperty("body").GetString()!));
 try {var form=await context.Request.ReadFormAsync();var draft=(string?)form["entwurfId"];var request=(string?)form["anfrageId"];results.Add(new{name=row.GetProperty("name").GetString(),error=(string?)null,entwurfId=draft,anfrageId=request,identityValid=Guid.TryParse(draft,out _)&&int.TryParse(request,out var number)&&number>0,fileCount=form.Files.Count,files=form.Files.Take(2).Select(f=>new{f.Name,f.FileName,f.Length}).ToArray()});}
 catch(Exception error) when(error is InvalidDataException or NotSupportedException){results.Add(new{name=row.GetProperty("name").GetString(),error=error.GetType().Name});}
}
Console.WriteLine(JsonSerializer.Serialize(results));
