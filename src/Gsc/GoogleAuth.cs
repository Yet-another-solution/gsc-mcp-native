using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gsc;

/// <summary>
/// Access tokens from a Google credentials JSON file: a service account key, or an
/// authorized-user file holding a refresh token (gcloud ADC, mcp-gsc's token.json).
/// </summary>
public sealed class GoogleAuth(HttpClient http, GoogleCredentials credentials, string scope)
{
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/webmasters.readonly";
    public const string ReadWriteScope = "https://www.googleapis.com/auth/webmasters";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public static GoogleAuth FromFile(HttpClient http, string path, string scope = ReadOnlyScope) =>
        new(http, JsonSerializer.Deserialize(File.ReadAllBytes(path), GoogleAuthJson.Default.GoogleCredentials)
            ?? throw new InvalidDataException($"{path} is not a credentials JSON file"), scope);

    /// <summary>
    /// One-off browser login for a Google "Desktop app" OAuth client. Listens on the loopback
    /// <paramref name="port"/> for Google's redirect and returns credentials holding a refresh token.
    /// </summary>
    /// <param name="showUrl">Receives the URL the user has to open in a browser.</param>
    public static async Task<GoogleCredentials> LoginAsync(
        HttpClient http, string clientSecretsPath, string scope, int port, Action<string> showUrl, CancellationToken ct = default)
    {
        var client = JsonSerializer.Deserialize(File.ReadAllBytes(clientSecretsPath), GoogleAuthJson.Default.ClientSecretsFile)?.Installed
            ?? throw new InvalidDataException($"{clientSecretsPath} is not the client secrets JSON of a 'Desktop app' OAuth client");

        var redirect = $"http://127.0.0.1:{port}/";
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));

        using var listener = new HttpListener();
        // Inside a container the published port arrives on the container's own interface, not its loopback.
        var host = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? "+" : "127.0.0.1";
        listener.Prefixes.Add($"http://{host}:{port}/");
        listener.Start();

        showUrl(client.AuthUri + "?" + await new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline", // offline + consent is what makes Google return a refresh token
            ["prompt"] = "consent",
        }).ReadAsStringAsync(ct));

        string? code = null;
        while (code is null)
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);
            var query = context.Request.QueryString;
            var error = query["error"];
            // Anything without our state (favicon requests, strays) is answered and ignored.
            if (query["state"] == state) code = query["code"];
            var message = code is not null ? "Signed in. You can close this tab." : error ?? "Not the request this login is waiting for.";
            context.Response.StatusCode = code is not null ? 200 : 400;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(message), ct);
            context.Response.Close();
            if (code is null && error is not null && query["state"] == state)
                throw new InvalidOperationException($"Google sign-in failed: {error}");
        }

        using var response = await http.PostAsync(client.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect,
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
        }), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Google token request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");

        var token = await response.Content.ReadFromJsonAsync(GoogleAuthJson.Default.TokenResponse, ct);
        return new GoogleCredentials(
            RefreshToken: token?.RefreshToken ?? throw new InvalidDataException("Google returned no refresh token"),
            ClientId: client.ClientId, ClientSecret: client.ClientSecret, TokenUri: client.TokenUri);
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expires) return _token;
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expires) return _token;

            var form = credentials switch
            {
                { PrivateKey: not null, ClientEmail: not null } => new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = SignJwt(),
                },
                { RefreshToken: not null, ClientId: not null, ClientSecret: not null } => new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credentials.RefreshToken,
                    ["client_id"] = credentials.ClientId,
                    ["client_secret"] = credentials.ClientSecret,
                },
                _ => throw new InvalidDataException(
                    "Credentials need either private_key + client_email (service account) " +
                    "or refresh_token + client_id + client_secret (OAuth user)."),
            };

            using var response = await http.PostAsync(credentials.TokenUri, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Google token request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");

            var token = await response.Content.ReadFromJsonAsync(GoogleAuthJson.Default.TokenResponse, ct)
                ?? throw new InvalidDataException("Empty token response");
            _expires = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 60);
            return _token = token.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string SignJwt()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new JwtClaims(credentials.ClientEmail!, scope, credentials.TokenUri, now, now + 3600);
        var unsigned = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8)
            + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims, GoogleAuthJson.Default.JwtClaims));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(credentials.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url(signature);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record GoogleCredentials(
    string? ClientEmail = null,
    string? PrivateKey = null,
    string? RefreshToken = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string TokenUri = "https://oauth2.googleapis.com/token")
{
    /// <summary>Writes the file <see cref="GoogleAuth.FromFile"/> reads.</summary>
    public void Save(string path) =>
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, GoogleAuthJson.Default.GoogleCredentials));
}

internal sealed record ClientSecretsFile(ClientSecrets? Installed);

internal sealed record ClientSecrets(
    string ClientId,
    string ClientSecret,
    string AuthUri = "https://accounts.google.com/o/oauth2/auth",
    string TokenUri = "https://oauth2.googleapis.com/token");

internal sealed record TokenResponse(string AccessToken, int ExpiresIn, string? RefreshToken = null);

internal sealed record JwtClaims(string Iss, string Scope, string Aud, long Iat, long Exp);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GoogleCredentials))]
[JsonSerializable(typeof(ClientSecretsFile))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(JwtClaims))]
internal sealed partial class GoogleAuthJson : JsonSerializerContext;
