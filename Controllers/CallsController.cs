using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CallsController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;

    public CallsController(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet("turn-credentials")]
    public async Task<IActionResult> GetTurnCredentials()
    {
        var domain = _config["Metered:Domain"];
        var secretKey = _config["Metered:ApiKey"];

        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(secretKey))
            return StatusCode(503, "TURN server not configured");

        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://{domain}/api/v1/turn/credentials?apiKey={secretKey}";
            var httpResponse = await client.GetAsync(url);
            var body = await httpResponse.Content.ReadAsStringAsync();
            if (!httpResponse.IsSuccessStatusCode)
                return StatusCode(502, $"Metered API error {(int)httpResponse.StatusCode}: {body}");
            return Content(body, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"Failed to fetch TURN credentials: {ex.Message}");
        }
    }
}
