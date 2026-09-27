using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VaultTransfer;

internal readonly record struct ProcoreScanResult(int Read, int Unchanged);

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

    public async Task<ProcoreScanResult> Scan(string appDirectory, Func<string, string?, bool, string?, string?, bool?, bool> unchanged, Action<ProcoreProjectRecord> save, IProgress<string> progress, CancellationToken cancel, bool interactive = true, IReadOnlySet<string>? knownIds = null)
    {
        var (clientId, secret) = ReadCredentialsFile(Path.Combine(appDirectory, ".secrets"));
        using var http = NewHttp();
        var session = await OpenSession(http, clientId, secret, progress, cancel, interactive);
        progress.Report("Reading the Procore project list.");
        using var list = JsonDocument.Parse(await Get(http, session, new Uri("https://api.procore.com/rest/v1.0/companies/" + CompanyId + "/projects"), cancel));
        if (list.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Procore did not return a project list.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var read = 0;
        var skipped = 0;
        foreach (var item in list.RootElement.EnumerateArray().OrderBy(item => knownIds?.Contains(RequiredId(item)) == true ? 1 : 0))
        {
            cancel.ThrowIfCancellationRequested();
            var id = RequiredId(item);
            if (!seen.Add(id)) throw new InvalidDataException("Procore returned the same project twice.");
            progress.Report("Checking Procore project " + seen.Count + " of " + list.RootElement.GetArrayLength() + ". " + skipped + " cached.");
            var view = ReadListView(item);
            if (unchanged(id, view.UpdatedAt, view.Comparable, view.Number, view.Name, view.Active))
            {
                skipped++;
                continue;
            }
            ProcoreProjectRecord project;
            if (view.Comparable)
                project = new ProcoreProjectRecord(CompanyId, id, view.Number, view.Name ?? "", view.Active, view.UpdatedAt);
            else
            {
                using var detail = JsonDocument.Parse(await Get(http, session, new Uri("https://api.procore.com/rest/v1.0/projects/" + id + "?company_id=" + CompanyId), cancel));
                project = ReadProject(CompanyId, id, detail.RootElement);
                if (string.IsNullOrEmpty(project.UpdatedAt)) project = project with { UpdatedAt = view.UpdatedAt };
            }
            save(project);
            read++;
            if (read % 25 == 0) progress.Report("Saved " + read + " changed projects. " + skipped + " unchanged.");
        }
        progress.Report(skipped + " projects cached or unchanged. " + read + " read.");
        SaveRefreshToken(session.RefreshToken);
        return new ProcoreScanResult(read, skipped);
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
            using var listed = JsonDocument.Parse("{\"id\":2460697,\"project_number\":\"101097\",\"name\":\"FY23 WM 2151 Sunrise, FL\",\"active\":true,\"updated_at\":\"2026-09-01T00:00:00Z\"}");
            var fromList = ReadListView(listed.RootElement);
            if (!fromList.Comparable || fromList.Number != "101097" || fromList.UpdatedAt != "2026-09-01T00:00:00Z")
                throw new Exception("A complete project list row still required a separate read.");
            if (AuthorizationCode("http://localhost/?state=expected&code=ok", "expected") != "ok") throw new Exception("Callback rejected.");
            foreach (var callback in new[] { "http://localhost/?state=wrong&code=ok", "http://localhost/?state=expected&state=wrong&code=ok", "http://localhost/?state=expected&error=access_denied" })
            {
                var rejected = false;
                try { AuthorizationCode(callback, "expected"); } catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception("Invalid callback was accepted.");
            }
            using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests))
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
                if (Delay(response, 0) < 3600) throw new Exception("Retry-After was ignored.");
            }
            using (var response = new HttpResponseMessage(HttpStatusCode.OK))
            {
                response.Headers.Add("X-Rate-Limit-Remaining", "0");
                response.Headers.Add("X-Rate-Limit-Reset", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString());
                if (RateDelay(response) < 290) throw new Exception("Exhausted rate limit was ignored.");
            }
            using (var http = new HttpClient(new TestHandler(HttpStatusCode.ServiceUnavailable, "{}")))
            {
                try { Token(http, [], CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Token outage was accepted."); }
                catch (InvalidDataException) { } // Outages must never be classified as expired authorization.
            }
            using (var http = new HttpClient(new TestHandler(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")))
            {
                try { Token(http, [], CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Expired authorization was accepted."); }
                catch (SignInExpiredException) { }
            }
            Console.WriteLine("PASS Procore sign-in data stays inside the app");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL Procore sign-in data stays inside the app: " + error.Message);
            return 1;
        }
    }

    private async Task<Session> OpenSession(HttpClient http, string clientId, string secret, IProgress<string> progress, CancellationToken cancel, bool interactive)
    {
        var saved = ReadRefreshToken();
        if (saved is not null)
        {
            try { return await Refresh(http, clientId, secret, saved, cancel); }
            catch (SignInExpiredException) { DeleteRefreshToken(); }
        }
        if (!interactive) throw new InvalidDataException("Procore sign-in is required. Open Settings and choose Sign in to Procore.");
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
        if (!response.IsSuccessStatusCode)
        {
            string? reason = null;
            try { using var failure = JsonDocument.Parse(text); reason = failure.RootElement.GetProperty("error").GetString(); }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (reason == "invalid_grant") throw new SignInExpiredException();
            throw new InvalidDataException("Procore token request returned HTTP " + (int)response.StatusCode
                + ". Saved sign-in was retained. Check the connection and production app credentials.");
        }
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
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        cancel = budget.Token;
        for (var attempt = 0; attempt <= 2; attempt++)
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
                if (attempt == 2 || Delay(response, attempt) > 45)
                    throw new InvalidDataException("Procore HTTP " + (int)response.StatusCode + " at " + url.AbsolutePath
                        + ". Retry later; saved sign-in and project details were retained.");
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidDataException("Procore returned HTTP " + (int)response.StatusCode + ".");
            session.NextAllowed = DateTimeOffset.UtcNow.AddSeconds(RateDelay(response));
            return await response.Content.ReadAsStringAsync(cancel);
        }
        throw new InvalidDataException("Procore remained unavailable. Scan again later.");
    }

    private static double RateDelay(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Rate-Limit-Remaining", out var remainingValues)
            && double.TryParse(remainingValues.FirstOrDefault(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var remaining)
            && response.Headers.TryGetValues("X-Rate-Limit-Reset", out var resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), out var reset) && reset is > 0 and < 253402300800)
        {
            var seconds = Math.Max(0, (DateTimeOffset.FromUnixTimeSeconds(reset) - (response.Headers.Date ?? DateTimeOffset.UtcNow)).TotalSeconds) + 1;
            return remaining <= 0 ? seconds : Math.Max(0.5, seconds / remaining);
        }
        return 2.5; // Conservative fallback when the server omits its limits.
    }

    private static async Task Pace(Session session, CancellationToken cancel)
    {
        var wait = session.NextAllowed - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.FromSeconds(45)) throw new InvalidDataException("Procore rate limit reached. Retry after " + session.NextAllowed.ToLocalTime().ToString("h:mm:ss tt") + ". Cached progress is saved.");
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel);
    }

    private static double Delay(HttpResponseMessage response, int attempt)
    {
        var rateDelay = RateDelay(response);
        if (response.Headers.RetryAfter?.Delta is { } delta) return Math.Max(rateDelay, delta.TotalSeconds + 3);
        if (response.Headers.RetryAfter?.Date is { } date) return Math.Max(rateDelay, (date - (response.Headers.Date ?? DateTimeOffset.UtcNow)).TotalSeconds + 3);
        if (response.StatusCode == HttpStatusCode.TooManyRequests && rateDelay > 2.5) return rateDelay;
        return Math.Min(30, 3 * Math.Pow(2, attempt));
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
                    try
                    {
                        var code = await ReadCallback(client, state, cancel);
                        if (code is not null) return code;
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                    catch (IOException) { }
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        cancel = timeout.Token;
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
            .GroupBy(pair => Uri.UnescapeDataString(pair[0]), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count() == 1 ? Uri.UnescapeDataString(group.First()[1]) : "", StringComparer.Ordinal);
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
        string? updated = null;
        if (detail.TryGetProperty("updated_at", out var updatedValue) && updatedValue.ValueKind == JsonValueKind.String)
            updated = updatedValue.GetString();
        return new ProcoreProjectRecord(companyId, projectId, number, name, active, updated);
    }

    private readonly record struct ListView(string? UpdatedAt, bool Comparable, string? Number, string? Name, bool? Active);

    private static ListView ReadListView(JsonElement item)
    {
        string? updated = null;
        if (item.TryGetProperty("updated_at", out var updatedValue) && updatedValue.ValueKind == JsonValueKind.String)
            updated = updatedValue.GetString();
        if (!item.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("project_number", out var numberValue)
            || numberValue.ValueKind is not JsonValueKind.String and not JsonValueKind.Null
            || !item.TryGetProperty("active", out var activeValue)
            || activeValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False and not JsonValueKind.Null)
            return new ListView(updated, false, null, null, null);
        bool? active = activeValue.ValueKind == JsonValueKind.Null ? null : activeValue.GetBoolean();
        var number = numberValue.ValueKind == JsonValueKind.String ? numberValue.GetString() : null;
        return new ListView(updated, true, number, nameValue.GetString() ?? "", active);
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

    private sealed class TestHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class SignInExpiredException : Exception
    {
        public SignInExpiredException() : base("Procore authorization expired. Sign in again from Settings.") { }
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
