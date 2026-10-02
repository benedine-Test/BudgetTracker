using System.Net;
using Microsoft.JSInterop;

namespace Budget.Web.Services;

/// <summary>
/// The secret key, remembered on this phone only (browser storage).
/// If storage is unavailable (private browsing), the key simply lasts until the app is closed.
/// </summary>
public class KeyStore(IJSRuntime js)
{
    public string? Key { get; private set; }
    public bool Loaded { get; private set; }
    public event Action? Changed;

    public async Task LoadAsync()
    {
        Key = await js.InvokeAsync<string?>("budget.getKey");
        Loaded = true;
        Changed?.Invoke();
    }

    public async Task SetAsync(string key)
    {
        Key = key;
        await js.InvokeVoidAsync("budget.setKey", key);
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        Key = null;
        await js.InvokeVoidAsync("budget.setKey", null);
        Changed?.Invoke();
    }
}

/// <summary>Adds the key to every request, and forgets it if the server rejects it.</summary>
public class ApiKeyHandler(KeyStore keys) : DelegatingHandler
{
    public const string Header = "X-Api-Key";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var usedSavedKey = false;
        if (!request.Headers.Contains(Header) && keys.Key is { Length: > 0 } key)
        {
            request.Headers.TryAddWithoutValidation(Header, key);
            usedSavedKey = true;
        }

        var response = await base.SendAsync(request, ct);
        if (usedSavedKey && response.StatusCode == HttpStatusCode.Unauthorized)
            await keys.ClearAsync();
        return response;
    }
}

/// <summary>One short message at the bottom of the screen.</summary>
public class Toast
{
    public string? Message { get; private set; }
    public bool IsError { get; private set; }
    public event Action? Changed;
    private int _version;

    public void Show(string message, bool isError = false)
    {
        Message = message;
        IsError = isError;
        var mine = ++_version;
        Changed?.Invoke();
        _ = HideLater(mine, isError ? 6000 : 3500);
    }

    public void Error(string message) => Show(message, true);

    private async Task HideLater(int version, int ms)
    {
        await Task.Delay(ms);
        if (version != _version) return;
        Message = null;
        Changed?.Invoke();
    }
}
