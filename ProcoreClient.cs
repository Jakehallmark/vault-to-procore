using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VaultTransfer;

internal sealed class ProcoreClient
{
    private const string CompanyId = "12233";
    private static readonly Uri TokenUrl = new("https://login.procore.com/oauth/token");

    public async Task SignIn(string appDirectory, IProgress<string> progress, CancellationToken cancel)
    {
        var (clientId, secret) = ReadCredentialsFile(Path.Combine(appDirectory, ".secrets"));
        progress.Report("Opening Procore sign-in. Approve it in the browser.");
        using var http = NewHttp();
        var session = await BrowserSignIn(http, clientId, secret, cancel);
        await Get(http, session, new Uri("https://api.procore.com/rest/v1.0/me"), cancel);
        SaveRefreshToken(session.RefreshToken);
    }

    public async Task<IReadOnlyList<ProcoreProjectRecord>> Scan(string appDirectory, IProgress<string> progress, CancellationToken cancel)
    {
        var (clientId, secret) = ReadCredentialsFile(Path.Combine(appDirectory, ".secrets"));
        using var http = NewHttp();
        var session = await OpenSession(http, clientId, secret, progress, cancel);
        progress.Report("Reading Procore projects.");
        using var list = JsonDocument.Parse(await Get(http, session, new Uri("https://api.procore.com/rest/v1.0/companies/" + CompanyId + "/projects"), cancel));
        if (list.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Procore did not return a project list.");
        var ids = list.RootElement.EnumerateArray().Select(item => RequiredId(item)).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new InvalidDataException("Procore returned the same project twice.");
        var projects = new List<ProcoreProjectRecord>(ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            cancel.ThrowIfCancellationRequested();
            progress.Report("Procore projects: " + (index + 1) + " / " + ids.Length);
            var id = ids[index];
            using var detail = JsonDocument.Parse(await Get(http, session, new Uri("https://api.procore.com/rest/v1.0/projects/" + id + "?company_id=" + CompanyId), cancel));
            projects.Add(ReadProject(CompanyId, id, detail.RootElement));
        }
        SaveRefreshToken(session.RefreshToken);
        return projects;
    }

    public static int SelfTest()
    {
        try
        {
            var credentials = ReadCredentials("#Sandbox\r\nClient ID: sandbox\r\nClient Secret: hidden\r\n#Production\r\nClient ID: app-id\r\nClient Secret: sec+ret\r\n");
            if (credentials.Id != "app-id" || credentials.Secret != "sec+ret") throw new Exception("Production credentials were not read.");
            using var detail = JsonDocument.Parse("{\"id\":\"2460697\",\"company_id\":\"12233\",\"project_number\":\"101097\",\"name\":\"FY23 WM 2151 Sunrise, FL\",\"active\":true}");
            var project = ReadProject("12233", "2460697", detail.RootElement);
            if (project.Number != "101097" || project.Name != "FY23 WM 2151 Sunrise, FL" || project.Active != true)
                throw new Exception("Procore project detail was not kept.");
            using var missing = JsonDocument.Parse("{\"id\":\"2460697\",\"project_number\":null,\"name\":\"Untitled\",\"active\":false}");
            var untitled = ReadProject("12233", "2460697", missing.RootElement);
            if (untitled.Number is not null || untitled.Active != false) throw new Exception("A missing project number was invented.");
            var refused = false;
            try
            {
                using var numbered = JsonDocument.Parse("{\"id\":\"1\",\"project_number\":101097,\"name\":\"Bad\"}");
                ReadProject("12233", "1", numbered.RootElement);
            }
            catch (InvalidDataException) { refused = true; }
            if (!refused) throw new Exception("A numeric project number was accepted.");
            Console.WriteLine("PASS Procore sign-in data stays inside the app");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL Procore sign-in data stays inside the app: " + error.Message);
            return 1;
        }
    }

    private async Task<Session> OpenSession(HttpClient http, string clientId, string secret, IProgress<string> progress, CancellationToken cancel)
    {
        var saved = ReadRefreshToken();
        if (saved is not null)
        {
            try { return await Refresh(http, clientId, secret, saved, cancel); }
            catch (InvalidDataException) { DeleteRefreshToken(); }
        }
        progress.Report("Opening Procore sign-in. Approve it in the browser.");
        return await BrowserSignIn(http, clientId, secret, cancel);
    }

    private async Task<Session> BrowserSignIn(HttpClient http, string clientId, string secret, CancellationToken cancel)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var url = "https://login.procore.com/oauth/authorize?response_type=code&client_id="
            + Uri.EscapeDataString(clientId) + "&redirect_uri=" + Uri.EscapeDataString("http://localhost") + "&state=" + state;
        var code = await ReceiveCode(url, state, cancel);
        var session = await Token(http, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["code"] = code,
            ["redirect_uri"] = "http://localhost"
        }, cancel);
        session.ClientId = clientId;
        session.Secret = secret;
        SaveRefreshToken(session.RefreshToken);
        return session;
    }

    private static async Task<Session> Refresh(HttpClient http, string clientId, string secret, string refreshToken, CancellationToken cancel)
    {
        var session = await Token(http, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["refresh_token"] = refreshToken,
            ["redirect_uri"] = "http://localhost"
        }, cancel);
        session.ClientId = clientId;
        session.Secret = secret;
        if (session.RefreshToken.Length == 0) session.RefreshToken = refreshToken;
        SaveRefreshToken(session.RefreshToken);
        return session;
    }

    private static async Task<Session> Token(HttpClient http, Dictionary<string, string> fields, CancellationToken cancel)
    {
        using var body = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(TokenUrl, body, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("Procore did not accept the sign-in.");
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var access = root.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidDataException("Procore did not return an access token.");
        var refresh = root.TryGetProperty("refresh_token", out var refreshValue) ? refreshValue.GetString() ?? "" : "";
        var lifetime = 3600d;
        if (root.TryGetProperty("expires_in", out var expires) && (!expires.TryGetDouble(out lifetime) || lifetime <= 0 || lifetime > 31536000))
            throw new InvalidDataException("Unexpected token expiry.");
        return new Session { AccessToken = access, RefreshToken = refresh, Expires = DateTimeOffset.UtcNow.AddSeconds(lifetime) };
    }

    private async Task<string> Get(HttpClient http, Session session, Uri url, CancellationToken cancel)
    {
        if (url.Scheme != "https" || url.Host != "api.procore.com" || url.Port != 443)
            throw new InvalidOperationException("Procore request was blocked.");
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            if (session.Expires <= DateTimeOffset.UtcNow.AddSeconds(60))
            {
                var renewed = await Refresh(http, session.ClientId, session.Secret, session.RefreshToken, cancel);
                session.AccessToken = renewed.AccessToken;
                session.RefreshToken = renewed.RefreshToken;
                session.Expires = renewed.Expires;
            }
            await Pace(session, cancel);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", session.AccessToken);
            request.Headers.TryAddWithoutValidation("Procore-Company-Id", CompanyId);
            using var response = await http.SendAsync(request, cancel);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                var renewed = await Refresh(http, session.ClientId, session.Secret, session.RefreshToken, cancel);
                session.AccessToken = renewed.AccessToken;
                session.RefreshToken = renewed.RefreshToken;
                session.Expires = renewed.Expires;
                continue;
            }
            if ((int)response.StatusCode is 408 or 429 or 500 or 502 or 503 or 504)
            {
                session.NextAllowed = DateTimeOffset.UtcNow.AddSeconds(Delay(response, attempt));
                if (attempt == 5) throw new InvalidDataException("Procore remained unavailable. Scan again later.");
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("Procore returned HTTP " + (int)response.StatusCode + ".");
            session.NextAllowed = DateTimeOffset.UtcNow.AddSeconds(2.5);
            return await response.Content.ReadAsStringAsync(cancel);
        }
        throw new InvalidDataException("Procore remained unavailable. Scan again later.");
    }

    private static async Task Pace(Session session, CancellationToken cancel)
    {
        var wait = session.NextAllowed - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel);
    }

    private static double Delay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return Math.Max(2.5, delta.TotalSeconds + 3);
        return Math.Min(300, 30 * Math.Pow(2, attempt)) + 3;
    }

    private static async Task<string> ReceiveCode(string authUrl, string state, CancellationToken cancel)
    {
        var listeners = new List<TcpListener>();
        try
        {
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                var listener = new TcpListener(address, 80);
                listener.Server.ExclusiveAddressUse = true;
                try { listener.Start(); listeners.Add(listener); }
                catch
                {
                    listener.Stop();
                    throw new InvalidDataException("Cannot listen on localhost port 80. Close the program using it, then sign in again.");
                }
            }
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancel.ThrowIfCancellationRequested();
                foreach (var listener in listeners)
                {
                    if (!listener.Pending()) continue;
                    using var client = await listener.AcceptTcpClientAsync(cancel);
                    var code = await ReadCallback(client, state, cancel);
                    if (code is not null) return code;
                }
                await Task.Delay(100, cancel);
            }
            throw new InvalidDataException("Procore sign-in timed out. Sign in again.");
        }
        finally
        {
            foreach (var listener in listeners) listener.Stop();
        }
    }

    private static async Task<string?> ReadCallback(TcpClient client, string state, CancellationToken cancel)
    {
        using var stream = client.GetStream();
        stream.ReadTimeout = 2000;
        var bytes = new List<byte>();
        var one = new byte[1];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (bytes.Count < 16384 && DateTimeOffset.UtcNow < deadline)
        {
            if (await stream.ReadAsync(one, cancel) == 0) break;
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
        }
        var request = Encoding.ASCII.GetString(bytes.ToArray());
        string? code = null;
        var declined = false;
        var first = request.Split("\r\n", 2)[0];
        if (first.StartsWith("GET /?", StringComparison.Ordinal) && first.Contains(" HTTP/1.", StringComparison.Ordinal)
            && request.Contains("Host: localhost", StringComparison.OrdinalIgnoreCase))
        {
            var target = first.Split(' ')[1];
            try { code = AuthorizationCode("http://localhost" + target, state); }
            catch (InvalidDataException error) { declined = error.Message.Contains("declined", StringComparison.Ordinal); }
        }
        var message = code is null ? "This request was not accepted. Return to the Procore sign-in tab." : "Sign-in received. You can close this tab and return to Vault Transfer.";
        var payload = Encoding.UTF8.GetBytes(message);
        var status = code is null ? "400 Bad Request" : "200 OK";
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + payload.Length + "\r\n\r\n");
        await stream.WriteAsync(header, cancel);
        await stream.WriteAsync(payload, cancel);
        if (declined) throw new InvalidDataException("Procore authorization was declined.");
        return code;
    }

    private static string AuthorizationCode(string callback, string expectedState)
    {
        if (!Uri.TryCreate(callback, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "localhost" || uri.Port != 80 || uri.AbsolutePath != "/")
            throw new InvalidDataException("The Procore callback address was not accepted.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
        if (!query.TryGetValue("state", out var state) || state != expectedState)
            throw new InvalidDataException("Procore sign-in state did not match. Sign in again.");
        if (query.ContainsKey("error")) throw new InvalidDataException("Procore authorization was declined.");
        if (!query.TryGetValue("code", out var code) || code.Length == 0) throw new InvalidDataException("The Procore callback contains no authorization code.");
        return code;
    }

    private static (string Id, string Secret) ReadCredentialsFile(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("The Procore credentials file .secrets was not found next to Vault Transfer.");
        return ReadCredentials(File.ReadAllText(path));
    }

    private static (string Id, string Secret) ReadCredentials(string text)
    {
        var production = false;
        string? id = null;
        string? secret = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                production = line.Equals("#Production", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!production) continue;
            var split = line.Split(':', 2);
            if (split.Length != 2) throw new InvalidDataException("Invalid production credential entry. Expected Client ID: or Client Secret:.");
            var key = split[0].Trim();
            var value = split[1].Trim();
            if (key == "Client ID")
            {
                if (id is not null) throw new InvalidDataException("Duplicate production credential entry.");
                id = value;
            }
            else if (key == "Client Secret")
            {
                if (secret is not null) throw new InvalidDataException("Duplicate production credential entry.");
                secret = value;
            }
            else throw new InvalidDataException("Invalid production credential entry. Expected Client ID: or Client Secret:.");
        }
        if (id is null || secret is null) throw new InvalidDataException("The #Production section must contain Client ID: and Client Secret:.");
        return (id, secret);
    }

    private static ProcoreProjectRecord ReadProject(string companyId, string projectId, JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("id", out var id) || id.ToString() != projectId)
            throw new InvalidDataException("Project detail ID did not match the requested project.");
        if (detail.TryGetProperty("company_id", out var company) && company.ToString() != companyId)
            throw new InvalidDataException("Project detail company did not match the requested company.");
        string? number = null;
        if (detail.TryGetProperty("project_number", out var projectNumber) && projectNumber.ValueKind != JsonValueKind.Null)
        {
            if (projectNumber.ValueKind != JsonValueKind.String) throw new InvalidDataException("Detailed project number was not text.");
            number = projectNumber.GetString();
        }
        bool? active = null;
        if (detail.TryGetProperty("active", out var activeValue) && activeValue.ValueKind != JsonValueKind.Null)
        {
            if (activeValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new InvalidDataException("Detailed active status was not boolean.");
            active = activeValue.GetBoolean();
        }
        var name = detail.TryGetProperty("name", out var nameValue) ? nameValue.ToString() : "";
        return new ProcoreProjectRecord(companyId, projectId, number, name, active);
    }

    private static string RequiredId(JsonElement project)
    {
        if (!project.TryGetProperty("id", out var id)) throw new InvalidDataException("Project response is missing a valid ID.");
        var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
        if (text is null || text.Length == 0 || text.Any(character => !char.IsAsciiDigit(character)))
            throw new InvalidDataException("Project response is missing a valid ID.");
        return text;
    }

    private static string TokenPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultTransfer", "procore.refresh");

    private static void SaveRefreshToken(string token)
    {
        if (token.Length == 0) throw new InvalidDataException("Procore did not return a refresh token, so the next scan cannot sign in on its own.");
        var folder = Path.GetDirectoryName(TokenPath())!;
        Directory.CreateDirectory(folder);
        var plain = Encoding.UTF8.GetBytes(token);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            var temp = TokenPath() + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, TokenPath(), true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static string? ReadRefreshToken()
    {
        var path = TokenPath();
        if (!File.Exists(path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try { var token = Encoding.UTF8.GetString(plain).Trim(); return token.Length == 0 ? null : token; }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException) { return null; }
    }

    private static void DeleteRefreshToken()
    {
        var path = TokenPath();
        if (File.Exists(path)) File.Delete(path);
    }

    private static HttpClient NewHttp()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private sealed class Session
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string Secret { get; set; } = "";
        public DateTimeOffset Expires { get; set; }
        public DateTimeOffset NextAllowed { get; set; }
    }
}
