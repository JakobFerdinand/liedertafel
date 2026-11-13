// Public synthetic framework observations for the frozen endpoints' ReadFormAsync
// + StringValues conversion + Int32.TryParse boundary. No database/model/input.
using System.Globalization;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
string[] tokens = ["", " ", "0", "1", "01", "+1", "-1", "2147483647", "2147483648", "-2147483648", "-2147483649", "1.0", "1e0", "1_0", "1,000", "0x1", "#1", "++1", "+-1", "١", "１", "1\0", "1\0\0", "1 \0", "1\0 ", "1 \0 ", "1\0x", "\01", "-0", "+0"];
var parts = tokens.Select(t => new[] { t }).ToList();
foreach (var whitespace in new[] { " ", "\t", "\r", "\n", "\v", "\f", "\u0085", "\u00a0", "\u1680", "\u2000", "\u2028", "\u202f", "\u3000" })
{
 parts.Add([whitespace + "+0001" + whitespace]);
 parts.Add(["1" + whitespace + "\0"]);
 parts.Add(["1\0" + whitespace]);
}
parts.AddRange([new[] { "", "1" }, new[] { "1", "" }, new[] { "", "", "1", "" }, new[] { "1", "2" }, new[] { "", "" }, new[] { "1", "", "2" }]);
var rows = new List<object>();
foreach (var values in parts)
{
 foreach (var multipart in new[] {false,true})
 {
 var encoded = string.Join("&", values.Select((v,i) => (i % 2 == 0 ? "anfrageId=" : "ANFRAGEID=") + Uri.EscapeDataString(v)));
 var context = new DefaultHttpContext();
 context.Request.ContentType = multipart ? "multipart/form-data; boundary=oracle230" : "application/x-www-form-urlencoded";
 var body = multipart ? string.Concat(values.Select((v,i)=>"--oracle230\r\nContent-Disposition: form-data; name=\""+(i%2==0?"anfrageId":"ANFRAGEID")+"\"\r\n\r\n"+v+"\r\n"))+"--oracle230--\r\n" : encoded;
 context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
 try {
  var form = await context.Request.ReadFormAsync();
  var value = (string?)form["anfrageId"];
  var parsed = int.TryParse(value, out var number);
  rows.Add(new { multipart, parts = values, value, parsed, number = parsed ? (int?)number : null, error = (string?)null });
 } catch (InvalidDataException) {rows.Add(new {multipart,parts=values,value=(string?)null,parsed=false,number=(int?)null,error="InvalidDataException"});}
 }
}
Console.WriteLine(JsonSerializer.Serialize(new { culture = CultureInfo.CurrentCulture.Name, rows }));
