// Execute real FormFeature on exactly the public bytes used by native Rust gates.
using System.Text.Json;
using Microsoft.AspNetCore.Http;
var cases=JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var results=new List<object>();
foreach(var row in cases.RootElement.EnumerateArray()) {
 var context=new DefaultHttpContext();context.Request.ContentType=row.GetProperty("contentType").GetString();
 context.Request.Body=new MemoryStream(Convert.FromBase64String(row.GetProperty("body").GetString()!));
 try {var form=await context.Request.ReadFormAsync();results.Add(new{name=row.GetProperty("name").GetString(),error=(string?)null,entwurfId=(string?)form["entwurfId"],anfrageId=(string?)form["anfrageId"],fileCount=form.Files.Count,files=form.Files.Take(2).Select(f=>new{f.Name,f.FileName,f.Length}).ToArray()});}
 catch(Exception error) when(error is InvalidDataException or NotSupportedException){results.Add(new{name=row.GetProperty("name").GetString(),error=error.GetType().Name});}
}
Console.WriteLine(JsonSerializer.Serialize(results));
