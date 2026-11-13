// Public framework observations for the frozen FormFeature/GetName boundary;
// no database, document corpus, provider, socket or credential.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
var rows=new List<object>();
async Task Observe(string label,string extra) {
 var context=new DefaultHttpContext();context.Request.ContentType="multipart/form-data; boundary=oracle230";
 context.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes("--oracle230\r\n"+extra+"\r\n--oracle230--\r\n"));
 try{var form=await context.Request.ReadFormAsync();rows.Add(new{label,error=(string?)null,fields=form.Keys.ToArray(),files=form.Files.Select(f=>new{f.Name,f.FileName,f.Length}).ToArray()});}
 catch(Exception e){rows.Add(new{label,error=e.GetType().Name});}
}
foreach(var (label,extra) in new[]{
 ("ordinary","Content-Disposition: form-data; name=\"datei\"; filename=\"a.pdf\"\r\n\r\nx"),
 ("upper-name","Content-Disposition: form-data; NAME=\"datei\"; filename=\"a.pdf\"\r\n\r\nx"),
 ("upper-filename","Content-Disposition: form-data; name=\"datei\"; FILENAME=\"a.pdf\"\r\n\r\nx"),
 ("star-filename","Content-Disposition: form-data; name=\"datei\"; filename*=utf-8''a.pdf\r\n\r\nx"),
 ("empty-file","Content-Disposition: form-data; name=\"datei\"; filename=\"\"\r\n\r\n"),
 ("empty-named-file","Content-Disposition: form-data; name=\"datei\"; filename=\"a.pdf\"\r\n\r\n"),
 ("empty-name-with-bytes","Content-Disposition: form-data; name=\"datei\"; filename=\"\"\r\n\r\nx"),
 ("escaped-name","Content-Disposition: form-data; name=\"date\\i\"; filename=\"a.pdf\"\r\n\r\nx"),
 ("unicode-filename","Content-Disposition: form-data; name=\"datei\"; filename=\"Grüße.pdf\"\r\n\r\nx"),
 ("mime-name","Content-Disposition: form-data; name=\"=?utf-8?B?ZGF0ZWk=?=\"; filename=\"a.pdf\"\r\n\r\nx"),
 ("mime-empty-filename","Content-Disposition: form-data; name=\"datei\"; filename=\"=?utf-8?B??=\"\r\n\r\nx"),
 ("attachment","Content-Disposition: attachment; name=\"datei\"; filename=\"a.pdf\"\r\n\r\nx"),
 ("noncanonical-base64-name","Content-Disposition: form-data; name=\"=?utf-8?B?ZGF0ZWl=?=\"; filename=\"a.pdf\"\r\n\r\nx")}) await Observe(label,extra);
string[] aliases=["ansi_x3.4-1968","ansi_x3.4-1986","ascii","cp367","csascii","ibm367","iso-ir-6","iso646-us","iso_646.irv:1991","us","us-ascii","cp819","csisolatin1","ibm819","iso-8859-1","iso-ir-100","iso8859-1","iso_8859-1","iso_8859-1:1987","l1","latin1","unicode-1-1-utf-8","unicode-2-0-utf-8","utf-8","x-unicode-1-1-utf-8","x-unicode-2-0-utf-8","iso-10646-ucs-2","ucs-2","unicode","utf-16","utf-16le","unicodefffe","utf-16be","utf-32","utf-32le","utf-32be"];
foreach(var alias in aliases) {
 var data=Convert.ToBase64String(Encoding.GetEncoding(alias).GetBytes("datei"));
 await Observe("alias-"+alias,"Content-Disposition: form-data; name=\"=?"+alias+"?B?"+data+"?=\"; filename=\"a.pdf\"\r\n\r\nx");
}
Console.WriteLine(JsonSerializer.Serialize(rows));
