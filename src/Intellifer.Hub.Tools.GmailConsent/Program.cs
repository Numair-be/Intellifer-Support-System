using System.Diagnostics;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;

var file = LoadDotEnv();
var clientId = Pick(args, "--client-id", "GMAIL_CLIENT_ID", file);
var clientSecret = Pick(args, "--client-secret", "GMAIL_CLIENT_SECRET", file);
var account = Pick(args, "--account", "GMAIL_ACCOUNT", file);
if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(account))
{
    Console.Error.WriteLine("Set GMAIL_CLIENT_ID, GMAIL_CLIENT_SECRET, and GMAIL_ACCOUNT in .env.");
    Console.Error.WriteLine("GMAIL_ACCOUNT is the support mailbox, for example support@yourcompany.com.");
    Console.Error.WriteLine("You can also pass --client-id, --client-secret, and --account.");
    return 1;
}

if (!clientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("GMAIL_CLIENT_ID is not a Google OAuth client id.");
    Console.Error.WriteLine("Copy the Desktop client id from Google Cloud into .env. It ends with .apps.googleusercontent.com.");
    return 1;
}

account = account.Trim().ToLowerInvariant();
if (!account.Contains('@'))
{
    Console.Error.WriteLine("GMAIL_ACCOUNT must be the full mailbox address, not \"me\".");
    return 1;
}

var storePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "IntelliferHub",
    "gmail-consent");
if (Directory.Exists(storePath))
    Directory.Delete(storePath, recursive: true);
Directory.CreateDirectory(storePath);

var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
{
    ClientSecrets = new ClientSecrets
    {
        ClientId = clientId,
        ClientSecret = clientSecret
    },
    Scopes = [GmailService.Scope.GmailModify],
    DataStore = new FileDataStore(storePath, fullPath: true),
    LoginHint = account,
    Prompt = "select_account consent"
});

Console.WriteLine($"A blank browser window will open. Sign in there as {account}.");
Console.WriteLine("That window is not your normal browser, so it is not signed in to the default account.");
var credential = await new AuthorizationCodeInstalledApp(flow, new IsolatedBrowserCodeReceiver())
    .AuthorizeAsync(account, CancellationToken.None);

var gmail = new GmailService(new BaseClientService.Initializer
{
    HttpClientInitializer = credential,
    ApplicationName = "Intellifer Hub"
});
var profile = await gmail.Users.GetProfile("me").ExecuteAsync();
var authorized = profile.EmailAddress?.Trim().ToLowerInvariant();
if (!string.Equals(authorized, account, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"Google authorized {authorized ?? "an unknown mailbox"} instead of {account}.");
    Console.Error.WriteLine("Run this again and choose the support mailbox on the account screen.");
    return 1;
}

var refreshToken = credential.Token.RefreshToken;
if (string.IsNullOrWhiteSpace(refreshToken))
{
    Console.Error.WriteLine("Google did not return a refresh token.");
    Console.Error.WriteLine($"Delete {storePath} and run this command again.");
    return 1;
}

Console.WriteLine($"Authorized mailbox: {profile.EmailAddress}");
Console.WriteLine("Leave GMAIL_MAILBOX=me. That means the mailbox that granted this token.");
Console.WriteLine("Put this in .env. It is a secret; do not commit it.");
Console.WriteLine("GMAIL_REFRESH_TOKEN=" + refreshToken);
return 0;

static string? Pick(string[] args, string flag, string name, IReadOnlyDictionary<string, string> file)
{
    var fromArgs = ReadArg(args, flag);
    if (IsReal(fromArgs))
        return fromArgs;
    if (file.TryGetValue(name, out var fromFile) && IsReal(fromFile))
        return fromFile;
    var fromEnv = Environment.GetEnvironmentVariable(name);
    return IsReal(fromEnv) ? fromEnv : null;
}

static bool IsReal(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return false;
    return !value.Contains("your-client", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("yourcompany", StringComparison.OrdinalIgnoreCase);
}

static Dictionary<string, string> LoadDotEnv()
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env")))
        directory = directory.Parent;
    if (directory is null)
        return values;

    foreach (var line in File.ReadLines(Path.Combine(directory.FullName, ".env")))
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#'))
            continue;
        var split = text.IndexOf('=');
        if (split <= 0)
            continue;
        values[text[..split].Trim()] = text[(split + 1)..].Trim().Trim('"');
    }

    return values;
}

static string? ReadArg(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[index + 1];
    }

    return null;
}

file sealed class IsolatedBrowserCodeReceiver : LocalServerCodeReceiver
{
    protected override bool OpenBrowser(string url)
    {
        var browser = FindBrowser();
        if (browser is null)
        {
            Console.Error.WriteLine("Chrome and Edge were not found. The normal browser will open instead.");
            return base.OpenBrowser(url);
        }

        var profile = Path.Combine(Path.GetTempPath(), "IntelliferHub", "gmail-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Console.WriteLine("Browser: " + browser);
        Process.Start(new ProcessStartInfo
        {
            FileName = browser,
            ArgumentList =
            {
                "--user-data-dir=" + profile,
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-sync",
                url
            },
            UseShellExecute = false
        });
        return true;
    }

    static string? FindBrowser()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
