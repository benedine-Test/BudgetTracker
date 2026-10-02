using Budget.Web;
using Budget.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<KeyStore>();
builder.Services.AddScoped<Toast>();
builder.Services.AddScoped(sp =>
{
    var handler = new ApiKeyHandler(sp.GetRequiredService<KeyStore>()) { InnerHandler = new HttpClientHandler() };
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});
builder.Services.AddScoped<Api>();

await builder.Build().RunAsync();
