namespace Common.Messaging.Channels.MicrosoftGraph;

using System.Net.Mail;
using System.Text;
using System.Globalization;

// Used for routed email only. Existing JSON sendMail callers retain their original behavior.
public static class MicrosoftGraphMimeEnvelope
{
    public static string Build(MessageRecipient recipient, MessageRequest request, Guid deliveryId, string sender)
    {
        var metadata = request.Metadata;
        string Get(string key, string fallback = "") => metadata?.GetValueOrDefault(key) ?? fallback;
        string Header(string value)
        {
            if (value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("Invalid email header.");
            return value;
        }
        string Address(string value) => Header(new MailAddress(value).Address);
        string EncodeHeader(string value)
        {
            Header(value);List<string> words=[];StringBuilder chunk=new();
            var elements=StringInfo.GetTextElementEnumerator(value);
            while(elements.MoveNext())
            {
                string part=elements.GetTextElement();
                if(Encoding.UTF8.GetByteCount(chunk.ToString()+part)>42&&chunk.Length>0)
                {words.Add("=?utf-8?B?"+Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString()))+"?=");chunk.Clear();}
                chunk.Append(part);
            }
            if(chunk.Length>0)words.Add("=?utf-8?B?"+Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString()))+"?=");
            return string.Join("\r\n ",words);
        }
        string messageId = Header(Get("Email.MessageId"));
        if (!messageId.StartsWith('<') || !messageId.EndsWith('>') || !messageId.Contains('@'))
            throw new InvalidOperationException("A stable RFC message identifier is required.");
        string boundary = "common-" + deliveryId.ToString("N");
        StringBuilder mime = new();
        mime.Append("From: ");
        if(!string.IsNullOrWhiteSpace(Get("Email.FromName")))mime.Append(EncodeHeader(Get("Email.FromName"))).Append(' ').Append('<').Append(Address(Get("Email.From",sender))).Append('>');
        else mime.Append(Address(Get("Email.From",sender)));
        mime.Append("\r\nTo: ").Append(Address(recipient.Email!))
            .Append("\r\nSubject: ").Append(EncodeHeader(request.Title))
            .Append("\r\nDate: ").Append(DateTimeOffset.UtcNow.ToString("r",CultureInfo.InvariantCulture))
            .Append("\r\nMessage-ID: ").Append(messageId)
            .Append("\r\nX-Studio-Message-Id: ").Append(Header(Get("Studio.MessageId", deliveryId.ToString("D"))))
            .Append("\r\nMIME-Version: 1.0\r\n");
        if (!string.IsNullOrWhiteSpace(Get("Email.ReplyTo"))) mime.Append("Reply-To: ").Append(Address(Get("Email.ReplyTo"))).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(Get("Email.InReplyTo"))) mime.Append("In-Reply-To: ").Append(Header(Get("Email.InReplyTo"))).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(Get("Email.References"))) mime.Append("References: ").Append(Header(Get("Email.References"))).Append("\r\n");
        mime.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n--").Append(boundary)
            .Append("\r\nContent-Type: text/").Append(request.BodyIsHtml ? "html" : "plain").Append("; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n")
            .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Body), Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
        foreach (var attachment in request.Attachments)
        {
            string name = "utf-8''" + Uri.EscapeDataString(Path.GetFileName(attachment.FileName));
            string contentType = Header(attachment.ContentType);
            mime.Append("--").Append(boundary).Append("\r\nContent-Type: ").Append(contentType)
                .Append("\r\nContent-Disposition: attachment; filename*=").Append(name)
                .Append("\r\nContent-Transfer-Encoding: base64\r\n\r\n")
                .Append(Convert.ToBase64String(attachment.Content, Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
        }
        mime.Append("--").Append(boundary).Append("--\r\n");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(mime.ToString()));
    }
}
