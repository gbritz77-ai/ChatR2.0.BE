using System.Security.Claims;

namespace Chat.Api.Auth
{
    public static class ClaimsPrincipalExtensions
    {
        public static Guid GetUserId(this ClaimsPrincipal user)
        {
            var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? throw new InvalidOperationException("User id claim missing");
            return Guid.Parse(id);
        }
    }
}
