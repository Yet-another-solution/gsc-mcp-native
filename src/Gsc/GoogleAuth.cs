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
    string TokenUri = "https://oauth2.googleapis.com/token");

internal sealed record TokenResponse(string AccessToken, int ExpiresIn);

internal sealed record JwtClaims(string Iss, string Scope, string Aud, long Iat, long Exp);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GoogleCredentials))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(JwtClaims))]
internal sealed partial class GoogleAuthJson : JsonSerializerContext;
