using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PmpStudy.Web.Data;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    /// <summary>Seul point d'entrée hors pages : la déconnexion (formulaire POST protégé par antiforgery).</summary>
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var accountGroup = endpoints.MapGroup("/Account");
        accountGroup.MapPost("/Logout", async (
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect("~/Account/Login");
        });
        return accountGroup;
    }
}
