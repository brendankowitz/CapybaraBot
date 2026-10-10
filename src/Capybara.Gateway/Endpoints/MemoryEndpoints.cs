using Capybara.Client;
using Capybara.Memory.Access;
using Capybara.Memory.Admin;
using Capybara.Memory.Export;
using Capybara.Memory.Paths;
using Capybara.Memory.Store;
using Microsoft.AspNetCore.Http.Features;

namespace Capybara.Gateway.Endpoints;

/// <summary>
/// The user's view of memory (FR-MEM-12): browse, read, edit and delete any page, review staged changes, export the
/// wiki. Every operation goes through <see cref="MemoryAdmin"/> as <see cref="MemoryAdminPrincipal.Human"/>, which
/// only these authenticated-device handlers use.
/// </summary>
public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder api)
    {
        var memory = api.MapGroup("/memory").WithTags("Memory");

        memory.MapGet("/pages", (string? prefix, MemoryAdmin admin) =>
            TypedResults.Ok(admin.ListPages(MemoryAdminPrincipal.Human, prefix).Select(p => new MemoryPageInfoDto(p.Path, p.Size, p.UpdatedAt)).ToList()));

        memory.MapGet("/page", async (string? path, MemoryAdmin admin, CancellationToken cancellationToken) =>
        {
            if (!MemoryPath.TryParse(path, out var parsed, out var error))
            {
                return Results.BadRequest(error);
            }

            try
            {
                return await admin.ReadPageAsync(MemoryAdminPrincipal.Human, parsed.Value, cancellationToken) is { } content
                    ? Results.Ok(new MemoryPageDto(parsed.Value, content))
                    : Results.NotFound();
            }
            catch (MemoryAccessDeniedException ex)
            {
                // A path the store refuses (symlinks, escapes).
                return Results.BadRequest(ex.Message);
            }
        });

        memory.MapPut("/page", async (SaveMemoryPageDto request, MemoryAdmin admin, CancellationToken cancellationToken) =>
        {
            if (!MemoryPath.TryParse(request.Path, out var parsed, out var error))
            {
                return Results.BadRequest(error);
            }

            if (request.Content is null)
            {
                return Results.BadRequest("content is required");
            }

            try
            {
                await admin.SavePageAsync(MemoryAdminPrincipal.Human, parsed.Value, request.Content, cancellationToken);
                return Results.NoContent();
            }
            catch (MemoryAccessDeniedException ex)
            {
                // Over the size cap, or a path the store refuses (symlinks, escapes).
                return Results.BadRequest(ex.Message);
            }
        });

        memory.MapDelete("/page", async (string? path, MemoryAdmin admin, CancellationToken cancellationToken) =>
        {
            if (!MemoryPath.TryParse(path, out var parsed, out var error))
            {
                return Results.BadRequest(error);
            }

            try
            {
                return await admin.DeletePageAsync(MemoryAdminPrincipal.Human, parsed.Value, cancellationToken) ? Results.NoContent() : Results.NotFound();
            }
            catch (MemoryAccessDeniedException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        memory.MapGet("/pending", async (MemoryAdmin admin) =>
            TypedResults.Ok((await admin.ListPendingAsync(MemoryAdminPrincipal.Human))
                .Select(c => new PendingMemoryChangeDto(c.Id, c.Path, c.ProposedBy, c.Before, c.After, c.At, c.Kind.ToString(), c.Reason))
                .ToList()));

        memory.MapPost("/pending/{id}", async (string id, PendingChangeDecisionDto request, MemoryAdmin admin, CancellationToken cancellationToken) =>
            await admin.ResolveChangeAsync(MemoryAdminPrincipal.Human, id, request.Apply, cancellationToken) switch
            {
                PendingChangeResult.NotFound => Results.NotFound(),
                PendingChangeResult.Stale => Results.Conflict("the page changed since the proposal; it was discarded"),
                _ => Results.NoContent(),
            });

        // Streamed straight to the response: the wiki is never buffered in memory.
        memory.MapGet("/export", async (HttpContext context, MemoryExporter exporter, CancellationToken cancellationToken) =>
        {
            // ZipArchive writes entry headers and its central directory synchronously; allow that for this response only.
            if (context.Features.Get<IHttpBodyControlFeature>() is { } body)
            {
                body.AllowSynchronousIO = true;
            }

            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = "attachment; filename=capybara-memory.zip";
            context.Response.Headers.CacheControl = "no-store";
            await exporter.ExportZipAsync(context.Response.Body, cancellationToken);
        });

        return api;
    }
}
