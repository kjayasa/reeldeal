using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace reeldeel_new.Cms;

/// <summary>Authorization that admits an authenticated user only if their email is still
/// in the Users table. Checked on every request, so removing a user takes effect
/// immediately (no need to wait for their cookie to expire).</summary>
public sealed class AdminUserRequirement : IAuthorizationRequirement;

public sealed class AdminUserHandler(CmsRepository repo) : AuthorizationHandler<AdminUserRequirement>
{
    public const string PolicyName = "Admin";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminUserRequirement requirement)
    {
        var email = context.User.FindFirstValue(ClaimTypes.Email);
        if (!string.IsNullOrEmpty(email) && repo.UserExists(email))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
