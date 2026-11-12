using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EveCommandCenter.Services;

public static class JaniceAppraisalClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<JaniceSnapshot> LoadAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.Host.Equals("janice.e-351.com", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(uri.AbsolutePath, @"^/a/[A-Za-z0-9_-]+/?$"))
            throw new ArgumentException("Paste a Janice appraisal link such as https://janice.e-351.com/a/2coDfZ.");
        string code = uri.AbsolutePath.TrimEnd('/').Split('/').Last();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(new { id = 1, method = "Appraisal.get", @params = new { code } }), Encoding.UTF8), "~request~");
        using var response = await Http.PostAsync("https://janice.e-351.com/api/rpc/v1?m=Appraisal.get", form, token);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Janice could not load this appraisal.");
        return JaniceProfitability.Parse(result);
    }
}
