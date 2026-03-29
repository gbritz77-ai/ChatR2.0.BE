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
        var secretKey = _config["Metered:SecretKey"];

        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(secretKey))
            return StatusCode(503, "TURN server not configured");

        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://{domain}/api/v1/turn/credentials?apiKey={secretKey}";
            var response = await client.GetStringAsync(url);
            return Content(response, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"Failed to fetch TURN credentials: {ex.Message}");
        }
    }
}
