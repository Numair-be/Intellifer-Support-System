using System.Globalization;
using System.Net;
using System.Text;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Intellifer.Hub.Application;
using Intellifer.Hub.Domain;
using Intellifer.Hub.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Intellifer.Hub.Infrastructure.Gmail;

public sealed class GmailMailboxClient(IOptions<GmailOptions> options, ILogger<GmailMailboxClient> logger) : IGmailMailbox
{
    private readonly object _gate = new();
    private GmailService? _service;
    private string? _labelId;

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<GmailProfile> GetProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await Service.Users.GetProfile(UserId).ExecuteAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(profile.EmailAddress) || profile.HistoryId is null)
            throw new InvalidOperationException("Gmail did not return a mailbox address and history id.");

        return new GmailProfile(profile.EmailAddress, profile.HistoryId.Value.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<GmailHistoryPage> ListHistoryAsync(string startHistoryId, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(startHistoryId, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            throw new GmailHistoryExpiredException();

        var ids = new List<string>();
        string? pageToken = null;
        ulong? latest = null;
        var pages = 0;

        do
        {
            var request = Service.Users.History.List(UserId);
            request.StartHistoryId = start;
            request.HistoryTypes = UsersResource.HistoryResource.ListRequest.HistoryTypesEnum.MessageAdded;
            request.PageToken = pageToken;
            ListHistoryResponse response;
            try
            {
                response = await request.ExecuteAsync(cancellationToken);
            }
            catch (GoogleApiException exception) when (IsExpiredHistory(exception))
            {
                throw new GmailHistoryExpiredException();
            }

            latest = response.HistoryId ?? latest;
            if (response.History is not null)
            {
                foreach (var record in response.History)
                {
                    if (record.MessagesAdded is null)
                        continue;
                    foreach (var added in record.MessagesAdded)
                    {
                        if (!string.IsNullOrWhiteSpace(added.Message?.Id))
                            ids.Add(added.Message.Id);
                    }
                }
            }

            pageToken = response.NextPageToken;
            pages++;
        }
        while (!string.IsNullOrEmpty(pageToken) && pages < 100);

        if (!string.IsNullOrEmpty(pageToken))
            throw new InvalidOperationException("Gmail history returned more than 100 pages.");

        return new GmailHistoryPage(latest?.ToString(CultureInfo.InvariantCulture), Distinct(ids));
    }

    public async Task<IReadOnlyList<string>> ListRecentMessageIdsAsync(
        int newerThanDays,
        int maxMessages,
        CancellationToken cancellationToken)
    {
        var request = Service.Users.Messages.List(UserId);
        request.Q = $"newer_than:{newerThanDays}d -in:spam -in:trash -in:drafts -in:sent";
        request.MaxResults = maxMessages;
        var response = await request.ExecuteAsync(cancellationToken);
        var ids = response.Messages?
            .Select(message => message.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList() ?? [];
        ids.Reverse();
        return ids;
    }

    public async Task<GmailFetchedMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        var request = Service.Users.Messages.Get(UserId, messageId);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        Message message;
        try
        {
            message = await request.ExecuteAsync(cancellationToken);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var texts = new List<(string Mime, string Data)>();
        var files = new List<string>();
        var images = new List<GmailImagePart>();
        Collect(message.Payload, 0, texts, files, images);
        var plain = texts.FirstOrDefault(text => text.Mime == "text/plain").Data;
        var html = texts.FirstOrDefault(text => text.Mime == "text/html").Data;
        var received = message.InternalDate is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(message.InternalDate.Value)
            : DateTimeOffset.UtcNow;

        return new GmailFetchedMessage(
            message.Id ?? messageId,
            message.ThreadId ?? messageId,
            Header(message, "From"),
            Header(message, "Subject"),
            plain ?? html ?? "",
            plain is null && html is not null,
            message.LabelIds?.ToArray() ?? [],
            files,
            received,
            images);
    }

    public async Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken cancellationToken)
    {
        var body = await Service.Users.Messages.Attachments.Get(UserId, messageId, attachmentId).ExecuteAsync(cancellationToken);
        return string.IsNullOrEmpty(body.Data) ? [] : DecodeBase64UrlBytes(body.Data);
    }

    public async Task ApplyProcessedLabelAsync(string messageId, CancellationToken cancellationToken)
    {
        var labelName = options.Value.ProcessedLabel?.Trim();
        if (string.IsNullOrWhiteSpace(labelName))
            return;

        try
        {
            var labelId = await GetOrCreateLabelAsync(labelName, cancellationToken);
            var modify = new ModifyMessageRequest { AddLabelIds = [labelId] };
            await Service.Users.Messages.Modify(modify, UserId, messageId).ExecuteAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _labelId = null;
            logger.LogWarning(exception, "Could not apply the Gmail label to {MessageId}", messageId);
        }
    }

    private async Task<string> GetOrCreateLabelAsync(string labelName, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_labelId))
            return _labelId;

        var listed = await Service.Users.Labels.List(UserId).ExecuteAsync(cancellationToken);
        var existing = listed.Labels?.FirstOrDefault(label => label.Name == labelName);
        if (!string.IsNullOrWhiteSpace(existing?.Id))
        {
            _labelId = existing.Id;
            return existing.Id;
        }

        var created = await Service.Users.Labels.Create(new Label
        {
            Name = labelName,
            LabelListVisibility = "labelShow",
            MessageListVisibility = "show"
        }, UserId).ExecuteAsync(cancellationToken);

        _labelId = created.Id ?? throw new InvalidOperationException("Gmail did not return the created label id.");
        return _labelId;
    }

    /// <summary>Replies inside an existing thread so the customer sees it under their original email.</summary>
    public async Task<string?> SendReplyAsync(string threadId, string to, string subject, string body, CancellationToken cancellationToken)
    {
        // Thread the reply properly: In-Reply-To and References point at the thread's latest message.
        var threadRequest = Service.Users.Threads.Get(UserId, threadId);
        threadRequest.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Metadata;
        threadRequest.MetadataHeaders = new Google.Apis.Util.Repeatable<string>(["Message-ID", "References"]);
        var thread = await threadRequest.ExecuteAsync(cancellationToken);
        var last = thread.Messages?.LastOrDefault();
        var inReplyTo = last is null ? null : Header(last, "Message-ID");
        var references = string.Join(' ', new[] { last is null ? null : Header(last, "References"), inReplyTo }.Where(value => !string.IsNullOrWhiteSpace(value)));

        const string Crlf = "\r\n";
        var mime = new StringBuilder();
        mime.Append("To: ").Append(SingleLine(to)).Append(Crlf);
        mime.Append("Subject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(SingleLine(subject)))).Append("?=").Append(Crlf);
        if (!string.IsNullOrWhiteSpace(inReplyTo))
        {
            mime.Append("In-Reply-To: ").Append(SingleLine(inReplyTo)).Append(Crlf);
            mime.Append("References: ").Append(SingleLine(references)).Append(Crlf);
        }

        mime.Append("MIME-Version: 1.0").Append(Crlf);
        mime.Append("Content-Type: text/plain; charset=UTF-8").Append(Crlf);
        mime.Append("Content-Transfer-Encoding: base64").Append(Crlf).Append(Crlf);
        var normalized = body.Replace(Crlf, "\n").Replace("\n", Crlf);
        mime.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized), Base64FormattingOptions.InsertLineBreaks));

        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(mime.ToString()))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var sent = await Service.Users.Messages.Send(new Message { Raw = raw, ThreadId = threadId }, UserId)
            .ExecuteAsync(cancellationToken);
        logger.LogInformation("Sent Gmail reply {MessageId} in thread {ThreadId}", sent.Id, threadId);
        return sent.Id;
    }

    // Header values must not contain line breaks (header injection).
    private static string SingleLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();

    private GmailService Service
    {
        get
        {
            if (_service is not null)
                return _service;

            lock (_gate)
            {
                if (_service is not null)
                    return _service;

                var settings = options.Value;
                var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
                {
                    ClientSecrets = new ClientSecrets
                    {
                        ClientId = settings.ClientId,
                        ClientSecret = settings.ClientSecret
                    },
                    Scopes = [GmailService.Scope.GmailModify]
                });
                var credential = new UserCredential(flow, "intellifer-hub", new TokenResponse
                {
                    RefreshToken = settings.RefreshToken
                });
                _service = new GmailService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "Intellifer Hub"
                });
                return _service;
            }
        }
    }

    private string UserId => string.IsNullOrWhiteSpace(options.Value.Mailbox) ? "me" : options.Value.Mailbox;

    private static bool IsExpiredHistory(GoogleApiException exception)
    {
        if (exception.HttpStatusCode == HttpStatusCode.NotFound)
            return true;

        var text = exception.Error?.Message ?? exception.Message;
        return exception.HttpStatusCode == HttpStatusCode.BadRequest
            && text.Contains("history", StringComparison.OrdinalIgnoreCase);
    }

    private static void Collect(
        MessagePart? part,
        int depth,
        List<(string Mime, string Data)> texts,
        List<string> files,
        List<GmailImagePart> images)
    {
        if (part is null || depth > 20)
            return;

        if (!string.IsNullOrWhiteSpace(part.Filename))
            files.Add(part.Filename);

        // Attached or pasted images. Pasted screenshots have no file name, so give them one.
        if (InboundAttachment.IsImage(part.MimeType) && (part.Body?.AttachmentId is not null || part.Body?.Data is not null))
        {
            var name = string.IsNullOrWhiteSpace(part.Filename)
                ? $"image-{images.Count + 1}.{part.MimeType!.Split('/')[1].Split(';')[0]}"
                : part.Filename;
            var inline = part.Body?.AttachmentId is null && part.Body?.Data is { } small ? DecodeBase64UrlBytes(small) : null;
            var disposition = part.Headers?
                .FirstOrDefault(header => string.Equals(header.Name, "Content-Disposition", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            var isInline = !disposition.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
            images.Add(new GmailImagePart(name, part.MimeType!.Split(';')[0].Trim(), part.Body?.Size ?? inline?.Length ?? 0, part.Body?.AttachmentId, inline, isInline));
        }

        if (part.Body?.Data is { Length: > 0 } data && part.MimeType is "text/plain" or "text/html")
            texts.Add((part.MimeType, DecodeBase64Url(data)));

        if (part.Parts is null)
            return;

        foreach (var child in part.Parts)
            Collect(child, depth + 1, texts, files, images);
    }

    private static string? Header(Message message, string name) =>
        message.Payload?.Headers?
            .FirstOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static string DecodeBase64Url(string data) => Encoding.UTF8.GetString(DecodeBase64UrlBytes(data));

    private static byte[] DecodeBase64UrlBytes(string data)
    {
        var padded = data.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    private static List<string> Distinct(List<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (seen.Add(id))
                ordered.Add(id);
        }

        return ordered;
    }
}
