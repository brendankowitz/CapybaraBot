using Capybara.Client;
using Capybara.Pwa;
using Capybara.Pwa.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<BrowserTokenHandler>();
builder.Services.AddScoped(sp => new HttpClient(sp.GetRequiredService<BrowserTokenHandler>())
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddScoped<CapybaraApiClient>();
builder.Services.AddScoped<CapybaraRealtimeService>();

await builder.Build().RunAsync();
