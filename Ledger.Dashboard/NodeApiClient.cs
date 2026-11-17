using System.Text.Json;
using System.Text.Json.Serialization;
using Skyframe.Ledger.Core.Models;
using System.Net.Http.Json;

namespace Ledger.Dashboard;

public sealed class NodeApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    public int NodePort { get; set; } = 5001;
    public Uri BaseAddress => new($"http://127.0.0.1:{NodePort + 1000}/");

    public async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken token = default)
    {
        try { using var response = await _http.GetAsync(new Uri(BaseAddress, path), token); response.EnsureSuccessStatusCode(); await using var stream = await response.Content.ReadAsStreamAsync(token); return await JsonDocument.ParseAsync(stream, cancellationToken: token); }
        catch (HttpRequestException) { return null; } catch (TaskCanceledException) { return null; }
    }
    public async Task<List<EscrowRecord>> GetEscrowsAsync(CancellationToken token = default)
    {
        using var doc = await GetJsonAsync("escrows", token); if (doc?.RootElement.ValueKind != JsonValueKind.Array) return [];
        try { return JsonSerializer.Deserialize<List<EscrowRecord>>(doc.RootElement.GetRawText(), JsonOptions) ?? []; } catch (JsonException) { return []; }
    }
    public async Task<(bool Success, string Message)> PostAsync(string route, object? payload = null, CancellationToken token = default)
    {
        try
        {
            HttpContent content = payload is null ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(payload, options: JsonOptions);
            using (content)
            {
            using var response = await _http.PostAsync(new Uri(BaseAddress, route), content, token);
            if (response.IsSuccessStatusCode) return (true, "Request accepted.");
            var text = await response.Content.ReadAsStringAsync(token);
            try { using var error = JsonDocument.Parse(text); return (false, error.RootElement.TryGetProperty("error", out var message) ? message.GetString() ?? response.ReasonPhrase ?? "Request failed." : response.ReasonPhrase ?? "Request failed."); }
            catch { return (false, response.ReasonPhrase ?? "Request failed."); }
            }
        }
        catch (HttpRequestException) { return (false, $"Ledger.Node is not reachable at 127.0.0.1:{NodePort + 1000}."); }
        catch (TaskCanceledException) { return (false, "Ledger.Node did not respond in time."); }
    }
    public void Dispose() => _http.Dispose();
}
